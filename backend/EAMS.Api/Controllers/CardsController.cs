using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// <b>The card registry, searched by serial.</b> "Whose card is this?" — including when the honest
/// answer is more than one person.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is <c>/cards</c> and not a branch of <c>/students</c>, which is the decision the whole
/// endpoint turns on.</b> A route under the roster would have said the student is the answer, and
/// ADR-001 D-3 is explicit that it is not: card uniqueness is
/// <c>UNIQUE(SchoolId, CardUid) WHERE IsActive = 1</c>, so <em>inactive</em> rows are deliberately
/// unconstrained and one serial can name several cards held by several people. QA answered Q5 that a
/// withdrawn card must still resolve — "so admin can still be able to track the card's association with
/// the student" — which puts those rows in scope and makes the lookup multi-valued by construction. The
/// thing that always has exactly one answer is the <em>card</em>, so the card is the resource and the
/// student is a field on it.
/// </para>
///
/// <para>
/// <b>It does not replace <c>GET /students/by-card/{cardUid}</c> and must not be confused with it.</b>
/// That route is the kiosk's: a device key, a rate limit, a whole UID, active cards only, and one
/// student or a 404. Its single-answer shape is correct there — only one card may tap at a time — and
/// it is published contract the mobile client is already built against. This one is the administrator's
/// and answers a different question.
/// </para>
///
/// <para>
/// <b>Permission codes are <c>students.read</c> rather than a newly minted <c>cards.read</c>.</b> The
/// same reasoning <c>ClassificationsController</c> records: cards exist only to identify the people in
/// the roster, the administrator who curates one curates the other, and minting a code here would change
/// the approved RBAC grant matrix (<c>RbacSeedTests</c> pins 11 / 11 / 6 / 4) as a side effect of adding
/// a lookup. The attribute enforces nothing today (ADR-001 D-6).
/// </para>
///
/// <para>
/// <b>It takes <see cref="IStudentService"/> rather than a service of its own</b>, because cards are
/// part of the student aggregate and always have been — <c>POST /students/{id}/cards</c> and
/// <c>DELETE /students/{id}/cards/{cardId}</c> are on that interface too. A second service over the same
/// table would be two places for the ADR-001 D-3 rules to disagree.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/cards")]
public class CardsController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on every other controller.</summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IStudentService _students;

    public CardsController(IStudentService students) => _students = students;

    /// <summary>
    /// <c>GET /cards?cardUid=</c> — find every card whose serial contains a fragment, and who holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Partial matches, by design.</b> QA answered Q6 that typing <c>2503</c> must surface
    /// <c>0012503326</c>, so this is a substring search rather than a lookup. The fragment is normalized
    /// the way stored serials are — uppercase, separators stripped — before it is compared, so
    /// <c>25-03</c>, <c>25:03</c> and <c>2503</c> are one search.
    /// </para>
    ///
    /// <para>
    /// <b>Leading zeros are significant.</b> The CICSS export writes card numbers as decimal digits, so
    /// <c>0012503326</c> and <c>12503326</c> are two different cards; nothing here trims either.
    /// Searching for the shorter one does surface the longer, because it is contained in it — that is
    /// the substring rule working, not the zeros being lost.
    /// </para>
    ///
    /// <para>
    /// <b>Every match is returned, active or not, and none is nominated as the answer.</b> Read
    /// <c>isActive</c> and <c>deactivatedAt</c> on each row. The page order — active first, then most
    /// recently issued — is a convenience for a human reading a list, not a ranking.
    /// </para>
    /// </remarks>
    /// <param name="cardUid">A whole serial or any part of one, in any reader format. Required.</param>
    /// <param name="page">1-based page number, default 1. Out-of-range values are clamped, not refused.</param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200; a larger value is clamped and the response says so.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of matching cards, possibly empty.</response>
    /// <response code="400">
    /// <c>cardUid</c> was missing, or contained no letter or digit — which normalizes to an empty
    /// fragment and would match every card in the school rather than none.
    /// </response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsRead)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsRead)]
    [ProducesResponseType(typeof(PagedResult<CardMatchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<CardMatchDto>>> Search(
        [FromQuery] string? cardUid, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        var response = await _students.SearchCardsAsync(
            cardUid, PageRequest.From(page, pageSize), ct);

        return response.Outcome == CardSearchOutcome.Matched
            ? Ok(response.Matches)
            : Failure(response);
    }

    /// <summary>
    /// The status-code contract for this surface, as one total function over
    /// <see cref="CardSearchOutcome"/>.
    ///
    /// <para>
    /// Every member is listed rather than a <c>_ =&gt; Ok(...)</c> fall-through, for the reason every
    /// other <c>StatusCodeFor</c> on this API records: a discard arm made "an outcome nobody mapped"
    /// indistinguishable from "an outcome that means success", and shipped a rejection as a 200.
    /// </para>
    /// </summary>
    internal static int StatusCodeFor(CardSearchOutcome outcome) => outcome switch
    {
        CardSearchOutcome.Matched => StatusCodes.Status200OK,

        // The caller's query string: a fragment no normalization can turn into something to match on.
        // 400 rather than an empty 200, because an empty page would read as "no such card", which is a
        // fact about the roster, and this is a fact about the request.
        CardSearchOutcome.FragmentUnusable => StatusCodes.Status400BadRequest,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(CardSearchOutcome)}. Every outcome must be " +
            "mapped explicitly, or an unmapped one ships as a success."),
    };

    /// <summary>
    /// §6's declared error shape (RFC 7807), built through <see cref="ProblemDetailsFactory"/> so the
    /// <c>traceId</c> is stamped once, in <c>TracedProblemDetailsFactory</c>, rather than by this action.
    /// </summary>
    private ObjectResult Failure(CardSearchResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status,
            title: "That card search cannot be run.", detail: response.Message);

        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        return StatusCode(status, problem);
    }
}
