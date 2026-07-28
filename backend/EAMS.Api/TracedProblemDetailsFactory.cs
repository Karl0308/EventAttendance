using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace EAMS.Api;

/// <summary>
/// The one place a <see cref="ProblemDetails"/> body gets its <c>traceId</c>, whoever built it.
///
/// <para>
/// <b>Why <c>Program.cs</c>'s <c>CustomizeProblemDetails</c> was not enough.</b> That callback belongs
/// to <c>IProblemDetailsService</c>, which runs only for the responses the exception handler and the
/// status-code middleware <em>generate</em>. It never sees a body MVC produces — and MVC produces the
/// most common error in the whole API: <c>[ApiController]</c>'s automatic model-validation 400, emitted
/// before any action method runs, on every controller. So a caller who mistyped a GUID got the one
/// error shape in the system with nothing to quote back, and the omission was invisible because the
/// body still looked like a ProblemDetails.
/// </para>
///
/// <para>
/// <b>A factory rather than an <c>InvalidModelStateResponseFactory</c>.</b> Setting that option fixes
/// the automatic 400 and nothing else, leaving <c>ControllerBase.Problem()</c>,
/// <c>ValidationProblem()</c> and every hand-composed body to stamp their own — which is exactly how
/// the gap appeared the first time. <see cref="ProblemDetailsFactory"/> is the single seam all of those
/// run through (the default <c>InvalidModelStateResponseFactory</c> calls
/// <see cref="CreateValidationProblemDetails"/> itself), so stamping here is stamping once.
/// </para>
///
/// <para>
/// It also fills <see cref="ProblemDetails.Status"/> and <see cref="ProblemDetails.Instance"/>, because
/// a body serializing <c>"status": null</c> forces a client to read the HTTP status and the payload
/// from two places and reconcile them.
/// </para>
///
/// <para>
/// <b>The <c>Title</c>/<c>Type</c> defaulting below is not decoration, and leaving it out broke a
/// test immediately.</b> Replacing this factory replaces it on <em>every</em> path MVC owns, and one of
/// those is <c>DefaultApiProblemDetailsWriter</c> — the writer <c>UseExceptionHandler</c> ends up using
/// when MVC is present. So an unhandled 500 is built here too, and a factory that only stamped a trace
/// id produced a body with no title at all: less than the one it replaced. The mapping is read from
/// <see cref="ApiBehaviorOptions.ClientErrorMapping"/>, which is where the framework's own defaults
/// live, rather than restated here — a private copy of "500 means …" would drift.
/// </para>
/// </summary>
internal sealed class TracedProblemDetailsFactory : ProblemDetailsFactory
{
    private readonly ApiBehaviorOptions _options;

    public TracedProblemDetailsFactory(IOptions<ApiBehaviorOptions> options) =>
        _options = options.Value;

    public override ProblemDetails CreateProblemDetails(
        HttpContext httpContext,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode ?? StatusCodes.Status500InternalServerError,
            Title = title,
            Type = type,
            Detail = detail,
            Instance = instance,
        };

        ApplyDefaults(problem, title);
        Stamp(problem, httpContext);
        return problem;
    }

    public override ValidationProblemDetails CreateValidationProblemDetails(
        HttpContext httpContext,
        ModelStateDictionary modelStateDictionary,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null)
    {
        ArgumentNullException.ThrowIfNull(modelStateDictionary);

        var problem = new ValidationProblemDetails(modelStateDictionary)
        {
            Status = statusCode ?? StatusCodes.Status400BadRequest,
            Type = type,
            Detail = detail,
            Instance = instance,
        };

        // ValidationProblemDetails seeds its own title ("One or more validation errors occurred."), so
        // it is passed to ApplyDefaults as already-supplied and the status mapping does not overwrite
        // it with the generic "Bad Request".
        if (title is not null) problem.Title = title;
        ApplyDefaults(problem, problem.Title);

        Stamp(problem, httpContext);
        return problem;
    }

    /// <summary>
    /// Fills <c>Title</c> and <c>Type</c> from the framework's own status-code mapping when the caller
    /// supplied no title — the behaviour <c>DefaultProblemDetailsFactory</c> provides and this factory
    /// would otherwise remove from every path MVC builds a body on.
    /// </summary>
    private void ApplyDefaults(ProblemDetails problem, string? suppliedTitle)
    {
        if (suppliedTitle is not null) return;
        if (problem.Status is not { } status) return;
        if (!_options.ClientErrorMapping.TryGetValue(status, out var mapping)) return;

        problem.Title ??= mapping.Title;
        problem.Type ??= mapping.Link;
    }

    private static void Stamp(ProblemDetails problem, HttpContext httpContext)
    {
        // Activity.Current is the id the logs are stamped with when distributed tracing is on;
        // TraceIdentifier is the per-connection fallback. Same pair Program.cs uses, so a 500 from the
        // exception handler and a 400 from model binding quote ids from the same source.
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        problem.Instance ??= httpContext.Request.Path;
    }
}
