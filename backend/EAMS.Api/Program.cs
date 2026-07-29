using System.Diagnostics;
using EAMS.Api;
using EAMS.Api.Authentication;
using EAMS.Api.Authorization;
using EAMS.Api.Cors;
using EAMS.Api.Identity;
using EAMS.Api.MultiTenancy;
using EAMS.Api.OpenApi;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Infrastructure;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DeviceKey = EAMS.Domain.DeviceKey;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Replaces MVC's DefaultProblemDetailsFactory, which is what builds [ApiController]'s automatic
// model-validation 400s — the most common error body in the API and the only one that carried no
// traceId, because those never pass through AddProblemDetails' CustomizeProblemDetails below. See
// TracedProblemDetailsFactory. Replace rather than Add: AddControllers registers the default with
// TryAddSingleton, so an Add here would be ignored and the fix would silently do nothing.
builder.Services.Replace(
    ServiceDescriptor.Singleton<ProblemDetailsFactory, TracedProblemDetailsFactory>());

// Phase 4e: the generated OpenAPI document is the published contract, replacing the hand-written
// docs/api/attendance-contract-handoff.md that could drift from the code with nothing failing.
// Registered unconditionally — only the endpoints that *serve* it are Development-gated, forty lines
// below. See EamsOpenApi.
builder.Services.AddEamsOpenApi();

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

// ---------------------------------------------------------------------------- device authentication
//
// Phase 4b (Phase 4a design, D-23). ADR-001 D-6 deferred *authorization* — §11's permission-based RBAC
// over human users — not *authentication*. This adds authentication for one principal type, a device,
// emitting exactly the claim types Phase 6's JWT will emit, so Phase 6 adds `.AddJwtBearer("Bearer")`
// beside this line rather than replacing what is here.
//
// D-6's operational constraint is unchanged and still binding: four endpoints are gated; everything
// else in this API is open and unauthenticated, and this build must not be exposed beyond local or
// development use until §11 lands in full. See AuthorizationStatus, which says so on every start.
builder.Services
    .AddAuthentication(DeviceKey.AuthenticationScheme)
    .AddScheme<DeviceKeyOptions, DeviceKeyHandler>(DeviceKey.AuthenticationScheme, _ => { });

// The policy name and the claim value are the same string on purpose: a policy that exists and demands
// nothing is then not expressible. §11 scopes a device key to `attendance.capture` and nothing else, so
// this is the whole of what a device may do — and a Phase 6 JWT user carrying the same permission
// satisfies the same policy, which is what makes the authorization layer principal-agnostic.
builder.Services.AddAuthorization(options => options.AddPolicy(
    EamsPermissions.AttendanceCapture,
    policy => policy
        .AddAuthenticationSchemes(DeviceKey.AuthenticationScheme)
        .RequireClaim(EamsClaimTypes.Permission, EamsPermissions.AttendanceCapture)));

// §14: "rate limiting on /auth and /attendance/tap". Partitioned by device_id — see CaptureRateLimiting
// for why that forces the limiter to sit *after* authentication, and what the per-instance limitation
// means behind several SaaS instances.
builder.Services.AddCaptureRateLimiter();

// Both seams become claims-reading here, replacing the Infrastructure defaults registered above.
// Scoped, because the answer is now a property of the request rather than of the process.
//
// ClaimsSchoolContext is Phase 6's implementation with exactly one branch to delete — the fallback to
// the pinned development school for a request that carries no credentials, which is still most of them.
// This is the largest "build toward the seam" win in the phase: every §11 global query filter now runs
// against a tenant that can move, which is the condition under which its failure modes appear at all.
builder.Services.AddHttpContextAccessor();
builder.Services.Replace(ServiceDescriptor.Scoped<ISchoolContext, ClaimsSchoolContext>());
builder.Services.Replace(ServiceDescriptor.Scoped<IDeviceContext, ClaimsDeviceContext>());

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

// Migrate on start, and seed dev convenience data in Development only. All idempotent: migrations
// skip what is already applied, seeding returns early once a school row exists. This also pins the
// development tenant for §11's SchoolId query filter and logs which school won.
//
// The gate is IsDevelopment(), not !IsProduction(), and that changed in Phase 4b. The seed now
// includes a device row carrying a *working* well-known API key (SeedData.DevelopmentKioskApiKey),
// and the only thing that makes a hard-coded credential in source acceptable is that it cannot exist
// where it could be reached. "Not Production" admits Staging — a real, network-reachable host — so it
// was the wrong predicate for that claim. Note the asymmetry it would otherwise leave twenty lines
// below: Swagger, a strictly lesser exposure, is already gated on IsDevelopment().
//
// The whole seed moves rather than the device row alone. Splitting the predicate would trade one
// asymmetry for a subtler one, and eight fictional students in a Staging database a client might look
// at is its own small hazard.
await app.Services.InitializeEamsDatabaseAsync(seed: app.Environment.IsDevelopment());

// Development only. Swagger UI is an unauthenticated, complete description of an API whose
// endpoints are all open (ADR-001 D-6) — publishing it from a production host hands an attacker
// the map as well as the door. The generator stays registered so the document can still be built
// for tooling; only the endpoints are gated.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint($"/swagger/{EamsOpenApi.DocumentName}/swagger.json", "EAMS API v1");
        o.RoutePrefix = string.Empty; // Swagger UI at the root.
    });
}

// ------------------------------------------------------------------------------ the gated pipeline
//
// Routing is called explicitly rather than left to the framework's implicit insertion, because the
// three middlewares below have to sit between it and the endpoints, in this order, and an implicit
// UseRouting would put them all on the wrong side of it.
app.UseRouting();

// Runs on every request. The DeviceKey handler returns NoResult when the request carries no
// Authorization header of its scheme, so the open endpoints are entirely unaffected.
app.UseAuthentication();

// After authentication, not before, and that is deliberate — the partition key is `device_id`, which
// only exists once the key has been verified. CaptureRateLimiting records the trade-off: the usual
// "limit before you validate" advice assumes an IP partition, and an IP partition would put every
// kiosk behind one campus NAT in a single bucket.
app.UseRateLimiter();

// Only the endpoints carrying [Authorize] are gated (Phase 4a design, D-28) — three of D-28's four
// today, because POST /attendance/tap/batch does not exist until 4d. Everything else in this API is
// still open under ADR-001 D-6.
app.UseAuthorization();

app.MapControllers();

app.Run();
