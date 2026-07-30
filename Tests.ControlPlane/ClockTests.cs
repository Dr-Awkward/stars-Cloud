// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Galaxies.ControlPlane;
using Galaxies.ControlPlane.Model;
using Google.Cloud.Firestore;
using NUnit.Framework;

namespace Galaxies.ControlPlane.Tests;

[TestFixture]
public class ExactlyOnceTests
{
    private static GameMeta ActiveGame(string id, int turnYear) => new()
    {
        GameId = id,
        Lifecycle = GameLifecycle.Active,
        Generation = GenerationState.Idle,
        TurnYear = turnYear,
        MaxTimeBetweenTurnsSeconds = 86400,
        LastGenerationAt = Timestamp.GetCurrentTimestamp(),
    };

    [Test]
    public async Task ConcurrentTriggersProduceExactlyOneWinner()
    {
        var cp = new InMemoryControlPlane();
        await cp.CreateGameAsync(ActiveGame("g1", 2100));

        // Fire 12 concurrent generation triggers for the same (game, turnYear),
        // as the all-submitted path, the deadline task, and the sweep might.
        var claims = await System.Threading.Tasks.Task.WhenAll(
            Enumerable.Range(0, 12).Select(i =>
                cp.TryClaimGenerationAsync("g1", 2100, $"worker-{i}", leaseSeconds: 300)));

        int winners = claims.Count(c => c.Won);
        Assert.That(winners, Is.EqualTo(1), "exactly one trigger may claim a turn");
        Assert.That(claims.Count(c => c.Outcome == ClaimOutcome.HeldByOther), Is.EqualTo(11));
    }

    [Test]
    public async Task CommitAdvancesTurnAndStaleTriggerDrops()
    {
        var cp = new InMemoryControlPlane();
        await cp.CreateGameAsync(ActiveGame("g2", 2100));

        GenerationClaim claim = await cp.TryClaimGenerationAsync("g2", 2100, "w1", 300);
        Assert.That(claim.Won, Is.True);

        bool committed = await cp.CommitGenerationAsync("g2", new GenerationCommit
        {
            TurnYear = 2100, WorkerToken = "w1", NewStatePath = "gs://state/2101.xml",
        });
        Assert.That(committed, Is.True);

        GameMeta after = (await cp.GetGameAsync("g2"))!;
        Assert.That(after.TurnYear, Is.EqualTo(2101), "commit advances the turn year");
        Assert.That(after.Generation, Is.EqualTo(GenerationState.Idle));
        Assert.That(after.Lock, Is.Null);

        // A duplicate trigger for the turn that already generated must drop.
        GenerationClaim stale = await cp.TryClaimGenerationAsync("g2", 2100, "w2", 300);
        Assert.That(stale.Outcome, Is.EqualTo(ClaimOutcome.AlreadyAdvanced));

        // The next turn is claimable again.
        GenerationClaim next = await cp.TryClaimGenerationAsync("g2", 2101, "w3", 300);
        Assert.That(next.Won, Is.True);
    }

    [Test]
    public async Task CommitWithWrongTokenIsRejected()
    {
        var cp = new InMemoryControlPlane();
        await cp.CreateGameAsync(ActiveGame("g3", 2100));
        await cp.TryClaimGenerationAsync("g3", 2100, "owner", 300);

        // A worker whose lease expired and was superseded cannot commit.
        bool committed = await cp.CommitGenerationAsync("g3", new GenerationCommit
        {
            TurnYear = 2100, WorkerToken = "impostor", NewStatePath = "gs://x",
        });
        Assert.That(committed, Is.False);
        Assert.That((await cp.GetGameAsync("g3"))!.TurnYear, Is.EqualTo(2100), "a rejected commit does not advance");
    }

    [Test]
    public async Task ExpiredLeaseCanBeStolen()
    {
        var cp = new InMemoryControlPlane();
        await cp.CreateGameAsync(ActiveGame("g4", 2100));
        // Claim with a zero-second lease so it is already expired.
        await cp.TryClaimGenerationAsync("g4", 2100, "slow", leaseSeconds: 0);
        await System.Threading.Tasks.Task.Delay(20);

        GenerationClaim second = await cp.TryClaimGenerationAsync("g4", 2100, "fast", 300);
        Assert.That(second.Won, Is.True, "an expired lease may be taken over");
    }
}

[TestFixture]
public class CadenceLifecycleTests
{
    [Test]
    public void DeadlineIsAnchorPlusMaxTime()
    {
        Timestamp anchor = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));
        Timestamp? deadline = Cadence.DeadlineFor(anchor, 3600);
        Assert.That(deadline!.Value.ToDateTimeOffset(), Is.EqualTo(anchor.ToDateTimeOffset().AddHours(1)));
    }

    [Test]
    public void NoClockMeansNoDeadline()
        => Assert.That(Cadence.DeadlineFor(Timestamp.GetCurrentTimestamp(), 0), Is.Null);

    [Test]
    public void EveryoneSubmittedWhenCountsMeet()
    {
        var g = new GameMeta { ActivePlayerCount = 3, SubmittedCount = 3 };
        Assert.That(Cadence.EveryoneSubmitted(g), Is.True);
        g.SubmittedCount = 2;
        Assert.That(Cadence.EveryoneSubmitted(g), Is.False);
    }

    [Test]
    public void LifecycleTransitionsAreGuarded()
    {
        Assert.That(Lifecycle.CanTransition(GameLifecycle.Lobby, GameLifecycle.Active), Is.True);
        Assert.That(Lifecycle.CanTransition(GameLifecycle.Active, GameLifecycle.Paused), Is.True);
        Assert.That(Lifecycle.CanTransition(GameLifecycle.Paused, GameLifecycle.Active), Is.True);
        Assert.That(Lifecycle.CanTransition(GameLifecycle.Finished, GameLifecycle.Active), Is.False);
        Assert.Throws<InvalidLifecycleTransitionException>(
            () => Lifecycle.EnsureTransition(GameLifecycle.Lobby, GameLifecycle.Finished));
    }
}

[TestFixture]
public class MissedTurnTests
{
    private static GameMeta Game(MissedTurnPolicy policy, int takeoverAfter = 3) => new()
    { MissedTurnPolicy = policy, AiTakeoverAfterMisses = takeoverAfter };

    [Test]
    public void SubmittedSeatUsesItsOrders()
    {
        var action = MissedTurn.Decide(Game(MissedTurnPolicy.HoldOrders),
            new Member { LastSubmittedTurn = 2100 }, submittedThisTurn: true);
        Assert.That(action, Is.EqualTo(MissedTurnAction.UseSubmittedOrders));
    }

    [Test]
    public void HoldOrdersReusesLastWhenAvailable()
    {
        var withHistory = MissedTurn.Decide(Game(MissedTurnPolicy.HoldOrders),
            new Member { LastSubmittedTurn = 2100 }, submittedThisTurn: false);
        Assert.That(withHistory, Is.EqualTo(MissedTurnAction.HoldOrders));

        var noHistory = MissedTurn.Decide(Game(MissedTurnPolicy.HoldOrders),
            new Member { LastSubmittedTurn = -1 }, submittedThisTurn: false);
        Assert.That(noHistory, Is.EqualTo(MissedTurnAction.NoOrders));
    }

    [Test]
    public void AiTakeoverEscalatesAfterThreshold()
    {
        var underThreshold = MissedTurn.Decide(Game(MissedTurnPolicy.AiTakeover, 3),
            new Member { LastSubmittedTurn = 2100, ConsecutiveMisses = 1 }, submittedThisTurn: false);
        Assert.That(underThreshold, Is.EqualTo(MissedTurnAction.HoldOrders));

        var atThreshold = MissedTurn.Decide(Game(MissedTurnPolicy.AiTakeover, 3),
            new Member { LastSubmittedTurn = 2100, ConsecutiveMisses = 2 }, submittedThisTurn: false);
        Assert.That(atThreshold, Is.EqualTo(MissedTurnAction.HandToAi));
    }
}
