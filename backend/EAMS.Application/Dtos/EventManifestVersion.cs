using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EAMS.Domain;

namespace EAMS.Application.Dtos;

/// <summary>
/// The <c>ETag</c> / <see cref="EventManifestDto.Version"/> of one manifest (D-46), and the lenient
/// <c>If-None-Match</c> comparison that goes with it.
///
/// <code>
/// ETag = W/"m1." + base64url(SHA-256(canonical bytes))[..22]
/// </code>
///
/// <para>
/// The canonical bytes are a deterministic serialization of the <b>entire published content</b> —
/// <c>event</c>, <c>groups</c>, <c>attendees</c> — with <c>serverTime</c> and <c>version</c> excluded,
/// under fixed ordering: groups by <c>studentGroupId</c>, attendees by <c>studentId</c>, each
/// <c>groupIds</c> and <c>cardUids</c> ordinal, invariant culture, round-trip (<c>"O"</c>) date-times,
/// no whitespace. The ordering is applied when the manifest is built, so the body a client receives is
/// already in canonical order and this class only has to hash it.
/// </para>
///
/// <para>
/// <b>The exclusion is structural, not a re-listing of what to include.</b> The whole assembled
/// <see cref="EventManifestDto"/> is hashed and <see cref="ExcludedFromTheHash"/> drops exactly two
/// properties from it, so a field added to the DTO is inside the hash by default and leaving it out is
/// what would take a deliberate edit. Listing the included fields instead would read as equivalent and
/// would let a new field escape the hash in silence — see <see cref="ExcludedFromTheHash"/>.
/// </para>
///
/// <para>
/// <b>1. A content hash, not a watermark.</b> The cheap alternative — <c>max(UpdatedAt)</c> plus row
/// counts — produces silent staleness <em>by construction</em>, and deletes are the hole:
/// <c>max(UpdatedAt)</c> never decreases, and removing a student from a section deletes the row that
/// carried the timestamp. The same goes for bulk SQL paths that skip <c>UpdatedAt</c> (the SIS import
/// is exactly that shape) and for a card deactivation, which touches <c>RfidCards</c> and never
/// <c>Students</c>. A hash over the bytes we are about to publish cannot miss a change, because the
/// thing that changed <em>is</em> the thing being hashed. Its only cost is spurious churn from
/// incidental reordering — removed by the fixed ordering, and failing in the safe direction anyway: a
/// needless 200, never a wrongly-withheld one.
/// </para>
///
/// <para>
/// <b>2. A cryptographic hash over bytes. NEVER <see cref="object.GetHashCode"/> or a composed
/// <see cref="HashCode"/>.</b> .NET randomizes string hashing per process, so a <c>GetHashCode</c>-derived
/// version changes on every restart — every device re-downloads after every deploy — and two instances
/// never agree, so a load-balanced device 304s only by luck. <b>It looks like it works in a
/// single-process dev run</b>, which is why this paragraph is here rather than in a commit message.
/// </para>
///
/// <para>
/// <b>3. <see cref="ContractShapePrefix"/> is the DTO-shape discriminator; bump it whenever
/// <see cref="EventManifestDto"/> changes.</b> Add a field and every cached device holds a body without
/// it; if the content hash happens to be unchanged they 304 forever against a shape that no longer
/// exists.
/// </para>
///
/// <para>
/// <b>4. A weak validator (<c>W/</c>).</b> A strong ETag asserts byte-identical representations, and
/// two 200s with identical content still differ in <c>serverTime</c>. Weak comparison is what
/// <c>If-None-Match</c> uses anyway, and there are no range requests here.
/// </para>
///
/// <para>
/// <b>What must move it:</b> the event's <c>name</c>/<c>startAt</c>/<c>endAt</c>/<c>graceMinutes</c>/
/// <c>attendanceMode</c>/<c>status</c>; a group attached or detached; an attached group's
/// <c>name</c>/<c>type</c>; a student added to or removed from an attached group, <em>including by SIS
/// import, including by bulk SQL</em>; a student individually attached or detached; a listed student's
/// <c>studentNumber</c>/<c>fullName</c>; any change to a listed student's active card set (issue,
/// reissue, deactivate); a listed student's soft-delete; the DTO shape, via the prefix.
/// </para>
///
/// <para>
/// <b>What must not move it:</b> <c>serverTime</c>; taps and attendance records; the ADR-001 D-2
/// display cache (<c>Students.Course/YearLevel/Section</c>), which is not published here; a group or a
/// student outside this event's audience; and <b>the requesting device</b> — the manifest is identical
/// for every device authorized for the event, which is what would make a server-side hash cache safe if
/// one is ever needed.
/// </para>
/// </summary>
public static class EventManifestVersion
{
    /// <summary>
    /// The contract-shape prefix. <b>Bump it in the same change that alters
    /// <see cref="EventManifestDto"/>'s shape</b> — see rule 3 above.
    /// </summary>
    public const string ContractShapePrefix = "m1.";

    /// <summary>
    /// How many base64url characters of the digest are published. 22 characters is 132 bits, which is
    /// far past the point where two distinct manifests collide, and it keeps the header short enough to
    /// read in a log line.
    /// </summary>
    private const int DigestCharacters = 22;

    /// <summary>The RFC 9110 weak-validator prefix.</summary>
    private const string WeakPrefix = "W/";

    /// <summary>RFC 9110's "any current representation" — matches whatever we would have served.</summary>
    private const string AnyEntityTag = "*";

    /// <summary>
    /// Pinned deliberately, and not shared with the MVC output pipeline. If the wire naming policy or
    /// the escaping ever changes, the response body changes and this hash must not: the version has to
    /// track what the manifest <em>says</em>, not how a serializer happened to spell it that release.
    /// Everything a serializer could decide for us is decided here instead.
    /// </summary>
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        NumberHandling = JsonNumberHandling.Strict,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new RoundTripUtcDateTimeConverter(), new PublishedContentConverter() },
    };

    /// <summary>
    /// What <see cref="EventManifestDto.Version"/> carries while the manifest is being hashed. Assemble
    /// the manifest with this, hash it, then <c>with { Version = … }</c> the answer back on.
    ///
    /// <para>
    /// The placeholder never reaches the hash — <see cref="ExcludedFromTheHash"/> drops the property
    /// structurally — so its value is arbitrary. It exists because the version is a function of the
    /// object that carries it, and something has to occupy the field for the one statement in between.
    /// </para>
    /// </summary>
    public const string PendingVersion = "";

    /// <summary>
    /// The two properties of <see cref="EventManifestDto"/> that are <b>not</b> hashed, and the whole of
    /// that list.
    ///
    /// <para>
    /// <b><c>serverTime</c></b> would make every pull a fresh version, and the conditional GET would
    /// never once answer 304 — a bug whose only symptom is a data bill. <b><c>version</c></b> is the
    /// output and is not knowable while the input is being formed.
    /// </para>
    ///
    /// <para>
    /// <b>Exclusion is what is enumerated here, and that is the point.</b> The alternative — hashing a
    /// hand-written mirror of the published fields — reads as equivalent and is not: a field added to
    /// <see cref="EventManifestDto"/> would simply not appear in the mirror, the hash would not move,
    /// nobody would bump <see cref="ContractShapePrefix"/>, and every cached device would 304 forever
    /// against a shape that no longer exists. That is the failure rule 3 exists to prevent, arriving
    /// through the one door rule 3 does not watch. Inverted, a new field is inside the hash by default
    /// and staying outside it is the thing that takes a deliberate edit.
    /// </para>
    /// </summary>
    private static readonly string[] ExcludedFromTheHash =
    [
        nameof(EventManifestDto.ServerTime),
        nameof(EventManifestDto.Version),
    ];

    /// <summary>
    /// Every other property of <see cref="EventManifestDto"/>, in a fixed order. Resolved once, and
    /// <b>sorted by name rather than left in reflection order</b>: <see cref="Type.GetProperties()"/>
    /// makes no ordering promise, so trusting it would make the published version a property of the
    /// runtime's metadata layout — two hosts on different runtimes could then publish two versions of
    /// an identical manifest, which is the same failure the <see cref="DateTimeKind"/> normalization
    /// below exists to prevent, one level up.
    /// </summary>
    private static readonly PropertyInfo[] HashedProperties = ResolveHashedProperties();

    /// <summary>
    /// The version string for one manifest's published content — <see cref="EventManifestDto.Version"/>,
    /// and the <c>ETag</c> minus its <c>W/</c> and quotes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the whole assembled manifest, and everything on it is hashed except the two
    /// properties <see cref="ExcludedFromTheHash"/> names.</b> That is what keeps a field added to
    /// <see cref="EventManifestDto"/> inside the hash without anyone remembering to come here — the
    /// only DTO in the request is the one the caller is about to publish, so there is no second place
    /// for a field to be forgotten.
    /// </para>
    /// <para>
    /// Pass the manifest with <see cref="PendingVersion"/> in its <c>Version</c>; both that and
    /// <c>ServerTime</c> are dropped before a byte is hashed.
    /// </para>
    /// </remarks>
    public static string Compute(EventManifestDto content)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(content, CanonicalJson);

        return ContractShapePrefix + Base64Url(SHA256.HashData(canonical))[..DigestCharacters];
    }

    private static PropertyInfo[] ResolveHashedProperties()
    {
        var declared = typeof(EventManifestDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        // A rename would otherwise turn an exclusion into a no-op in silence, and the serverTime half of
        // that is the expensive one: it would enter the hash, every pull would answer 200, and the only
        // symptom would be the mobile fleet's data usage. Named properties that do not exist are a
        // startup failure instead.
        foreach (var excluded in ExcludedFromTheHash)
        {
            if (declared.Any(p => string.Equals(p.Name, excluded, StringComparison.Ordinal))) continue;

            throw new InvalidOperationException(
                $"'{excluded}' is excluded from the manifest version but is not a property of " +
                $"{nameof(EventManifestDto)}. It was renamed or removed, and the exclusion silently " +
                "stopped applying — which for ServerTime means every pull publishes a new version and " +
                "no device ever receives a 304. Update ExcludedFromTheHash in the same change.");
        }

        return [.. declared
            .Where(p => !ExcludedFromTheHash.Contains(p.Name, StringComparer.Ordinal))
            .OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Writes an <see cref="EventManifestDto"/> as its published content alone: every property except
    /// the ones <see cref="ExcludedFromTheHash"/> names, in <see cref="HashedProperties"/>' fixed order.
    ///
    /// <para>
    /// <b>Never used on the wire.</b> The response body is serialized by MVC with its own options and
    /// does carry <c>serverTime</c> and <c>version</c>; this form exists only to be hashed, which is
    /// also why <see cref="Read"/> throws.
    /// </para>
    /// </summary>
    private sealed class PublishedContentConverter : JsonConverter<EventManifestDto>
    {
        public override EventManifestDto Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException(
                "The canonical manifest form is written, never read: it exists only to be hashed.");

        public override void Write(
            Utf8JsonWriter writer, EventManifestDto value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            foreach (var property in HashedProperties)
            {
                writer.WritePropertyName(property.Name);
                JsonSerializer.Serialize(writer, property.GetValue(value), property.PropertyType, options);
            }

            writer.WriteEndObject();
        }
    }

    /// <summary>The <c>ETag</c> header value for a version — the weak form, with quotes.</summary>
    public static string ETagFor(string version) => $"{WeakPrefix}\"{version}\"";

    /// <summary>
    /// Whether an <c>If-None-Match</c> header matches <paramref name="version"/>, and therefore whether
    /// the answer is <c>304</c>.
    ///
    /// <para>
    /// <b>Deliberately lenient about the syntax and exact about the value.</b> Real clients send
    /// <c>W/"m1.abc"</c>, <c>"m1.abc"</c> and bare <c>m1.abc</c>, and an HTTP stack in the middle may
    /// coalesce several stored validators into one comma-separated header. Refusing any of those would
    /// downgrade a correct conditional request into a full re-download of a body the device already
    /// holds — which is the entire feature — while telling nobody. <c>*</c> matches per RFC 9110.
    /// </para>
    ///
    /// <para>
    /// What it does <em>not</em> do is compare loosely: the tag itself is matched ordinally. A near-miss
    /// that 304'd would pin a device to a manifest it can never refresh.
    /// </para>
    /// </summary>
    public static bool Matches(IEnumerable<string?> ifNoneMatchValues, string version)
    {
        foreach (var header in ifNoneMatchValues)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;

            foreach (var candidate in header.Split(','))
            {
                var tag = candidate.Trim();
                if (tag.Length == 0) continue;
                if (tag == AnyEntityTag) return true;

                if (tag.StartsWith(WeakPrefix, StringComparison.OrdinalIgnoreCase))
                    tag = tag[WeakPrefix.Length..].TrimStart();

                tag = tag.Trim('"');

                if (string.Equals(tag, version, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Unpadded base64url (RFC 4648 §5). Hand-rolled over <see cref="Convert.ToBase64String(byte[])"/>
    /// rather than taken from a helper so the alphabet is visible at the one place the published value
    /// is formed — <c>+</c> and <c>/</c> in an <c>ETag</c> are legal but survive quoting and proxying
    /// less reliably than the URL-safe pair.
    /// </summary>
    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Round-trip (<c>"O"</c>) UTC, always, whatever <see cref="DateTimeKind"/> the value arrived with.
    ///
    /// <para>
    /// <b>Both halves are load-bearing.</b> The format is pinned because
    /// <c>System.Text.Json</c>'s default shortens a value with no sub-second component, so
    /// <c>10:00:00</c> and <c>10:00:00.0000001</c> would serialize a different number of characters and
    /// the hash would depend on a formatting rule nobody chose. The <c>Kind</c> is normalized because
    /// <c>"O"</c> renders <c>Utc</c> with a <c>Z</c> and <c>Unspecified</c> without one — so two hosts
    /// that disagreed about the Kind of the same instant would publish two versions of an identical
    /// manifest, and every device between them would re-download on every pull.
    /// </para>
    /// </summary>
    private sealed class RoundTripUtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException(
                "The canonical manifest form is written, never read: it exists only to be hashed.");

        public override void Write(
            Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(
                UtcTime.Normalize(value).ToString("O", CultureInfo.InvariantCulture));
    }
}
