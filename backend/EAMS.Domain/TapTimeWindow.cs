using System.Globalization;

namespace EAMS.Domain;

/// <summary>
/// Phase 4c, D-36 — the bounds a claimed <c>tappedAt</c> has to fall inside, and the §4.13 keys that
/// move them per school.
///
/// <para>
/// <b>The rule this type exists to make possible: we never silently rewrite <c>tappedAt</c>.</b> The
/// obvious alternative — clamping a skewed timestamp into the event's window — is the same fabrication
/// <c>AttendanceService.RecordsAnArrival</c> already refuses to commit when it declines to stamp a
/// <c>CheckInAt</c> on an <c>Absent</c> row: it invents an observation nobody made, and it does it on
/// the one field that decides Present vs Late. So a timestamp we cannot believe is <em>rejected</em>,
/// with a token that tells the client which of the two reasons applies, and the client fixes its clock
/// or its event id. This is published: see <c>docs/api/attendance-contract-handoff.md</c>.
/// </para>
///
/// <para>
/// <b>Why the future and the past are treated differently, and it is not an oversight.</b> A tap
/// claiming to be more than <see cref="FutureToleranceMinutes"/> minutes ahead of the server is never
/// legitimate — no device can observe a card that has not been presented yet — and it is the skew
/// direction that <em>benefits</em> the student, since a check-in dated forward past the grace boundary
/// is the one a Late student would want. The past is deliberately never age-limited: an old queued tap
/// is the entire point of §8.2's offline sync, and a rule like "nothing older than a day" would discard
/// exactly the evidence the queue exists to preserve. The event window is what bounds the past, and it
/// bounds it by the event rather than by age.
/// </para>
///
/// <para>
/// <b>It is also the plan's own forgery mitigation, arrived at from the other side.</b> A card UID is a
/// student number (ADR-001 D-3), student numbers are sequential and printed on the ID, so a UID is
/// guessable. Bounding <em>when</em> a tap may be claimed for is what makes a guessed UID useless
/// outside the hour the event actually ran — and the same predicate happens to bound a badly skewed
/// device clock, which is why one mechanism closes two problems.
/// </para>
///
/// <para>
/// <b>Only the claimed time is checked, never the submission time.</b> A batch flushed three days late
/// is the queue working as designed; the rows inside it still have to name a time inside their event's
/// window. Constraining submission lateness would break offline sync outright.
/// </para>
/// </summary>
/// <param name="BeforeStartMinutes">
/// How long before <c>Event.StartAt</c> a tap may be claimed for. Early arrivals are ordinary — the
/// doors open before the event starts — so this is generous by default.
/// </param>
/// <param name="AfterEndMinutes">
/// How long after <c>Event.EndAt</c> a tap may be claimed for. Covers the check-out queue at the exit
/// of a <c>TimeInOut</c> event, which by construction drains after the event has ended.
/// </param>
public sealed record TapTimeWindow(int BeforeStartMinutes, int AfterEndMinutes)
{
    /// <summary>Published as "default 60 minutes either side".</summary>
    public const int DefaultBeforeStartMinutes = 60;

    /// <inheritdoc cref="DefaultBeforeStartMinutes"/>
    public const int DefaultAfterEndMinutes = 60;

    /// <summary>
    /// How far ahead of the server's clock a <c>tappedAt</c> may be before it is refused. Published as
    /// five minutes and deliberately <b>not</b> configurable: it is not a policy about events, it is the
    /// statement that a device cannot observe the future, and a school that could widen it could quietly
    /// re-open the one skew direction that lets a Late tap be dated Present.
    /// </summary>
    public const int FutureToleranceMinutes = 5;

    /// <summary>
    /// §4.13 <c>SystemSettings.Key</c> for <see cref="BeforeStartMinutes"/>. A row with the school's
    /// <c>SchoolId</c> wins over the global (<c>NULL</c>) row; absence of both means the default.
    ///
    /// <para>
    /// Absence meaning "the default" rather than "no window" is the load-bearing half: a settings table
    /// nobody has populated must not be the same thing as a validation that does not run.
    /// </para>
    /// </summary>
    public const string BeforeStartMinutesSettingKey = "attendance.tapWindow.beforeStartMinutes";

    /// <inheritdoc cref="BeforeStartMinutesSettingKey"/>
    public const string AfterEndMinutesSettingKey = "attendance.tapWindow.afterEndMinutes";

    /// <summary>What a school with no §4.13 rows gets, which today is every school.</summary>
    public static readonly TapTimeWindow Default =
        new(DefaultBeforeStartMinutes, DefaultAfterEndMinutes);

    /// <summary>
    /// Whether a claimed tap time is far enough ahead of the server's clock to be impossible. Both
    /// arguments must already be UTC — see <see cref="UtcTime"/> for why that is stated rather than
    /// assumed.
    /// </summary>
    public static bool IsImplausiblyFuture(DateTime tappedAt, DateTime serverTime) =>
        tappedAt > serverTime.AddMinutes(FutureToleranceMinutes);

    /// <summary>
    /// Whether a claimed tap time falls inside <c>[StartAt − BeforeStartMinutes, EndAt + AfterEndMinutes]</c>.
    /// Inclusive at both ends, matching the grace-period boundary in the tap path: a tap exactly on a
    /// published boundary is inside it, because "60 minutes either side" reads as a closed interval to
    /// everyone who is not writing the comparison.
    /// </summary>
    public bool Contains(DateTime tappedAt, DateTime startAt, DateTime endAt) =>
        tappedAt >= startAt.AddMinutes(-BeforeStartMinutes)
        && tappedAt <= endAt.AddMinutes(AfterEndMinutes);

    /// <summary>
    /// The earliest and latest instants this window admits for an event, for the message a rejected
    /// client is handed. A token tells a client to stop retrying; the bounds tell a human which of the
    /// two clocks is wrong.
    /// </summary>
    public (DateTime From, DateTime To) BoundsFor(DateTime startAt, DateTime endAt) =>
        (startAt.AddMinutes(-BeforeStartMinutes), endAt.AddMinutes(AfterEndMinutes));

    /// <summary>
    /// Reads a §4.13 setting value as a minute count. <c>SystemSettings.Value</c> is
    /// <c>nvarchar(max)</c> with a free-text <c>DataType</c> beside it, so anything at all can be in
    /// there; invariant culture because a settings row is not localized, and negative is refused
    /// because a window that runs backwards would reject every tap on the event it was meant to widen.
    /// </summary>
    public static bool TryParseMinutes(string? value, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;
        if (parsed < 0) return false;

        minutes = parsed;
        return true;
    }
}
