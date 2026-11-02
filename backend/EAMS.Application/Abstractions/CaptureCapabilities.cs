namespace EAMS.Application.Abstractions;

/// <summary>
/// What a capture client has said it understands, read once from <c>X-EAMS-Capabilities</c> at the API
/// boundary (ADR-015 D-100). Services receive this value and never read a header themselves.
///
/// <para>
/// <b>It changes response types only, never access.</b> Scheme, policy, tenant, rate-limit partition and
/// eligibility are identical with and without it. The one thing it switches is whether a personnel tap
/// can be answered as one (v2) or must be answered as <c>CardNotFound</c> (v1).
/// </para>
///
/// <para>
/// Honoured on <c>POST /attendance/tap</c>, <c>/tap/batch</c> and <c>GET /events/{id}/manifest</c>.
/// Ignored everywhere else.
/// </para>
/// </summary>
/// <param name="Personnel">The client sent the exact <see cref="PersonnelToken"/>.</param>
public readonly record struct CaptureCapabilities(bool Personnel)
{
    /// <summary>The field name. HTTP field names are case-insensitive; ASP.NET's header lookup already is.</summary>
    public const string HeaderName = "X-EAMS-Capabilities";

    /// <summary>
    /// The one token, matched <b>ordinal and case-sensitive</b>. The risky direction is switching v2 on,
    /// so only the exact token does: <c>PERSONNEL</c>, <c>Personnel</c> and <c>personel</c> do not match.
    /// </summary>
    public const string PersonnelToken = "personnel";

    /// <summary>A client that sent no header, or nothing this API recognizes: today's v1 contract.</summary>
    public static CaptureCapabilities None => default;

    /// <summary>
    /// RFC 9110 <c>#token</c> list syntax: comma-separated, optional whitespace (SP / HTAB) around each
    /// element trimmed, empty elements ignored, and repeated field lines merged into one list. Unknown
    /// tokens are ignored. <b>Never throws and never refuses</b> — the header cannot cause a 400.
    /// </summary>
    /// <param name="fieldLines">
    /// Every field line received, in order — ASP.NET's <c>StringValues</c> for the header. Null or empty
    /// reads as <see cref="None"/>.
    /// </param>
    public static CaptureCapabilities Parse(IEnumerable<string?>? fieldLines)
    {
        if (fieldLines is null) return None;

        foreach (var line in fieldLines)
        {
            if (string.IsNullOrEmpty(line)) continue;

            foreach (var element in line.Split(','))
            {
                if (string.Equals(element.Trim(OptionalWhitespace), PersonnelToken, StringComparison.Ordinal))
                    return new CaptureCapabilities(Personnel: true);
            }
        }

        return None;
    }

    /// <summary>RFC 9110 §5.6.3 <c>OWS</c>: space and horizontal tab, nothing else.</summary>
    private static readonly char[] OptionalWhitespace = [' ', '\t'];
}
