using System.Globalization;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Api;

/// <summary>
/// Maps <see cref="WriteLockUnavailableException"/> (today: ADR-015 D-98's card-UID lock refusal) to
/// <c>503 Service Unavailable</c> with <c>Retry-After</c> instead of a 500 (JJ, 2026-10-28, decision 5).
///
/// <para>
/// <b>Why 503 and not 500 or 409.</b> The request was correct: another writer held the lock, or this one
/// lost a deadlock. 500 says "our bug"; 409 says "your request conflicts with the resource's state" and
/// invites the client to change it. 503 plus <c>Retry-After</c> says the only true thing — send the same
/// request again shortly.
/// </para>
///
/// <para>
/// <b>The body must not promise "nothing was written", because on two of the five paths that is false.</b>
/// Adding a student's card, and creating or updating one personnel record, are one transaction, so a
/// refusal leaves nothing. <c>POST /personnel/import</c> commits row by row and a roster run commits in
/// passes (no wrapping transaction), so a refusal part-way leaves the earlier work in place. What holds on
/// all five is that repeating the request is safe: the three atomic writes did nothing, the personnel
/// import upserts by number, and a roster run leaves its batch <c>Failed</c>, which is re-runnable and
/// converges.
/// </para>
///
/// <para>
/// Runs inside <c>UseExceptionHandler()</c>, which has already cleared the response, so the status and
/// header set here are the ones sent. The body goes through <see cref="IProblemDetailsService"/>, so it
/// carries the same <c>traceId</c> as every other error. The exception message names the lock resource
/// (school id and UID) and is logged, never returned.
/// </para>
/// </summary>
internal sealed class WriteLockUnavailableExceptionHandler : IExceptionHandler
{
    private const string Title = "The write could not be completed. Try again shortly.";

    private const string Detail =
        "Another change to the same card was in progress, so this request did not finish. Sending the " +
        "same request again is safe; wait the number of seconds in Retry-After first.";

    private readonly IProblemDetailsService _problems;
    private readonly ILogger<WriteLockUnavailableExceptionHandler> _logger;

    public WriteLockUnavailableExceptionHandler(
        IProblemDetailsService problems, ILogger<WriteLockUnavailableExceptionHandler> logger)
    {
        _problems = problems;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not WriteLockUnavailableException) return false;

        _logger.LogWarning(
            exception,
            "A write was refused before it started because a lock it needed was not granted; answered " +
            "503 with Retry-After {RetryAfterSeconds}s.",
            WriteLockUnavailableException.RetryAfterSeconds);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter =
            WriteLockUnavailableException.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = Title,
            Detail = Detail,
        };
        problem.Extensions["code"] = WriteLockUnavailableException.Code;

        return await _problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
