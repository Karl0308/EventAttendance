using System.Diagnostics;
using EAMS.Api;
using EAMS.Api.Authentication;
using EAMS.Api.Authorization;
using EAMS.Api.Controllers;
using EAMS.Api.Cors;
using EAMS.Api.Identity;
using EAMS.Api.MultiTenancy;
using EAMS.Api.OpenApi;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.IdentityModel.Tokens;
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

// §11's signing key, validated while the host is still being described. The host REFUSES TO START on
// a missing, too-short or placeholder key — see JwtOptions for why a generated default would be worse
// than a refusal in both development and production.
//
// Deliberately AFTER AddEamsInfrastructure: a host missing both a connection string and a key must
// report the connection string first, because that is the setting an operator configures first and
// because HostPipelineTests' connection-string refusal has to keep failing for its own reason. The
// resolved options are consumed by the Bearer scheme below and by TokenIssuer, which read this one
// instance — so a token this host mints is a token this host accepts by construction rather than by
// two call sites agreeing on a key.
var jwt = JwtOptions.Resolve(builder.Configuration);
builder.Services.AddSingleton(jwt);

// The only thing in this system that signs a token (D-74). Singleton: it is stateless once the
// credentials are built, and rebuilding an HMAC key per request is pure cost.
builder.Services.AddSingleton<TokenIssuer>();

// The tight (email, ip) + email anti-guess limiter. Singleton because its partition tables ARE its
// memory — a scoped instance would mint a fresh, empty limiter per request and refuse nothing.
builder.Services.AddSingleton<AuthAccountLimiter>();

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
    .AddScheme<DeviceKeyOptions, DeviceKeyHandler>(DeviceKey.AuthenticationScheme, _ => { })

    // ------------------------------------------------------------------------- human authentication
    //
    // Phase 6b, and it is ADDITIVE in the literal sense: this line sits beside the device scheme
    // rather than instead of it, exactly as DeviceKeyHandler's remarks predicted. The device handler
    // returns NoResult when a request carries no DeviceKey header, so the two never contend, and no
    // endpoint outside /auth carries [Authorize] for this scheme — every other route behaves exactly
    // as it did before this line existed. AuthStagedCutoverTests asserts that rather than assuming it.
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        // ⚠ THE SHARPEST TRAP IN THIS PHASE. LEAVE THIS FALSE.
        //
        // The default is true, and what it does is rewrite well-known short claim names into the
        // WS-Federation URIs ClaimTypes.* uses — `sub` becomes
        // `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`. It does NOT touch
        // `school_id` or `perm`, because those are not in its map.
        //
        // So with the default: every permission policy still passes, ClaimsSchoolContext still
        // resolves the right tenant, /auth/me still answers — and ClaimsCurrentUser.UserId silently
        // returns null, because it looks for `sub` and `sub` is gone. Every audited write on the
        // endpoint Technical Plan §6.4 specifically calls audited would be attributed to nobody, with
        // a fully green test suite. The failure is invisible in every direction except the one nobody
        // checks.
        //
        // EamsClaimTypes exists to make "both schemes emit the same claim types" checkable; this flag
        // is what makes it true on the read side. JwtBearerClaimMappingTests fails if it is flipped.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            // `sub` is the identity.
            //
            // RoleClaimType is deliberately NOT set, and not set to null either — the setter throws
            // IDX10103 on null or whitespace, which AuthTokenCompositionTests found before any host
            // did: the options delegate runs lazily on the first authenticated request, so a null here
            // would have started fine, served every open endpoint, and thrown on the first sign-in.
            // The default (the WS-Federation role URI) is exactly right: this system mints no role
            // claim of any kind, so nothing ever matches it and User.IsInRole is permanently false.
            // Authorization here is permission-based (EamsPermissions); a role vocabulary that no
            // policy consults is one a future endpoint would eventually branch on.
            NameClaimType = EamsClaimTypes.Subject,

            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,

            ValidateAudience = true,
            ValidAudience = jwt.Audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = TokenIssuer.SigningKey(jwt),

            // Pinned, and this is the one validation setting whose absence is a vulnerability rather
            // than a laxity. Without it the token's own `alg` header decides how it is verified, which
            // is the whole of the `alg: none` and RS256-to-HS256 confusion families. One algorithm,
            // named in one place, shared with the minting side.
            ValidAlgorithms = [TokenIssuer.Algorithm],

            ValidateLifetime = true,

            // Zero, not the five-minute default. A fifteen-minute access token whose lifetime IS its
            // revocation window (see JwtOptions.AccessTokenLifetime) must not quietly be a
            // twenty-minute one — five minutes of slack is a third of the budget.
            ClockSkew = TimeSpan.Zero,
        };

        options.Events = new JwtBearerEvents
        {
            // The framework default is a bare 401 with no body — the same gap DeviceKeyHandler closed
            // for the device scheme. §6's header declares RFC 7807 for every error on this API, and a
            // caller needs one accessor to branch on and one traceId to quote back.
            OnChallenge = async context =>
            {
                context.HandleResponse();

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.Append(
                    Microsoft.Net.Http.Headers.HeaderNames.WWWAuthenticate,
                    JwtBearerDefaults.AuthenticationScheme);

                var factory = context.HttpContext.RequestServices
                    .GetRequiredService<ProblemDetailsFactory>();

                var problem = factory.CreateProblemDetails(
                    context.HttpContext,
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Sign-in is required.",
                    // Deliberately says nothing about WHY a presented token failed. "Expired",
                    // "wrong signature" and "wrong audience" are three different facts about a key an
                    // attacker is probing, and the client's action is the same in all three: refresh,
                    // then sign in.
                    detail:
                        "This endpoint requires 'Authorization: Bearer <accessToken>'. Obtain one " +
                        "from POST /api/v1/auth/login, or renew an expired one with " +
                        "POST /api/v1/auth/refresh.",
                    instance: context.Request.Path);

                problem.Extensions["code"] = AuthController.InvalidCredentialsCode;

                await context.HttpContext.RequestServices
                    .GetRequiredService<IProblemDetailsService>()
                    .WriteAsync(new ProblemDetailsContext
                    {
                        HttpContext = context.HttpContext,
                        ProblemDetails = problem,
                    });
            },
        };
    });

// The policy name and the claim value are the same string on purpose: a policy that exists and demands
// nothing is then not expressible. §11 scopes a device key to `attendance.capture` and nothing else, so
// this is the whole of what a device may do — and a Phase 6 JWT user carrying the same permission
// satisfies the same policy, which is what makes the authorization layer principal-agnostic.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        EamsPermissions.AttendanceCapture,
        policy => policy
            .AddAuthenticationSchemes(DeviceKey.AuthenticationScheme)
            .RequireClaim(EamsClaimTypes.Permission, EamsPermissions.AttendanceCapture));

    // The first §11 policy that gates a *read*, and the first one bound to a person rather than a
    // device. Bearer only, deliberately: a device key is scoped to `attendance.capture` and must not
    // be able to satisfy an operator's permission by accident, which is what a policy naming no
    // scheme would allow once several schemes are registered.
    //
    // It gates exactly one action today - GET /events/{id}/scans - because that endpoint is new,
    // nothing consumes it yet, and no device-key route can reach it. Enforcing the rest of §11 is a
    // deliberate later step, not something that should follow from this one existing.
    options.AddPolicy(
        EamsPermissions.EventsRead,
        policy => policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireClaim(EamsClaimTypes.Permission, EamsPermissions.EventsRead));
});

// §14: "rate limiting on /auth and /attendance/tap". Partitioned by device_id — see CaptureRateLimiting
// for why that forces the limiter to sit *after* authentication, and what the per-instance limitation
// means behind several SaaS instances.
builder.Services.AddCaptureRateLimiter(AuthRateLimiting.AddAuthPolicies);

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

// The third seam, and the last of the family ADR-001 D-6 created (Phase 6b). It changes nothing about
// any existing endpoint: no route outside /auth accepts a Bearer token, so there is no principal for
// it to find on any of them and it answers null exactly as UnauthenticatedCurrentUser did. What it
// buys is that attribution is correct on the day enforcement arrives — a NULL RecordedByUserId cannot
// be backfilled afterwards, which is why this seam existed before there was anything to put in it.
builder.Services.Replace(ServiceDescriptor.Scoped<ICurrentUser, ClaimsCurrentUser>());

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

// Said out loud on every start, beside the CORS line, because it fails the same way a wrong CORS
// origin does — silently, in the browser, with nothing in any server log.
//
// The refresh cookie's Path is derived per request from Request.PathBase, which IIS sets from the
// application alias. Under docs/DEPLOY-IIS.md the API is an application named `eamsapi` under Default
// Web Site, so the real path is `/eamsapi/api/v1/auth`. If the cookie is written at a path the browser
// does not consider a prefix of the refresh URL, it is never sent back: POST /auth/refresh arrives
// with no cookie and answers 401 forever, and nothing anywhere says why. AuthCookies logs the RESOLVED
// value the first time a cookie is actually issued; this line states the rule, so an operator reading
// a cold start knows what to look for.
// Note the repeated {Suffix}: Microsoft.Extensions.Logging templates are POSITIONAL despite the
// names, so a placeholder used twice needs the value passed twice. Getting that wrong throws a
// FormatException from inside the logger at startup, which is a failure mode worth one comment.
app.Logger.LogInformation(
    "Refresh-token cookie Path is the request's PathBase followed by '{Suffix}', resolved per " +
    "request. The resolved value is logged once when the first session cookie is issued. A path the " +
    "browser does not send back means POST {Suffix2}/refresh answers 401 with nothing in any log.",
    AuthCookies.RefreshCookiePathSuffix, AuthCookies.RefreshCookiePathSuffix);

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
// The RBAC reference data is no longer on the same switch. Permissions and the four Roles with their
// grants are written in EVERY environment — enforcement over an empty RolePermissions table
// authorizes nobody, so a Production install that skipped them would lock every operator out of
// itself and look like a broken authorization layer while doing it. Only the convenience data below
// rides IsDevelopment(). See InitializeEamsDatabaseAsync.
//
// The grant matrix is passed in from here because it lives beside the endpoints whose codes it grants
// (EamsRoles) and Infrastructure cannot see this assembly.
await app.Services.InitializeEamsDatabaseAsync(
    EamsRoles.ReferenceData,
    app.Environment.EnvironmentName,
    seedDevelopmentData: app.Environment.IsDevelopment());

// ------------------------------------------------------------------------------ create-admin
//
// The Production bootstrap: `dotnet run -- create-admin --email … --name "…"`. It runs on the built
// host — which is what composes the container and the configuration chain — and returns an exit code
// instead of falling through to app.Run(), so no port is ever opened. Placed after the initialization
// above because it needs the schema and the four roles, and before everything below because none of
// the middleware or endpoint wiring is any of its business.
if (CreateAdminCommand.IsRequested(args))
{
    return await CreateAdminCommand.RunAsync(app.Services, args, Console.Out);
}

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

// Reached only when the host shuts down cleanly. It exists because the create-admin branch above
// returns an exit code, which makes this entry point int-returning — the compiler then requires every
// path to say what it returns, and "the web host stopped normally" is 0.
return 0;
