namespace EAMS.Domain;

/// <summary>
/// Every <c>DateTime</c> in this system is UTC. §4 stores them as <c>datetime2</c>, which carries no
/// offset, so the <em>Kind</em> is the only thing that tells JSON (and therefore the browser) how to
/// read the value — and it is the thing most easily lost.
///
/// <para>
/// Two boundaries have to normalize, and they normalize in different places:
/// <list type="bullet">
///   <item><b>Persistence</b> — the EF value converter in EAMS.Infrastructure stamps
///   <see cref="DateTimeKind.Utc"/> on every value read back from SQL Server.</item>
///   <item><b>Inbound JSON</b> — this helper. <c>System.Text.Json</c> yields
///   <see cref="DateTimeKind.Utc"/> for <c>"…Z"</c>, <see cref="DateTimeKind.Local"/> for
///   <c>"…+08:00"</c>, and <see cref="DateTimeKind.Unspecified"/> for a bare
///   <c>"2026-07-28T10:00:00"</c>. A <c>Local</c> value compared numerically against a UTC one is
///   wrong by the machine's offset — which is exactly how a Manila tap lands eight hours early.</item>
/// </list>
/// </para>
///
/// <para>
/// <c>Unspecified</c> is read as already-UTC rather than converted. Converting it would apply the
/// <em>server's</em> offset to a value whose origin we do not know — silently wrong, and wrong by a
/// different amount on every machine. Treating it as UTC is the documented contract: clients that
/// mean local time must send an offset.
/// </para>
/// </summary>
public static class UtcTime
{
    public static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static DateTime? Normalize(DateTime? value) =>
        value.HasValue ? Normalize(value.Value) : null;
}
