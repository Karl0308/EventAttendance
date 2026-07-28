namespace EAMS.Domain;

/// <summary>
/// Which <see cref="EventStatus"/> changes are legal, as a directed graph rather than a set of
/// <c>if</c>s scattered through a service.
///
/// <para>
/// <b>Why the graph is a named domain type.</b> §4.5 lists the four values and says nothing about how
/// an event moves between them, so before this existed the answer was "whatever the last writer to
/// touch the column decided". The transition is also the trigger for the roster freeze
/// (<see cref="FreezesRoster"/>), which means a status write that bypassed the graph would be a
/// <em>silent</em> correctness bug: an event could reach <c>Closed</c> with no absentees materialized,
/// and nothing downstream could tell that from an event where everybody attended.
/// </para>
///
/// <para>
/// <b>The matrix.</b> Rows are the current status, columns the requested one.
/// <code>
///              → Draft   → Open   → Closed   → Cancelled
///   Draft         (=)      yes       no          yes
///   Open          no       (=)       yes         yes
///   Closed        no       no        (=)         no
///   Cancelled     no       no        no          (=)
/// </code>
/// <c>(=)</c> is the no-op: a request that names the status the event already has succeeds and changes
/// nothing. That is deliberate and is <em>not</em> the same as an allowed self-transition — see
/// <see cref="FreezesRoster"/>, which returns false for it. A <c>PATCH</c> retried after a timeout must
/// not re-run the freeze.
/// </para>
///
/// <para>
/// <b>Why <c>Closed</c> is terminal — the re-open decision.</b> Closing an event materializes an
/// <c>Absent</c> record for every expected student who has none, which is what fixes the denominator in
/// place. Re-opening would have to either delete those rows — destroying records an organizer may since
/// have edited by hand, and taking the absentee list with them — or keep them, in which case the event
/// claims to be live while carrying a frozen roster that no longer reflects who is enrolled. Both
/// choices break the guarantee the freeze exists to give, and they break it invisibly. So the graph has
/// no edge out of <c>Closed</c>.
/// </para>
///
/// <para>
/// <b>That is not a dead end for the organizer who closed the wrong event.</b>
/// <c>POST /attendance/manual</c> does not require an open event and never has, so any individual
/// student's status can still be corrected afterwards — deliberately, one row at a time, attributed
/// through <c>RecordedByUserId</c> and visible in the audit trail. The rule being kept is narrower and
/// stronger than "a closed event cannot change": a closed event's numbers cannot change
/// <em>silently</em>, as a side effect of a later roster import.
/// </para>
///
/// <para>
/// <b>Why there is no <c>Open → Draft</c>.</b> Taps can exist by then. Reverting to a status that means
/// "not yet published" while attendance rows sit under it would make <c>Draft</c> ambiguous, and the
/// only thing it buys is an edit that <c>PUT /events/{id}</c> already allows on an open event.
/// </para>
///
/// <para>
/// <b>Why <c>Draft → Closed</c> is refused.</b> An event that never opened recorded nothing, so closing
/// it would mark its entire audience <c>Absent</c> — a roster of absentees for a thing that never
/// happened. <c>Cancelled</c> is the honest terminal state for that, and it is reachable.
/// </para>
/// </summary>
public static class EventStatusTransition
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedTargets =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [EventStatus.Draft] = [EventStatus.Open, EventStatus.Cancelled],
            [EventStatus.Open] = [EventStatus.Closed, EventStatus.Cancelled],
            [EventStatus.Closed] = [],
            [EventStatus.Cancelled] = [],
        };

    /// <summary>
    /// The statuses reachable from <paramref name="from"/>, excluding itself. Empty for a terminal
    /// status, and empty for a value outside <see cref="EventStatus.All"/> — an unknown current status
    /// can only be a corrupt row, and offering it a menu of moves would be inventing a rule for it.
    /// </summary>
    public static IReadOnlyList<string> AllowedFrom(string? from) =>
        from is not null && AllowedTargets.TryGetValue(from, out var targets) ? targets : [];

    /// <summary>
    /// Whether an event may move from <paramref name="from"/> to <paramref name="to"/>. Both must
    /// already be canonical <see cref="EventStatus"/> values — normalize before asking.
    ///
    /// <para>
    /// A request naming the status the event already holds returns <c>true</c>: it is satisfiable, and
    /// it is what a retried <c>PATCH</c> looks like. Callers must still use <see cref="FreezesRoster"/>
    /// rather than <c>to == Closed</c> to decide whether to run the freeze.
    /// </para>
    /// </summary>
    public static bool IsAllowed(string? from, string? to) =>
        from is not null && to is not null
        && (string.Equals(from, to, StringComparison.Ordinal) || AllowedFrom(from).Contains(to));

    /// <summary>No status can be reached from here. <c>Closed</c> and <c>Cancelled</c>.</summary>
    public static bool IsTerminal(string? status) => AllowedFrom(status).Count == 0;

    /// <summary>
    /// Whether this transition is the one that materializes the expected roster.
    ///
    /// <para>
    /// <b><c>from != to</c> is the load-bearing half.</b> Re-running the freeze on an event that is
    /// already <c>Closed</c> would insert an <c>Absent</c> row for every student who has joined the
    /// attached sections since — which is precisely the movement the freeze exists to prevent, arriving
    /// through the mechanism meant to stop it.
    /// </para>
    /// </summary>
    public static bool FreezesRoster(string? from, string? to) =>
        to == EventStatus.Closed && !string.Equals(from, to, StringComparison.Ordinal);

    /// <summary>
    /// Whether the event's audience may still be changed.
    ///
    /// <para>
    /// True for <c>Draft</c> and <c>Open</c> — the two states in which the audience is <em>live</em>,
    /// meaning it re-reads current section membership every time it is asked, so a student enrolled by
    /// an import between now and the event is correctly expected.
    /// </para>
    ///
    /// <para>
    /// False for <c>Closed</c> (the roster is materialized; changing who was invited would contradict
    /// the rows already written) and for <c>Cancelled</c> (who was invited to an event that did not
    /// happen is a historical fact, not a working list).
    /// </para>
    /// </summary>
    public static bool AcceptsAudienceChanges(string? status) =>
        status == EventStatus.Draft || status == EventStatus.Open;

    /// <summary>
    /// Whether the event's own fields — name, window, mode, grace — may still be edited.
    ///
    /// <para>
    /// Everything except <c>Closed</c>. <c>StartAt</c> and <c>GraceMinutes</c> are the inputs that
    /// decided Present-versus-Late for every row already recorded, so editing them after the close
    /// silently changes what those rows mean without changing the rows. <c>Cancelled</c> is editable on
    /// purpose: nothing was computed from it, and "cancelled — venue flooded" is a legitimate edit.
    /// </para>
    /// </summary>
    public static bool AcceptsEdits(string? status) => status != EventStatus.Closed;
}
