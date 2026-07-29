using System.Diagnostics;
using EAMS.Api;
using EAMS.Api.Authorization;
using EAMS.Api.Cors;
using EAMS.Infrastructure;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Replaces MVC's DefaultProblemDetailsFactory, which is what builds [ApiController]'s automatic
// model-validation 400s — the most common error body in the API and the only one that carried no
// traceId, because those never pass through AddProblemDetails' CustomizeProblemDetails below. See
// TracedProblemDetailsFactory. Replace rather than Add: AddControllers registers the default with
// TryAddSingleton, so an Add here would be ignored and the fix would silently do nothing.
builder.Services.Replace(
    ServiceDescriptor.Singleton<ProblemDetailsFactory, TracedProblemDetailsFactory>());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Plan §6's header declares "Errors: RFC 7807 ProblemDetails", which nothing implemented. Without
// it an unhandled path returned a full stack trace in Development — on endpoints that are
// deliberately open (ADR-001 D-6) — and a 500 with an empty body in Production, which tells a
// client nothing and leaves an operator with no handle to search logs by.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    // The one thing a caller must be able to quote back. Activity.Current is the id the logs are
    // stamped with when distributed tracing is on; TraceIdentifier is the per-connection fallback.
    context.ProblemDetails.Extensions["traceId"] =
        Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
});

// The admin SPA is served from a different origin in development (Vite on :5173), so every call it
// makes to this API is cross-origin and the browser refuses it without this. Origins come from
// configuration and default to the Vite dev server only in Development — see LocalDevelopmentCors for
// why an unconfigured production host admits none.
var corsOrigins = LocalDevelopmentCors.ResolveOrigins(builder.Configuration, builder.Environment);

builder.Services.AddCors(options => options.AddPolicy(
    LocalDevelopmentCors.PolicyName,
    policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        // Location is not one of the seven CORS-safelisted response headers, so without this the SPA
        // can see the 201 and not the header telling it where the thing it just created lives —
        // POST /students and POST /students/{id}/cards both set it deliberately, and a client would
        // have to guess or re-query. Exposed by name rather than by wildcard: "*" is ignored outright
        // by any request that carries credentials, so a header list that works today and silently
        // stops working when Phase 6 adds auth is worse than one that names what it means.
        .WithExposedHeaders("Location")));
// No AllowCredentials, deliberately — see LocalDevelopmentCors.

// The one seam into Infrastructure: SQL Server, EamsDbContext, and the service implementations.
// Nothing else in this project can see an Infrastructure type — they are all internal.
builder.Services.AddEamsInfrastructure(builder.Configuration);

var app = builder.Build();

// Converts anything that escapes a controller into a ProblemDetails body. Registered first among
// our middleware so it wraps every later one.
//
// It sits *inside* the developer exception page, not ahead of it: WebApplicationBuilder inserts
// UseDeveloperExceptionPage before any user middleware, so ours is the inner handler. That is
// exactly why it wins in Development — it catches the exception on the way out and never rethrows,
// so the outer page has nothing to render and the stack trace goes to the log, never the response.
// (DevelopmentApiFactory boots a real Development host to prove this; a Production-only test would
// have passed for the wrong reason.)
app.UseExceptionHandler();

// Inside the exception handler, which is Microsoft's documented order (exception handler first, CORS
// after routing) and the one that stays correct if the middleware ever changes.
//
// It is worth knowing *why* this is not fragile, because the obvious worry is real:
// ExceptionHandlerMiddleware clears the response before writing its ProblemDetails, so headers written
// on the way down would be lost on a 500 — and a browser that cannot read an error body reports a CORS
// failure instead of surfacing the traceId. CorsMiddleware does not write on the way down; it registers
// a Response.OnStarting callback that runs after that clearing. Measured, not assumed: with UseCors
// moved ahead of UseExceptionHandler, CorsTests still passes, so the order here is convention and
// defence-in-depth rather than the thing holding the behaviour up. What CorsTests *does* pin is the
// behaviour itself — a 500 stays readable cross-origin.
app.UseCors(LocalDevelopmentCors.PolicyName);

// Said out loud on every start, next to the authorization warning, because a policy that admits
// nothing looks identical to one that is working until a browser says otherwise.
if (corsOrigins.Length == 0)
{
    app.Logger.LogWarning(
        "CORS policy '{Policy}' admits NO origin: '{Setting}' is not configured and the Vite " +
        "dev-server default applies only in Development. Every cross-origin browser call will be " +
        "refused.",
        LocalDevelopmentCors.PolicyName, LocalDevelopmentCors.ConfigurationSection);
}
else
{
    app.Logger.LogInformation(
        "CORS policy '{Policy}' admits {Origins}. Set '{Setting}' to change it.",
        LocalDevelopmentCors.PolicyName, string.Join(", ", corsOrigins),
        LocalDevelopmentCors.ConfigurationSection);
}

// Said out loud on every start: nothing here is protected (ADR-001 D-6, Technical Plan §11).
AuthorizationStatus.LogEnforcementState(app.Logger);

// Migrate on start, and seed dev convenience data only outside Production. All idempotent:
// migrations skip what is already applied, seeding returns early once a school row exists. This
// also pins the development tenant for §11's SchoolId query filter and logs which school won.
await app.Services.InitializeEamsDatabaseAsync(seed: !app.Environment.IsProduction());

// Development only. Swagger UI is an unauthenticated, complete description of an API whose
// endpoints are all open (ADR-001 D-6) — publishing it from a production host hands an attacker
// the map as well as the door. The generator stays registered so the document can still be built
// for tooling; only the endpoints are gated.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "EAMS API v1");
        o.RoutePrefix = string.Empty; // Swagger UI at the root.
    });
}

// Auth is stubbed for the mock build — endpoints are open. See AuthorizationStatus.
app.MapControllers();

app.Run();
