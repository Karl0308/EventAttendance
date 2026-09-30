using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>The outcome of a session write (create / close).</summary>
public enum PreRegistrationSessionOutcome
{
    Saved,
    NotFound,

    /// <summary>A blank/over-length name, or a capacity outside 1..1,000,000. 400.</summary>
    ValidationFailed,

    /// <summary>No school could be resolved. 409.</summary>
    NoSchoolResolved,

    /// <summary>The chosen audience does not exist, is inactive, or belongs to another school. 409.</summary>
    AudienceUnavailable,
}

/// <summary>The result of a session write. Null unless it saved.</summary>
public record PreRegistrationSessionWriteResult(
    PreRegistrationSessionOutcome Outcome, string Message, PreRegistrationSessionDto? Session);

/// <summary>The outcome of a single registration (PreRegistration.docx §Validation distinguishes these).</summary>
public enum PreRegisterOutcome
{
    Registered,

    /// <summary>The attendee is already in this session (§: Duplicate Registration). 409.</summary>
    Duplicate,

    /// <summary>A tapped card matched no Academic Community record (§: Unrecognized Card). 422.</summary>
    UnrecognizedCard,

    /// <summary>The session is at capacity (§: Capacity Reached). 409.</summary>
    CapacityReached,

    /// <summary>A manually chosen id matched no student or personnel. 404.</summary>
    AttendeeNotFound,

    /// <summary>No such session in this school. 404.</summary>
    SessionNotFound,

    /// <summary>The session is closed. 409.</summary>
    SessionClosed,

    /// <summary>A malformed request — unknown method/type, missing card or id, or a blank card. 400.</summary>
    ValidationFailed,
}

/// <summary>The result of a registration attempt, with the live counter for the screen.</summary>
public record PreRegisterResult(
    PreRegisterOutcome Outcome,
    string Message,
    int RegisteredCount,
    int Capacity,
    PreRegistrantDto? Registrant);

/// <summary>The result of a registrants read.</summary>
public record PreRegistrantListResult(
    PreRegistrationSessionOutcome Outcome, IReadOnlyList<PreRegistrantDto> Registrants);

/// <summary>
/// <b>The Pre-Registration surface (PreRegistration.docx).</b> Sessions opened against an
/// <c>AudienceDefinition</c>, into which attendees are pre-registered by RFID tap or manual selection,
/// up to a capacity, with duplicate and capacity guards.
/// </summary>
public interface IPreRegistrationService
{
    /// <summary><c>GET /pre-registration/sessions</c> — the sessions, newest first, with live counters.</summary>
    Task<IReadOnlyList<PreRegistrationSessionDto>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary><c>GET /pre-registration/sessions/{id}</c> — one session, or null.</summary>
    Task<PreRegistrationSessionDto?> GetSessionAsync(Guid id, CancellationToken ct = default);

    /// <summary><c>POST /pre-registration/sessions</c> — open a session against an active audience.</summary>
    Task<PreRegistrationSessionWriteResult> CreateSessionAsync(
        PreRegistrationSessionCreateRequest request, CancellationToken ct = default);

    /// <summary><c>PATCH /pre-registration/sessions/{id}/close</c> — close or reopen. Idempotent.</summary>
    Task<PreRegistrationSessionWriteResult> SetClosedAsync(
        Guid id, bool isClosed, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /pre-registration/sessions/{id}/register</c> — register one attendee by tap or manual
    /// selection, subject to the duplicate and capacity rules that apply equally to both methods.
    /// </summary>
    Task<PreRegisterResult> RegisterAsync(
        Guid sessionId, PreRegisterRequest request, CancellationToken ct = default);

    /// <summary><c>GET /pre-registration/sessions/{id}/registrants</c> — the registered attendees.</summary>
    Task<PreRegistrantListResult> ListRegistrantsAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// <c>DELETE /pre-registration/sessions/{id}/registrants/{registrantId}</c> — remove one registration,
    /// freeing its capacity slot. Returns the session outcome (NotFound when the session or row is gone).
    /// </summary>
    Task<PreRegisterResult> RemoveRegistrantAsync(
        Guid sessionId, Guid registrantId, CancellationToken ct = default);
}
