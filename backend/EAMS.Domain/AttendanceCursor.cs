using System.Buffers.Binary;
using System.Buffers.Text;

namespace EAMS.Domain;

/// <summary>
/// The wire form of the live-attendance cursor (Phase 4d, D-30): base64 of a SQL Server
/// <c>rowversion</c>'s eight bytes.
///
/// <para>
/// <b>Why not <c>UpdatedAt</c>, which is the obvious cursor and is already on the row.</b> It is a
/// <c>datetime2</c> written by the application from <c>DateTime.UtcNow</c>, and it fails as a cursor in
/// two independent ways. Two rows written in the same tick share a value, so <c>&gt; lastSeen</c> drops
/// the second and <c>&gt;=</c> re-sends the first forever; and the clock it comes from can move
/// <em>backwards</em> — an NTP correction or a VM migration — so a row written after the cursor can
/// carry a timestamp before it and is never seen again. Neither failure raises anything: the dashboard
/// simply stops showing a student who tapped, which is indistinguishable from a student who did not.
/// </para>
///
/// <para>
/// A <c>rowversion</c> has neither problem. It is assigned by the database from a single per-database
/// counter, on every insert <em>and</em> every update, and it only ever increases. It is not a time and
/// carries no meaning beyond ordering, which is exactly what a cursor is for.
/// </para>
///
/// <para>
/// <b>Opaque on purpose.</b> The published contract says a client stores the cursor and sends it back,
/// nothing more. Encoding the raw eight bytes rather than the decimal number is what keeps it that
/// way: a client that sees <c>"AAAAAAAAB9I"</c> does not start doing arithmetic on it.
/// </para>
///
/// <para>
/// <b>base64url, not standard base64, and the difference is a real defect rather than a preference</b>
/// (Phase 4d review). This value's only job is to survive a round trip through <c>?since=</c>. Standard
/// base64's alphabet includes <c>+</c>, which a URL decodes as a space; <c>Convert.TryFromBase64String</c>
/// then <em>ignores</em> that whitespace, so eleven data characters become ten, the decode yields six
/// bytes instead of eight, and the poll comes back <c>400 InvalidCursor</c>.
/// </para>
///
/// <para>
/// It is reachable rather than exotic: the ninth character of the encoding is <c>byte6 &gt;&gt; 2</c>,
/// which reaches alphabet index 62 — <c>+</c> — whenever that byte lands in 248..251. A
/// <c>rowversion</c> counter is database-wide and advances on every insert and update to <em>any</em>
/// table, so it passes through that range roughly every sixteen million writes. And it fails in the
/// worst available way: the client is told its cursor is invalid, falls back to a full snapshot, and
/// every cursor it is handed afterwards has the same problem — so it storms snapshots and never
/// recovers. Our own handoff document tells the mobile developer to send the cursor verbatim, which is
/// precisely the client that builds the URL by hand.
/// </para>
///
/// <para>
/// <b><see cref="TryDecode"/> accepts both alphabets</b>, so a cursor issued before this change keeps
/// working. Only <see cref="Encode"/> narrowed.
/// </para>
/// </summary>
public static class AttendanceCursor
{
    /// <summary>A <c>rowversion</c> is exactly eight bytes; anything else is not one.</summary>
    public const int ByteLength = 8;

    /// <summary>
    /// The cursor a client is handed before it has seen anything. Never emitted by the server — a
    /// snapshot always reports a real ceiling — but it is what a decoded absent cursor means, and
    /// naming it stops <c>0</c> being written as a bare literal at three call sites.
    /// </summary>
    public const long Beginning = 0;

    /// <summary>
    /// Big-endian, and that is load-bearing rather than a style choice. SQL Server compares
    /// <c>binary(8)</c> left to right, so the byte order has to be the one where that comparison and
    /// numeric <c>&lt;</c> agree. EF's <c>NumberToBytesConverter&lt;long&gt;</c> writes big-endian for
    /// the same reason, and this method has to match it byte for byte or a cursor would sort one way in
    /// the database and another in the client's storage.
    /// </summary>
    public static string Encode(long rowVersion)
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        BinaryPrimitives.WriteInt64BigEndian(bytes, rowVersion);

        // base64url: '-' and '_' in place of '+' and '/', and no '=' padding — so the whole value is
        // URL-safe with no percent-encoding and no ambiguity about whether a client encoded it.
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>
    /// Reads a client-supplied cursor. Returns <c>false</c> for anything that is not base64 of exactly
    /// <see cref="ByteLength"/> bytes.
    ///
    /// <para>
    /// <b>A bad cursor is refused rather than treated as "start from the beginning".</b> Falling back to
    /// a full snapshot would be the friendlier-looking choice and is the wrong one: the client believes
    /// it is receiving a delta, so it would apply a complete row set to a reducer expecting changes, and
    /// the only symptom would be a dashboard that occasionally duplicates every entry. Refusing names the
    /// problem at the one moment it is cheap to find.
    /// </para>
    /// </summary>
    public static bool TryDecode(string? cursor, out long rowVersion)
    {
        rowVersion = Beginning;
        if (string.IsNullOrWhiteSpace(cursor)) return false;

        Span<byte> bytes = stackalloc byte[ByteLength];
        if (!TryDecodeBytes(cursor, bytes)) return false;

        rowVersion = BinaryPrimitives.ReadInt64BigEndian(bytes);
        return true;
    }

    /// <summary>
    /// Accepts either alphabet by normalizing base64url into standard base64 and decoding once.
    ///
    /// <para>
    /// The standard-base64 branch is compatibility for cursors issued before <see cref="Encode"/>
    /// narrowed. It is worth keeping rather than cutting over: a client holding one is mid-poll, and
    /// refusing it would hand exactly that client the full-snapshot storm this change exists to prevent.
    /// </para>
    ///
    /// <para>
    /// <b>Written as a normalization rather than as "try base64url, fall back to base64", which is what
    /// it was first, because <c>Base64Url.TryDecodeFromChars</c> THROWS.</b> It raises
    /// <c>FormatException</c> on a character outside its alphabet despite the <c>Try</c> prefix and the
    /// <c>bool</c> return — so the obvious two-attempt version turned every legacy cursor containing a
    /// <c>+</c> into an unhandled exception on the live endpoint, which is a 500 where the whole point
    /// of the change was to stop returning a 400. <c>Convert.TryFromBase64Chars</c> genuinely does not
    /// throw, so everything routes through it and nothing here depends on a <c>Try</c> method keeping
    /// its promise.
    /// </para>
    ///
    /// <para>
    /// The length gate is the other half. Eight bytes is 12 base64 characters padded or 11 unpadded, and
    /// nothing else can be a cursor — which is also what rejects the <c>+</c>-decoded-as-a-space case
    /// cleanly, since <c>Convert</c> silently ignores whitespace and would otherwise hand back six bytes
    /// from a string that looked the right length.
    /// </para>
    /// </summary>
    private static bool TryDecodeBytes(string cursor, Span<byte> bytes)
    {
        const int PaddedLength = 12;
        const int UnpaddedLength = 11;

        if (cursor.Length is not (PaddedLength or UnpaddedLength)) return false;

        Span<char> standard = stackalloc char[PaddedLength];
        for (var i = 0; i < cursor.Length; i++)
        {
            standard[i] = cursor[i] switch { '-' => '+', '_' => '/', var c => c };
        }

        if (cursor.Length == UnpaddedLength) standard[UnpaddedLength] = '=';

        return Convert.TryFromBase64Chars(standard, bytes, out var written) && written == ByteLength;
    }
}
