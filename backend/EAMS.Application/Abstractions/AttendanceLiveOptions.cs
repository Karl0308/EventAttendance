using System.Globalization;

namespace EAMS.Application.Abstractions;

/// <summary>
/// The one tunable of the D-29 polling endpoint: how long a client is told to wait before asking
/// again.
///
/// <para>
/// <b>Why it is configuration and not a constant.</b> A polling design's only load lever is the poll
/// interval, and the client holds it — so unless the server can move it, backing fifty open dashboards
/// off during a convocation means shipping a mobile and an SPA release. In the body of every response,
/// read from configuration, it is a restart.
/// </para>
///
/// <para>
/// A plain record rather than an <c>IOptions&lt;T&gt;</c> binding, because the Application layer takes
/// no dependency outside Domain (Technical Plan §3) and <c>IOptions</c> would be the first. It is
/// resolved once in <c>AddEamsInfrastructure</c>, which already has the <c>IConfiguration</c>, and
/// registered as a singleton.
/// </para>
/// </summary>
/// <param name="RejectedConfiguredValue">
/// The configured string that could not be used, when <see cref="Resolve"/> fell back to the default;
/// null otherwise. Carried on the record rather than logged inside <see cref="Resolve"/> because that
/// method is pure and is called from <c>AddEamsInfrastructure</c>, where no logger has been built yet.
/// The composition root reports it at startup — "why is the poll interval still five seconds?" is
/// otherwise a question only the source can answer.
/// </param>
public sealed record AttendanceLiveOptions(int PollAfterSeconds, string? RejectedConfiguredValue = null)
{
    /// <summary>
    /// The most delta objects one response may carry, snapshot or delta alike.
    ///
    /// <para>
    /// <b>Not configurable, unlike the poll interval, and the asymmetry is deliberate.</b> The interval
    /// is an operational lever — it exists so someone can back clients off during a convocation. This is
    /// a correctness bound on a single response, and an operator lowering it would silently make every
    /// dashboard slower to fill while an operator raising it would re-open the unbounded read it exists
    /// to close. Neither is a decision worth exposing.
    /// </para>
    ///
    /// <para>
    /// Five hundred: comfortably more than any single poll of a live event produces (an event taking
    /// five hundred taps between two polls is a stampede, not a lecture), so the cap is invisible in
    /// steady state and only ever engages on a first snapshot of a large audience — which is exactly
    /// the case it was added for. See <c>AttendanceLiveDto.HasMore</c> for why truncation is paging
    /// rather than loss.
    /// </para>
    /// </summary>
    public const int MaxPageRows = 500;

    /// <summary>
    /// Five seconds. Fast enough that an organizer watching the door sees a tap appear while the
    /// student is still in front of them, slow enough that a lecture hall's worth of open dashboards is
    /// a handful of indexed seeks a second.
    /// </summary>
    public const int DefaultPollAfterSeconds = 5;

    /// <summary>One second. Below this, polling is a busy-wait against the database.</summary>
    public const int MinPollAfterSeconds = 1;

    /// <summary>
    /// Five minutes. Above this the endpoint has stopped being "live" in any sense a user would accept,
    /// and a value that large is far more likely to be a typo — milliseconds pasted into a seconds
    /// field — than a decision.
    /// </summary>
    public const int MaxPollAfterSeconds = 300;

    /// <summary>The <c>appsettings</c> path. Flat rather than bound to a section, because there is one key.</summary>
    public const string ConfigurationKey = "Attendance:LivePollAfterSeconds";

    public static readonly AttendanceLiveOptions Default = new(DefaultPollAfterSeconds);

    /// <summary>
    /// Reads the configured value, falling back to <see cref="Default"/> for anything absent,
    /// unparseable or outside <see cref="MinPollAfterSeconds"/>..<see cref="MaxPollAfterSeconds"/>.
    ///
    /// <para>
    /// <b>A silent fallback is acceptable here and is not acceptable for the D-36 tap window</b>, and
    /// the difference is worth stating because the two look like the same pattern. A mis-typed tap
    /// window changes which taps are <em>accepted</em> — the failure is invisible, permanent in the
    /// data, and discovered from an attendance report weeks later, which is why that read logs every
    /// candidate it could not parse. A mis-typed poll interval changes how often a dashboard refreshes;
    /// it is visible within seconds to the person looking at the dashboard, affects no stored row, and
    /// is corrected by fixing the setting. Same shape, different blast radius.
    /// </para>
    /// </summary>
    public static AttendanceLiveOptions Resolve(string? configuredValue)
    {
        // Absent is not "rejected" — an unconfigured host is the ordinary case and has nothing to
        // report. Only a value somebody actually wrote and we could not use is worth a startup line.
        if (string.IsNullOrWhiteSpace(configuredValue)) return Default;

        if (!int.TryParse(
                configuredValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return Default with { RejectedConfiguredValue = configuredValue };
        }

        if (seconds < MinPollAfterSeconds || seconds > MaxPollAfterSeconds)
            return Default with { RejectedConfiguredValue = configuredValue };

        return new AttendanceLiveOptions(seconds);
    }
}
