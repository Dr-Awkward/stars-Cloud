// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using Galaxies.ControlPlane.Scheduling;
using NUnit.Framework;

namespace Galaxies.Tests.ControlPlane
{
    /// <summary>
    /// The deadline scheduler must refuse a target nobody can receive.
    ///
    /// Terraform set no GALAXIES_API_BASE_URL, so Api/Program.cs fell back to
    /// "http://localhost" and every Cloud Tasks deadline was created pointing at the
    /// loopback address of whichever machine ran the API. Cloud Tasks accepted each
    /// task and reported success, so nothing anywhere logged a problem; deadline
    /// driven turn generation simply never happened. A fallback string is a
    /// perfectly valid string, which is what made it invisible.
    ///
    /// The guard runs before the Cloud Tasks client is touched, so these tests need
    /// no credentials and no client.
    /// </summary>
    [TestFixture]
    public class DeadlineSchedulerTests
    {
        private static CloudTasksDeadlineScheduler SchedulerWithFireUrl(string fireUrl)
            => new CloudTasksDeadlineScheduler(
                client: null!,
                new DeadlineSchedulerOptions
                {
                    ProjectId = "roybot",
                    LocationId = "us-central1",
                    QueueId = "galaxies-deadlines",
                    DeadlineFireUrl = fireUrl,
                    InvokerServiceAccount = "sa-invoker@roybot.iam.gserviceaccount.com",
                });

        private static readonly DateTimeOffset Deadline = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        [TestCase("", TestName = "Empty base url")]
        [TestCase("/internal/deadline-fire", TestName = "Relative, which is what an empty base url produces")]
        [TestCase("not a url at all", TestName = "Unparseable")]
        public void ARelativeOrEmptyTargetIsRefused(string fireUrl)
        {
            CloudTasksDeadlineScheduler scheduler = SchedulerWithFireUrl(fireUrl);

            InvalidOperationException? error = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await scheduler.ScheduleDeadlineAsync("roybot:game:2f1a", 2101, Deadline));

            Assert.IsNotNull(error);
            StringAssert.Contains("GALAXIES_API_BASE_URL", error!.Message,
                "The error should name the setting an operator has to fix.");
        }

        /// <summary>
        /// A game with no clock is not an error. This runs before the URL check and
        /// must stay that way, so a game without a deadline never depends on the
        /// scheduler being configured at all.
        /// </summary>
        [Test]
        public void AGameWithNoDeadlineIsNotAffected()
        {
            CloudTasksDeadlineScheduler scheduler = SchedulerWithFireUrl(string.Empty);

            Assert.DoesNotThrowAsync(
                async () => await scheduler.ScheduleDeadlineAsync("roybot:game:2f1a", 2101, deadline: null));
        }

        /// <summary>
        /// An absolute URL passes the guard. It then reaches the Cloud Tasks client,
        /// which is null here, so a NullReferenceException means the guard let it
        /// through, which is exactly what this asserts. Anything else, in particular
        /// an InvalidOperationException, would mean the guard rejected a good URL.
        /// </summary>
        [Test]
        public void AnAbsoluteTargetPassesTheGuard()
        {
            CloudTasksDeadlineScheduler scheduler = SchedulerWithFireUrl(
                "https://galaxies-api-abc123.us-central1.run.app/internal/deadline-fire");

            Assert.ThrowsAsync<NullReferenceException>(
                async () => await scheduler.ScheduleDeadlineAsync("roybot:game:2f1a", 2101, Deadline),
                "An absolute URL should reach the Cloud Tasks client rather than being rejected.");
        }
    }
}
