// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Galaxies.Api.Auth;
using Galaxies.ControlPlane.Model;

namespace Galaxies.Api;

/// <summary>
/// The authorization rules at the API boundary (design Section C.3, rules R1 to
/// R7). Every check is here so no endpoint can forget one. The golden rule is R3:
/// the caller's empire is derived from the session plus membership, never taken
/// from the request body, so a player cannot act as another empire.
/// </summary>
public static class Authorization
{
    /// <summary>R1: a valid session is required.</summary>
    public static SessionPrincipal RequireSession(SessionPrincipal? principal)
        => principal ?? throw ApiProblem.Unauthorized();

    /// <summary>R7: the caller must hold a role (moderator/admin gates).</summary>
    public static void RequireRole(SessionPrincipal principal, string role)
    {
        if (!principal.HasRole(role))
        {
            throw ApiProblem.Forbidden($"Requires role '{role}'.");
        }
    }

    /// <summary>
    /// R7: the caller must hold at least one of the roles. Moderation gates use
    /// this so an admin is not refused for lacking the literal "moderator" role;
    /// admin is a superset of moderator everywhere in the service.
    /// </summary>
    public static void RequireAnyRole(SessionPrincipal principal, params string[] roles)
    {
        if (!roles.Any(principal.HasRole))
        {
            throw ApiProblem.Forbidden($"Requires one of these roles: {string.Join(", ", roles)}.");
        }
    }

    /// <summary>R2/R7: the caller must be the game's host.</summary>
    public static void RequireHost(SessionPrincipal principal, GameMeta game)
    {
        if (game.HostAccountId != principal.GoogleSub && !principal.HasRole("admin"))
        {
            throw ApiProblem.Forbidden("Only the host may do this.");
        }
    }

    /// <summary>
    /// R2 + R3: resolve the caller's own empire in a game from membership. The
    /// empire id is never read from the request. Throws 403 if the caller is not a
    /// member.
    /// </summary>
    public static Member ResolveOwnEmpire(SessionPrincipal principal, IReadOnlyList<Member> members)
    {
        Member? mine = members.FirstOrDefault(m => m.AccountId == principal.GoogleSub && !m.Resigned);
        return mine ?? throw ApiProblem.Forbidden("You are not a player in this game.");
    }

    /// <summary>
    /// The seat named by an internal AI submission must actually be AI driven. A
    /// human seat is never submittable through the internal route, whether it was an
    /// AI from the lobby or a seat the missed-turn ladder handed over: both set
    /// Kind = Ai, and nothing else does. This is the whole authorization story for
    /// the internal orders path, since Cloud Run IAM has already established that
    /// the CALLER is galaxies-ai; what remains is proving the SEAT is one it may act
    /// for.
    /// </summary>
    public static void RequireAiSeat(Member seat)
    {
        if (seat.Kind != PlayerKind.Ai)
        {
            throw ApiProblem.Forbidden($"Empire {seat.EmpireId} is a human seat; it cannot submit AI orders.");
        }
        if (seat.Resigned)
        {
            throw ApiProblem.Conflict($"Empire {seat.EmpireId} has resigned.");
        }
    }

    /// <summary>R5: intel and orders are per empire; assert the target is the caller's own.</summary>
    public static void RequireOwnEmpire(Member callerEmpire, int targetEmpireId)
    {
        if (callerEmpire.EmpireId != targetEmpireId)
        {
            throw ApiProblem.Forbidden("You may only access your own empire's view.");
        }
    }

    /// <summary>
    /// R4: orders must target the open turn. A stale turn year is a 409 (the world
    /// moved on); the caller should re-read status and resubmit.
    /// </summary>
    public static void RequireOpenTurn(GameMeta game, int orderTurnYear)
    {
        if (game.Generation == GenerationState.Generating)
        {
            throw ApiProblem.Conflict("A turn is being generated; orders are closed.");
        }
        if (orderTurnYear != game.TurnYear)
        {
            if (orderTurnYear < game.TurnYear)
            {
                throw ApiProblem.Gone();
            }
            throw ApiProblem.Conflict($"Orders are for turn {orderTurnYear} but the open turn is {game.TurnYear}.");
        }
    }
}
