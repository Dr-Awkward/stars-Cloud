// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.
//
// galaxies-api: the public Cloud Run gateway (design Sections C, D, E). Google
// sign-in and first-party sessions, the lobby and lifecycle, orders and intel,
// and the turn clock (deadlines, the exactly-once generation guard, host
// controls). Endpoints are thin; the rules live in GameOrchestrator and
// Authorization. GCP clients are created lazily so /healthz and /version work
// with no credentials.

using Galaxies.Api;
using Galaxies.Api.Auth;
using Galaxies.Api.Generation;
using Galaxies.Api.Storage;
using Galaxies.Api.Wire;
using Galaxies.ControlPlane;
using Galaxies.ControlPlane.Eventing;
using Galaxies.ControlPlane.Firestore;
using Galaxies.ControlPlane.Model;
using Galaxies.ControlPlane.Scheduling;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using Google.Cloud.Tasks.V2;
using Nova.Common.Commands;

// Unqualified Task is the framework task type; the Cloud Tasks message type is not
// used here by its short name.
using Task = System.Threading.Tasks.Task;

var builder = WebApplication.CreateBuilder(args);
IConfiguration cfg = builder.Configuration;

// appsettings.json ships every GALAXIES_* key as an empty string, deliberately, to
// document what has to be supplied at deploy time. That makes `??` the wrong
// operator against them: an empty string is not null, so the fallback never fires
// and the blank value wins. Read through Setting so blank means absent.
//
// This was not theoretical. With the committed appsettings.json the signing key
// below resolved to "" rather than to the dev placeholder, so the API would have
// signed and validated every session token with an empty HMAC key.
string? Setting(string key)
{
    string? value = cfg[key];
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

string Project() => Setting("GALAXIES_PROJECT_ID") ?? "roybot";
string Region() => Setting("GALAXIES_REGION") ?? "us-central1";

// ---- Options -----------------------------------------------------------------
// The two secrets have no safe default. Outside Development the service refuses to
// start without them, rather than booting and minting forgeable sessions or
// validating Google tokens against an audience nobody owns. A service that cannot
// authenticate anybody is not usefully "up", so failing at startup loses nothing
// and makes a misconfigured deploy obvious instead of silent.
const string DevSigningKey = "dev-only-insecure-signing-key-change-me-0123456789";
bool isDevelopment = builder.Environment.IsDevelopment();

string signingKey = Setting("GALAXIES_JWT_SIGNING_KEY")
    ?? (isDevelopment
        ? DevSigningKey
        : throw new InvalidOperationException(
            "GALAXIES_JWT_SIGNING_KEY is required outside Development. It comes from the "
            + "galaxies-jwt-signing-key secret; check the Cloud Run secret mount."));

string googleClientId = Setting("GALAXIES_GOOGLE_CLIENT_ID")
    ?? (isDevelopment
        ? "unset.apps.googleusercontent.com"
        : throw new InvalidOperationException(
            "GALAXIES_GOOGLE_CLIENT_ID is required outside Development. It comes from the "
            + "galaxies-google-client-id secret; check the Cloud Run secret mount."));

builder.Services.AddSingleton(new JwtOptions
{
    Issuer = Setting("GALAXIES_JWT_ISSUER") ?? "galaxies",
    Audience = Setting("GALAXIES_JWT_AUDIENCE") ?? "galaxies-client",
    SigningKey = signingKey,
    AccessTokenMinutes = 60,
});
builder.Services.AddSingleton(new GcsBucketOptions
{
    OrdersBucket = Setting("GALAXIES_ORDERS_BUCKET") ?? "roybot-galaxies-orders",
    IntelBucket = Setting("GALAXIES_INTEL_BUCKET") ?? "roybot-galaxies-intel",
});
// Feature flags and quotas. Everything added after M2 ships dark: a missing or
// unparsable env var reads as false, so the off state is also the default state.
bool Flag(string key) => bool.TryParse(cfg[key], out bool v) && v;
int Number(string key, int fallback) => int.TryParse(cfg[key], out int v) && v > 0 ? v : fallback;

var orchestratorOptions = new OrchestratorOptions
{
    GenerationLeaseSeconds = int.TryParse(cfg["GALAXIES_GEN_LEASE_SECONDS"], out int l) ? l : 300,
    AiOrdersEnabled = Flag("API_AI_ORDERS_ENABLED"),
    AiTakeoverEnabled = Flag("API_AI_TAKEOVER_ENABLED"),
    LaunchGateEnabled = Flag("API_LAUNCH_GATE_ENABLED"),
    MaxGamesPerAccount = Number("GALAXIES_MAX_GAMES_PER_ACCOUNT", 20),
    MaxAiSeatsPerGame = Number("GALAXIES_MAX_AI_SEATS_PER_GAME", 15),
    MaxPageSize = Number("GALAXIES_MAX_PAGE_SIZE", 100),
};
builder.Services.AddSingleton(orchestratorOptions);

// ---- Auth --------------------------------------------------------------------
builder.Services.AddSingleton(sp => new JwtService(sp.GetRequiredService<JwtOptions>()));
builder.Services.AddSingleton<IGoogleIdentityVerifier>(new GoogleIdentityVerifier(googleClientId));

// ---- GCP clients (lazy: constructed on first resolve, not at startup) ---------
builder.Services.AddSingleton(sp => FirestoreDb.Create(Project()));
builder.Services.AddSingleton(sp => StorageClient.Create());
builder.Services.AddSingleton(sp => CloudTasksClient.Create());
builder.Services.AddSingleton<IControlPlane>(sp => new FirestoreControlPlane(
    sp.GetRequiredService<FirestoreDb>(),
    sp.GetRequiredService<ILogger<FirestoreControlPlane>>()));
builder.Services.AddSingleton<IOrdersStore>(sp => new GcsOrdersStore(
    sp.GetRequiredService<StorageClient>(), sp.GetRequiredService<GcsBucketOptions>()));
builder.Services.AddSingleton<IIntelStore>(sp => new GcsIntelStore(
    sp.GetRequiredService<StorageClient>(), sp.GetRequiredService<GcsBucketOptions>()));
builder.Services.AddSingleton<IDeadlineScheduler>(sp => new CloudTasksDeadlineScheduler(
    sp.GetRequiredService<CloudTasksClient>(),
    new DeadlineSchedulerOptions
    {
        ProjectId = Project(),
        LocationId = Region(),
        QueueId = Setting("GALAXIES_DEADLINE_QUEUE") ?? "galaxies-deadlines",
        // Terraform does not set GALAXIES_API_BASE_URL today, so on a real deploy
        // this silently became http://localhost/internal/deadline-fire and every
        // Cloud Tasks deadline was undeliverable. The fallback is kept for local
        // runs but it is not a deployable value; see Documentation/Cloud/M0.md.
        DeadlineFireUrl = (Setting("GALAXIES_API_BASE_URL") ?? "http://localhost") + "/internal/deadline-fire",
        InvokerServiceAccount = Setting("GALAXIES_INVOKER_SA") ?? "sa-invoker@roybot.iam.gserviceaccount.com",
    },
    sp.GetRequiredService<ILogger<CloudTasksDeadlineScheduler>>()));
builder.Services.AddSingleton<ITurnEventPublisher>(sp =>
    PubSubTurnEventPublisher.CreateAsync(Project()).GetAwaiter().GetResult());
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ITurnGenerationInvoker>(sp => new HttpTurnGenerationInvoker(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("turngen"),
    Setting("GALAXIES_TURNGEN_URL") ?? "http://localhost:8081"));
builder.Services.AddSingleton<GameOrchestrator>();
builder.Services.AddSingleton<LaunchOrchestrator>();

var app = builder.Build();

// ---- Cross-cutting: map thrown problems to HTTP ------------------------------
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ApiProblem p) { await Problem(ctx, p.Status, p.Code, p.Message); }
    catch (UnknownCommandException e) { await Problem(ctx, 400, "unknown_command", e.Message); }
    catch (InvalidGoogleTokenException e) { await Problem(ctx, 401, "bad_google_token", e.Message); }
    catch (GameNotFoundException e) { await Problem(ctx, 404, "not_found", e.Message); }
    catch (MemberNotFoundException e) { await Problem(ctx, 404, "not_found", e.Message); }
    catch (InvalidLifecycleTransitionException e) { await Problem(ctx, 409, "conflict", e.Message); }
});

static async Task Problem(HttpContext ctx, int status, string code, string message)
{
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new { code, message });
}

// ---- The session path: validate the bearer once, and reject a banned account --
// A JWT stays cryptographically valid until it expires, so a ban has to be checked
// against live state or a banned player keeps playing for up to an hour. This runs
// before routing, on any request that carries a bearer, so every authenticated
// endpoint inherits the check without remembering to make it. The cost is one small
// Firestore point read per authenticated request; unauthenticated paths
// (/healthz, /version, /auth/*, /internal/*) never touch Firestore here.
const string SessionItemKey = "galaxies.session";

app.Use(async (ctx, next) =>
{
    string? header = ctx.Request.Headers.Authorization.FirstOrDefault();
    string? bearer = header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? header[7..] : null;
    if (bearer is not null)
    {
        SessionPrincipal? principal = ctx.RequestServices.GetRequiredService<JwtService>().Validate(bearer);
        if (principal is not null)
        {
            IControlPlane cp = ctx.RequestServices.GetRequiredService<IControlPlane>();
            if (await cp.GetBanAsync(principal.GoogleSub, ctx.RequestAborted) is not null)
            {
                await Problem(ctx, 403, "banned", "This account is banned.");
                return;
            }
            ctx.Items[SessionItemKey] = principal;
        }
    }
    await next();
});

SessionPrincipal RequireSession(HttpContext ctx)
    => Authorization.RequireSession(ctx.Items.TryGetValue(SessionItemKey, out object? p) ? p as SessionPrincipal : null);

// The M4 launch-gate surface ships dark (design "everything ships dark"): while the
// flag is off, reads answer {"disabled":true} and mutations are refused.
IResult? LaunchGateOff() => orchestratorOptions.LaunchGateEnabled ? null : Results.Ok(new { disabled = true });

void RequireLaunchGate()
{
    if (!orchestratorOptions.LaunchGateEnabled)
    {
        throw ApiProblem.Forbidden("This feature is not enabled (API_LAUNCH_GATE_ENABLED).");
    }
}

// ============================ Meta / health ===================================
app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/readyz", () => Results.Ok("ready"));
// minClientVersion drives the desktop upgrade gate (design Section 7 "Versioning"):
// the client compares its own build to this on startup and prompts an upgrade
// rather than sending orders the server can no longer parse. It comes from env so
// raising the floor is a config change, not a rebuild.
app.MapGet("/version", () => Results.Ok(new VersionResponse(
    Setting("GALAXIES_API_VERSION") ?? "1.0.0",
    Setting("GALAXIES_PROTOCOL_VERSION") ?? "1",
    Setting("GALAXIES_MIN_CLIENT_VERSION") ?? "0.1.0")));

// ============================ Auth ============================================
app.MapPost("/v1/auth/google", async (GoogleLoginRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var verifier = ctx.RequestServices.GetRequiredService<IGoogleIdentityVerifier>();
    var control = ctx.RequestServices.GetRequiredService<IControlPlane>();
    var jwt = ctx.RequestServices.GetRequiredService<JwtService>();

    GoogleIdentity id = await verifier.VerifyAsync(req.IdToken, ct);

    // Refuse at the door as well as on every later request. Without this a banned
    // account can still mint a token, and the ban only bites on the call after it.
    if (await control.GetBanAsync(id.Sub, ct) is not null)
    {
        throw ApiProblem.Forbidden("This account is banned.");
    }

    UserAccount? existing = await control.GetUserAsync(id.Sub, ct);
    string chainId = Guid.NewGuid().ToString("N");
    var user = existing ?? new UserAccount { GoogleSub = id.Sub, Email = id.Email, DisplayName = id.Name };
    user.Email = id.Email;
    if (existing is null) user.DisplayName = id.Name;
    user.AvatarUrl = id.Picture;
    user.RefreshChainId = chainId;
    user.DeletedAt = null;
    await control.UpsertUserAsync(user, ct);

    var principal = new SessionPrincipal(user.GoogleSub, user.Email, user.Roles.ToArray());
    return Results.Ok(new TokenResponse(jwt.MintAccessToken(principal), jwt.MintRefreshToken(user.GoogleSub, chainId), 3600));
});

app.MapPost("/v1/auth/refresh", async (RefreshRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var jwt = ctx.RequestServices.GetRequiredService<JwtService>();
    var control = ctx.RequestServices.GetRequiredService<IControlPlane>();

    var parsed = jwt.ValidateRefresh(req.RefreshToken) ?? throw ApiProblem.Unauthorized("Invalid refresh token.");
    UserAccount user = await control.GetUserAsync(parsed.GoogleSub, ct) ?? throw ApiProblem.Unauthorized();
    if (user.IsDeleted || user.RefreshChainId != parsed.ChainId)
    {
        throw ApiProblem.Unauthorized("Refresh token has been revoked.");
    }
    string newChain = Guid.NewGuid().ToString("N");
    user.RefreshChainId = newChain;
    await control.UpsertUserAsync(user, ct);
    var principal = new SessionPrincipal(user.GoogleSub, user.Email, user.Roles.ToArray());
    return Results.Ok(new TokenResponse(jwt.MintAccessToken(principal), jwt.MintRefreshToken(user.GoogleSub, newChain), 3600));
});

app.MapPost("/v1/auth/logout", async (HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    var control = ctx.RequestServices.GetRequiredService<IControlPlane>();
    UserAccount? user = await control.GetUserAsync(me.GoogleSub, ct);
    if (user is not null) { user.RefreshChainId = null; await control.UpsertUserAsync(user, ct); }
    return Results.NoContent();
});

app.MapGet("/v1/me", async (HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    var control = ctx.RequestServices.GetRequiredService<IControlPlane>();
    UserAccount user = await control.GetUserAsync(me.GoogleSub, ct) ?? throw ApiProblem.NotFound("No account.");
    return Results.Ok(new MeResponse(user.GoogleSub, user.Email, user.DisplayName, user.Roles.ToArray()));
});

app.MapDelete("/v1/account", async (HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    await ctx.RequestServices.GetRequiredService<IControlPlane>().SoftDeleteUserAsync(me.GoogleSub, ct);
    return Results.NoContent();
});

// The DSAR bundle (design Section C.5). Blobs are referenced, not inlined.
app.MapGet("/v1/account/export", async (HttpContext ctx, CancellationToken ct) =>
{
    if (LaunchGateOff() is { } off) return off;
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Launch(ctx).ExportAccountAsync(me, ct));
});

// ============================ Moderation ======================================
// Any authenticated player may file a report; only a moderator may read or resolve.
app.MapPost("/v1/reports", async (CreateReportRequest req, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Launch(ctx).CreateReportAsync(me, req, ct));
});

app.MapGet("/v1/admin/reports", async (HttpContext ctx, CancellationToken ct) =>
{
    if (LaunchGateOff() is { } off) return off;
    SessionPrincipal me = RequireSession(ctx);
    string? status = ctx.Request.Query["status"];
    int limit = int.TryParse(ctx.Request.Query["limit"], out int lim) ? lim : 0;
    return Results.Ok(await Launch(ctx).ListReportsAsync(me, status, limit, ct));
});

app.MapPost("/v1/admin/reports/{reportId}/resolve", async (string reportId, ResolveReportRequest req, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Launch(ctx).ResolveReportAsync(me, reportId, req, ct));
});

app.MapPost("/v1/admin/users/{googleSub}/ban", async (string googleSub, BanRequest req, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Launch(ctx).BanAsync(me, googleSub, req, ct));
});

app.MapDelete("/v1/admin/users/{googleSub}/ban", async (string googleSub, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    await Launch(ctx).UnbanAsync(me, googleSub, ct);
    return Results.NoContent();
});

// ============================ Games / lobby ===================================

// The game browser (design Section 6.2). scope=mine|open|public|finished.
app.MapGet("/v1/games", async (HttpContext ctx, CancellationToken ct) =>
{
    if (LaunchGateOff() is { } off) return off;
    SessionPrincipal me = RequireSession(ctx);
    string? scope = ctx.Request.Query["scope"];
    int limit = int.TryParse(ctx.Request.Query["limit"], out int lim) ? lim : 0;
    int offset = int.TryParse(ctx.Request.Query["offset"], out int off2) ? off2 : 0;
    return Results.Ok(await Launch(ctx).ListGamesAsync(me, scope, limit, offset, ct));
});

app.MapPost("/v1/games", async (CreateGameRequest req, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    GameMeta g = await Orchestrator(ctx).CreateGameAsync(me, req, ct);
    return Results.Created($"/v1/games/{g.GameId}", Summary(g));
});

app.MapGet("/v1/games/{gameId}", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    RequireSession(ctx);
    GameMeta g = await Orchestrator(ctx).GetGameAsync(gameId, ct) ?? throw ApiProblem.NotFound($"No game {gameId}.");
    return Results.Ok(Summary(g));
});

app.MapGet("/v1/games/{gameId}/players", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    RequireSession(ctx);
    var players = await Orchestrator(ctx).GetPlayersAsync(gameId, ct);
    return Results.Ok(players.Select(m => new PlayerResponse(m.EmpireId, m.AccountId, m.Kind.ToString(), m.Race, m.TurnSubmitted)));
});

app.MapPost("/v1/games/{gameId}/join", async (string gameId, JoinGameRequest req, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    Member m = await Orchestrator(ctx).JoinAsync(me, gameId, req, ct);
    return Results.Ok(new PlayerResponse(m.EmpireId, m.AccountId, m.Kind.ToString(), m.Race, m.TurnSubmitted));
});

app.MapPost("/v1/games/{gameId}/start", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    GameMeta g = await Orchestrator(ctx).StartAsync(me, gameId, ct);
    return Results.Ok(Summary(g));
});

// Host adds an AI opponent to an open slot (M3). Never displaces a human.
app.MapPost("/v1/games/{gameId}/players/ai", async (string gameId, AddAiPlayerRequest req, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    Member m = await Launch(ctx).AddAiPlayerAsync(me, gameId, req, ct);
    return Results.Ok(new PlayerResponse(m.EmpireId, m.AccountId, m.Kind.ToString(), m.Race, m.TurnSubmitted));
});

// Host removes a player or an AI before the start.
app.MapDelete("/v1/games/{gameId}/players/{empireId:int}", async (string gameId, int empireId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    await Launch(ctx).RemovePlayerAsync(me, gameId, empireId, ct);
    return Results.NoContent();
});

// Leave before the start; the seat becomes an open slot again.
app.MapPost("/v1/games/{gameId}/leave", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    await Launch(ctx).LeaveAsync(me, gameId, ct);
    return Results.NoContent();
});

// Host abandons the game: deleted if it never started, cancelled if it ran.
app.MapDelete("/v1/games/{gameId}", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    await Launch(ctx).DeleteGameAsync(me, gameId, ct);
    return Results.NoContent();
});

// ============================ Settings (pre-start) ============================
app.MapGet("/v1/games/{gameId}/settings", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    if (LaunchGateOff() is { } off) return off;
    RequireSession(ctx);
    return Results.Ok(await Launch(ctx).GetSettingsAsync(gameId, ct));
});

app.MapPatch("/v1/games/{gameId}/settings", async (string gameId, UpdateGameSettingsRequest req, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Launch(ctx).UpdateSettingsAsync(me, gameId, req, ct));
});

// ============================ Invites =========================================
app.MapPost("/v1/games/{gameId}/invites", async (string gameId, CreateInviteRequest req, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    InviteResponse invite = await Launch(ctx).CreateInviteAsync(me, gameId, req, ct);
    return Results.Created($"/v1/games/{gameId}/invites/{invite.InviteId}", invite);
});

app.MapGet("/v1/games/{gameId}/invites", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    if (LaunchGateOff() is { } off) return off;
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Launch(ctx).ListInvitesAsync(me, gameId, ct));
});

app.MapDelete("/v1/games/{gameId}/invites/{inviteId}", async (string gameId, string inviteId, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    await Launch(ctx).RevokeInviteAsync(me, gameId, inviteId, ct);
    return Results.NoContent();
});

// Accepting binds the seat to the accepting user's google_sub, and only if the
// session's verified email is the invited address.
app.MapPost("/v1/invites/{token}/accept", async (string token, HttpContext ctx, CancellationToken ct) =>
{
    RequireLaunchGate();
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Launch(ctx).AcceptInviteAsync(me, token, ct));
});

// ============================ Game-over summary ===============================
app.MapGet("/v1/games/{gameId}/summary", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    if (LaunchGateOff() is { } off) return off;
    RequireSession(ctx);
    return Results.Ok(await Launch(ctx).GetSummaryAsync(gameId, ct));
});

// ============================ Orders ==========================================
app.MapPut("/v1/games/{gameId}/orders", async (string gameId, OrdersRequest req, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    await Orchestrator(ctx).PutOrdersAsync(me, gameId, req, ct);
    return Results.NoContent();
});

app.MapGet("/v1/games/{gameId}/orders", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    OrdersResponse? o = await Orchestrator(ctx).GetOrdersAsync(me, gameId, ct);
    return o is null ? Results.NoContent() : Results.Ok(o);
});

app.MapPost("/v1/games/{gameId}/orders/submit", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    GameMeta g = await Orchestrator(ctx).SubmitOrdersAsync(me, gameId, ct);
    return Results.Ok(Summary(g));
});

app.MapDelete("/v1/games/{gameId}/orders", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    await Orchestrator(ctx).UnsubmitAsync(me, gameId, ct);
    return Results.NoContent();
});

// ============================ Intel / status ==================================
app.MapGet("/v1/games/{gameId}/intel", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Orchestrator(ctx).GetIntelAsync(me, gameId, null, ct));
});

app.MapGet("/v1/games/{gameId}/intel/{turnYear:int}", async (string gameId, int turnYear, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Orchestrator(ctx).GetIntelAsync(me, gameId, turnYear, ct));
});

app.MapGet("/v1/games/{gameId}/status", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    return Results.Ok(await Orchestrator(ctx).GetStatusAsync(me, gameId, ct));
});

// ============================ Host controls (clock) ===========================
app.MapPost("/v1/games/{gameId}/force-generate", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    await Orchestrator(ctx).ForceGenerateAsync(me, gameId, ct);
    return Results.Accepted();
});

app.MapPost("/v1/games/{gameId}/extend-deadline", async (string gameId, ExtendDeadlineRequest req, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    GameMeta g = await Orchestrator(ctx).ExtendDeadlineAsync(me, gameId, req.AddSeconds, ct);
    return Results.Ok(Summary(g));
});

app.MapPost("/v1/games/{gameId}/pause", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    GameMeta g = await Orchestrator(ctx).HostTransitionAsync(me, gameId, GameLifecycle.Paused, ct);
    return Results.Ok(Summary(g));
});

app.MapPost("/v1/games/{gameId}/resume", async (string gameId, HttpContext ctx, CancellationToken ct) =>
{
    SessionPrincipal me = RequireSession(ctx);
    GameMeta g = await Orchestrator(ctx).HostTransitionAsync(me, gameId, GameLifecycle.Active, ct);
    return Results.Ok(Summary(g));
});

// ============================ Internal (OIDC via Cloud Run IAM) ================
// These are private: Cloud Run requires an OIDC invoker token (sa-invoker) at the
// ingress, so no in-app bearer check is needed (design Section B.5).
app.MapPost("/internal/deadline-fire", async (DeadlineFireRequest req, HttpContext ctx, CancellationToken ct) =>
{
    await Orchestrator(ctx).OnDeadlineFireAsync(req.GameId, req.TurnYear, ct);
    return Results.Ok();
});

app.MapPost("/internal/sweep", async (HttpContext ctx, CancellationToken ct) =>
{
    await Orchestrator(ctx).SweepAsync(ct);
    return Results.Ok();
});

// The route galaxies-ai submits an AI seat's orders through (galaxies-ai spec
// Section 7.3). Like the routes above it is private: Cloud Run requires an OIDC
// invoker token at the ingress, so there is no in-app bearer check. The seat's own
// Kind is what authorizes the submission; a human seat is refused.
app.MapPost("/internal/v1/games/{gameId}/seats/{empireId:int}/orders",
    async (string gameId, int empireId, AiOrdersRequest req, HttpContext ctx, CancellationToken ct) =>
{
    return Results.Ok(await Orchestrator(ctx).SubmitAiOrdersAsync(gameId, empireId, req, ct));
});

app.Run();

// ---- Local helpers -----------------------------------------------------------
static GameOrchestrator Orchestrator(HttpContext ctx) => ctx.RequestServices.GetRequiredService<GameOrchestrator>();

static LaunchOrchestrator Launch(HttpContext ctx) => ctx.RequestServices.GetRequiredService<LaunchOrchestrator>();

static GameSummaryResponse Summary(GameMeta g) => new(
    g.GameId, g.Name, g.Lifecycle.ToString(), g.Generation.ToString(), g.TurnYear,
    g.DeadlineAt?.ToDateTimeOffset(), g.ActivePlayerCount, g.SubmittedCount, g.IsPublic);
