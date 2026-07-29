using System.Security.Claims;
using System.Text.Encodings.Web;
using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using DomainDeviceKey = EAMS.Domain.DeviceKey;

namespace EAMS.Api.Authentication;

/// <summary>
/// The <c>DeviceKey</c> authentication scheme (Phase 4a design, D-23). Frozen header contract:
///
/// <code>
/// Authorization: DeviceKey eams_dk_&lt;keyId&gt;_&lt;secret&gt;
/// </code>
///
/// <para>
/// <b>This does not weaken ADR-001 D-6, and the distinction it rests on is worth stating plainly.</b>
/// D-6 deferred <em>authorization</em> — §11's permission-based RBAC over human users — not
/// <em>authentication</em>. Its two seams are <c>ISchoolContext</c> and the structurally inert
/// <c>[HasPermissionNotEnforced]</c>, and neither is an auth mechanism. What lands here is
/// authentication for exactly one principal type, emitting exactly the claim <em>types</em> Phase 6's
/// JWT will emit, so Phase 6 adds <c>.AddJwtBearer("Bearer")</c> beside this rather than unpicking it.
/// </para>
///
/// <para>
/// <b>D-6's operational constraint stays in force verbatim: this system must not be exposed beyond
/// local/development use until §11 lands in full.</b> Four endpoints are now gated. Every other
/// endpoint in the API — the entire roster, every event, the import pipeline — is still open and
/// unauthenticated. This is a narrowing of the open surface, not a rollout.
/// </para>
///
/// <para>
/// <b>Why some failures 401 and others 403.</b> A malformed token, an unknown key id and a wrong
/// secret all fail authentication: nothing was identified, so the answer is "who are you?" — 401. A
/// revoked key and a retired device <em>do</em> identify a device; they simply carry no
/// <c>attendance.capture</c> permission, so authorization refuses them — 403. That split is published
/// contract (<c>docs/api/attendance-contract-handoff.md</c>: "<c>401</c> missing or bad key,
/// <c>403</c> revoked key") and it matters to the client: "your credential is wrong, check what you
/// stored" and "your credential was turned off, re-enrol" need different behaviour from an offline
/// queue.
/// </para>
///
/// <para>
/// <b>Nothing device-specific reaches the authorization layer.</b> The principal carries
/// <c>device_id</c>, but no policy reads it — the gate is <c>RequireClaim("perm",
/// "attendance.capture")</c> and would be satisfied identically by a JWT user holding that permission.
/// That is what makes Phase 6 additive.
/// </para>
/// </summary>
internal sealed class DeviceKeyHandler : AuthenticationHandler<DeviceKeyOptions>
{
    /// <summary>
    /// Where the outcome is stashed for <see cref="HandleChallengeAsync"/> and
    /// <see cref="HandleForbiddenAsync"/> to turn into a <c>code</c>. The two run in a later middleware
    /// than the authenticate call, and <c>AuthenticateResult.Failure</c> is not available on the
    /// forbid path at all, so the request itself is the only place both can read.
    /// </summary>
    private const string OutcomeItem = "eams.devicekey.outcome";

    private readonly IDeviceAuthenticator _devices;
    private readonly IProblemDetailsService _problemDetails;
    private readonly ProblemDetailsFactory _problemDetailsFactory;

    public DeviceKeyHandler(
        IOptionsMonitor<DeviceKeyOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IDeviceAuthenticator devices,
        IProblemDetailsService problemDetails,
        ProblemDetailsFactory problemDetailsFactory)
        : base(options, logger, encoder)
    {
        _devices = devices;
        _problemDetails = problemDetails;
        _problemDetailsFactory = problemDetailsFactory;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // NoResult, not Fail: "this request carries no device key" must stay distinct from "this
        // request carries a bad one". Every endpoint except the four gated ones is still open, and
        // failing here would make an anonymous GET /students look like a rejected credential in the
        // logs of a system where the overwhelming majority of requests legitimately carry none.
        if (!TryReadToken(out var token))
            return AuthenticateResult.NoResult();

        var result = await _devices.AuthenticateAsync(token, Context.RequestAborted);
        Context.Items[OutcomeItem] = result.Outcome;

        switch (result.Outcome)
        {
            case DeviceAuthenticationOutcome.Malformed:
                return AuthenticateResult.Fail("The DeviceKey token is malformed.");

            case DeviceAuthenticationOutcome.UnknownKey:
            case DeviceAuthenticationOutcome.SecretMismatch:
                // Deliberately one message for both. Telling a caller that the key id existed but the
                // secret was wrong confirms a valid key id, which is the only part of the token an
                // attacker could plausibly have obtained from a log.
                return AuthenticateResult.Fail("The DeviceKey token is not valid.");

            case DeviceAuthenticationOutcome.Revoked:
            case DeviceAuthenticationOutcome.DeviceInactive:
                // Identified, not permitted: a principal with sub/school_id/device_id and no `perm`.
                // The policy denies it, the authorization middleware forbids, and the caller gets 403.
                return AuthenticateResult.Success(TicketFor(result, permitted: false));

            case DeviceAuthenticationOutcome.Authenticated:
                // Liveness is stamped only for a device that actually got through, and it is throttled
                // to once a minute per device — see IDeviceAuthenticator.TouchAsync for why a write on
                // every tap is a cost worth avoiding, and why its failure cannot fail this request.
                await _devices.TouchAsync(result.DeviceId, Context.RequestAborted);
                return AuthenticateResult.Success(TicketFor(result, permitted: true));

            default:
                // Total over the enum for the reason AttendanceController.StatusCodeFor records: a
                // discard arm that guessed "success" would ship a rejection as an accepted request.
                throw new ArgumentOutOfRangeException(
                    nameof(result), result.Outcome,
                    $"No authentication result is mapped for this {nameof(DeviceAuthenticationOutcome)}.");
        }
    }

    /// <summary>
    /// Reads the <c>DeviceKey</c> credential out of the <c>Authorization</c> header, ignoring any
    /// other scheme. A request may legitimately carry a <c>Bearer</c> token once Phase 6 exists; this
    /// scheme must not claim it.
    /// </summary>
    private bool TryReadToken(out string token)
    {
        token = "";

        foreach (var value in Request.Headers.Authorization)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!value.StartsWith(DomainDeviceKey.AuthenticationScheme, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = value.AsSpan(DomainDeviceKey.AuthenticationScheme.Length);
            if (rest.Length == 0 || rest[0] != ' ') continue;

            token = rest.TrimStart().ToString();
            return token.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// The claim set, and the one place it is decided.
    ///
    /// <para>
    /// <c>perm</c> is present for a permitted device and absent otherwise. That single difference is
    /// what turns a revoked key into a 403 rather than a 401, and it is expressed as the presence or
    /// absence of a permission rather than as a "revoked" claim on purpose: the authorization layer
    /// must never learn what kind of principal it is refusing.
    /// </para>
    /// </summary>
    private AuthenticationTicket TicketFor(DeviceAuthentication device, bool permitted)
    {
        var claims = new List<Claim>
        {
            new(EamsClaimTypes.Subject, EamsClaimTypes.DeviceSubject(device.DeviceId)),
            new(EamsClaimTypes.SchoolId, device.SchoolId.ToString()),
            new(EamsClaimTypes.DeviceId, device.DeviceId.ToString()),
        };

        if (permitted) claims.Add(new Claim(EamsClaimTypes.Permission, EamsPermissions.AttendanceCapture));

        var identity = new ClaimsIdentity(claims, Scheme.Name, EamsClaimTypes.Subject, roleType: null);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
    }

    /// <summary>
    /// 401 with a <c>WWW-Authenticate</c> challenge (RFC 7235 requires one) and an RFC 7807 body
    /// carrying a <c>traceId</c>, matching the rest of the §6 surface. The framework default is a bare
    /// 401 with no body at all, which leaves a client nothing to branch on and an operator no handle to
    /// search logs by — the same gap <c>HostPipelineTests</c> closed for 500s.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, DomainDeviceKey.AuthenticationScheme);

        await WriteProblemAsync(
            StatusCodes.Status401Unauthorized,
            title: "A device key is required.",
            detail: Outcome() switch
            {
                DeviceAuthenticationOutcome.Malformed =>
                    "The Authorization header is not a well-formed DeviceKey token. Expected " +
                    "'DeviceKey eams_dk_<keyId>_<secret>'.",
                DeviceAuthenticationOutcome.UnknownKey or DeviceAuthenticationOutcome.SecretMismatch =>
                    "That device key is not valid. If the device was rotated, re-enrol it.",
                _ =>
                    "This endpoint requires 'Authorization: DeviceKey eams_dk_<keyId>_<secret>'.",
            },
            code: Outcome() switch
            {
                DeviceAuthenticationOutcome.Malformed => "DeviceKeyMalformed",
                DeviceAuthenticationOutcome.UnknownKey or DeviceAuthenticationOutcome.SecretMismatch
                    => "DeviceKeyInvalid",
                _ => "DeviceKeyMissing",
            });
    }

    /// <summary>
    /// 403 for a device that authenticated and holds no <c>attendance.capture</c> permission. The
    /// <c>code</c> is what tells a client whether to re-enrol (revoked) or to call its administrator
    /// (the device was retired).
    /// </summary>
    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;

        await WriteProblemAsync(
            StatusCodes.Status403Forbidden,
            title: "This device key is not permitted to capture attendance.",
            detail: Outcome() switch
            {
                DeviceAuthenticationOutcome.Revoked =>
                    "This device's key has been revoked. Issue a new one and re-enrol the device.",
                DeviceAuthenticationOutcome.DeviceInactive =>
                    "This device is deactivated. Reactivate it before it can capture attendance.",
                _ =>
                    "The presented credential does not carry the 'attendance.capture' permission.",
            },
            code: Outcome() switch
            {
                DeviceAuthenticationOutcome.Revoked => "DeviceKeyRevoked",
                DeviceAuthenticationOutcome.DeviceInactive => "DeviceInactive",
                _ => "DeviceKeyNotPermitted",
            });
    }

    private DeviceAuthenticationOutcome? Outcome() =>
        Context.Items.TryGetValue(OutcomeItem, out var value) && value is DeviceAuthenticationOutcome o
            ? o
            : null;

    /// <summary>
    /// Built through <see cref="ProblemDetailsFactory"/> so the <c>traceId</c> is stamped once, in
    /// <c>TracedProblemDetailsFactory</c>, rather than by each writer — the same seam every controller
    /// on this API already uses.
    /// </summary>
    private async Task WriteProblemAsync(int status, string title, string detail, string code)
    {
        var problem = _problemDetailsFactory.CreateProblemDetails(
            Context, statusCode: status, title: title, detail: detail, instance: Request.GetEncodedPathAndQuery());

        problem.Extensions["code"] = code;

        await _problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = problem,
        });
    }
}
