// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

namespace Galaxies.Api;

/// <summary>
/// A request-level failure with an HTTP status, thrown from the orchestrator and
/// mapped to a response by the endpoint layer (design Section E.5 error table):
/// 400 malformed/unknown command, 401 no/expired session, 403 not member / empire
/// mismatch / role missing, 404 unknown game/turn, 409 wrong turn year / stale
/// If-Match, 410 turn already generated, 429 rate limited.
/// </summary>
public sealed class ApiProblem : Exception
{
    public int Status { get; }
    public string Code { get; }

    public ApiProblem(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    public static ApiProblem Unauthorized(string m = "Authentication required.") => new(401, "unauthorized", m);
    public static ApiProblem Forbidden(string m = "Not permitted.") => new(403, "forbidden", m);
    public static ApiProblem NotFound(string m = "Not found.") => new(404, "not_found", m);
    public static ApiProblem BadRequest(string m) => new(400, "bad_request", m);
    public static ApiProblem Conflict(string m) => new(409, "conflict", m);
    public static ApiProblem Gone(string m = "The turn has already been generated.") => new(410, "gone", m);

    /// <summary>
    /// A quota or rate limit was hit. The message must name the limit, because a
    /// bare "too many requests" leaves the caller nothing to act on.
    /// </summary>
    public static ApiProblem TooManyRequests(string m) => new(429, "quota_exceeded", m);
}
