// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiService.Dispatch;

using Galaxies.AiService.Firestore;

using Google.Cloud.Firestore;

/// <summary>What happened when a worker tried to claim a seat-turn.</summary>
public enum RunClaimOutcome
{
    Claimed,          // this worker owns the run; proceed
    AlreadySubmitted, // a previous delivery already submitted this seat's orders
    InFlight,         // another worker holds a live claim
}

/// <param name="Outcome">Whether this worker may proceed.</param>
/// <param name="Attempts">Attempt number this claim represents, starting at 1.</param>
/// <param name="Existing">The run record as it stood, for reporting a no-op.</param>
public readonly record struct RunClaim(RunClaimOutcome Outcome, int Attempts, AiRun? Existing)
{
    public bool Won => Outcome == RunClaimOutcome.Claimed;
}

/// <summary>
/// The Firestore idempotency lock on <c>ai_runs/{gameId}_{turnYear}_{empireId}</c>
/// (spec Section 2.2 step 4).
///
/// Cloud Tasks guarantees at-least-once delivery, so a duplicate dispatch of the
/// same seat-turn is normal traffic, not an error, and it MUST be a no-op. A
/// read-then-write would not give that: two deliveries can both read "no record"
/// before either writes one, and the seat gets played twice. So the claim is a
/// Firestore transaction, which retries on contention and lets exactly one
/// caller observe the pre-claim state.
/// </summary>
public sealed class RunLock
{
    private readonly AiStore store;

    public RunLock(AiStore store)
    {
        this.store = store;
    }

    /// <summary>
    /// Try to claim one seat-turn.
    /// </summary>
    /// <param name="leaseSeconds">
    /// How long the claim is honoured before another worker may steal it. Set it
    /// comfortably above the participant timeout: too short and a slow but
    /// healthy dispatch gets a competitor, too long and a killed instance wedges
    /// the seat until the lease drains.
    /// </param>
    public async Task<RunClaim> TryClaimAsync(
        string gameId,
        int turnYear,
        int empireId,
        int leaseSeconds,
        CancellationToken ct = default)
    {
        DocumentReference doc = store.RunDoc(gameId, turnYear, empireId);

        return await store.Db.RunTransactionAsync(async transaction =>
        {
            DocumentSnapshot snap = await transaction.GetSnapshotAsync(doc, ct);
            Timestamp now = Timestamp.GetCurrentTimestamp();
            Timestamp lease = Timestamp.FromDateTimeOffset(
                now.ToDateTimeOffset().AddSeconds(Math.Max(30, leaseSeconds)));

            if (snap.Exists)
            {
                AiRun existing = snap.ConvertTo<AiRun>();

                // Terminal success. Never replay a seat whose orders already
                // landed: that would overwrite a submission with a second,
                // differently-timed one for the same year.
                if (existing.Status == RunStatus.Submitted)
                {
                    return new RunClaim(RunClaimOutcome.AlreadySubmitted, existing.Attempts, existing);
                }

                // Someone else is mid-dispatch and their lease is still good.
                if (existing.Status == RunStatus.Running
                    && existing.LeaseUntil is { } until
                    && until.ToDateTimeOffset() > now.ToDateTimeOffset())
                {
                    return new RunClaim(RunClaimOutcome.InFlight, existing.Attempts, existing);
                }

                // Everything else is claimable: an expired Running lease (the
                // instance died), or a previous Held/Failed run that a later
                // delivery or the deadline backstop is entitled to retry. Held is
                // deliberately NOT terminal, because a seat that fell back to held
                // orders can still be played properly if the participant recovers
                // before the deadline.
                int attempt = existing.Attempts + 1;
                transaction.Update(doc, new Dictionary<string, object>
                {
                    ["status"] = RunStatus.Running.ToString(),
                    ["attempts"] = attempt,
                    ["started_at"] = now,
                    ["lease_until"] = lease,
                });
                return new RunClaim(RunClaimOutcome.Claimed, attempt, existing);
            }

            AiRun fresh = new()
            {
                GameId = gameId,
                TurnYear = turnYear,
                EmpireId = empireId,
                Status = RunStatus.Running,
                Attempts = 1,
                StartedAt = now,
                LeaseUntil = lease,
            };

            // Create, not Set: if a racing transaction created the document
            // between our read and this write, Firestore aborts and retries the
            // whole delegate, which then takes the snap.Exists branch above.
            transaction.Create(doc, fresh);
            return new RunClaim(RunClaimOutcome.Claimed, 1, null);
        }, cancellationToken: ct);
    }
}
