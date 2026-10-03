// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys.SigningKeyTestSupport;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

/// <summary>
/// Review remediation R3.1 (a)–(e): the provider classifies its snapshot by the larger of the wall-clock and monotonic
/// age, so a wall-clock step can never extend trust in a snapshot. R3.2 (a)–(d): the unknown-key cooldown and the retry
/// delay count elapsed time, so a wall-clock step neither opens nor closes them early. Defaults apply: refresh 300 s,
/// max staleness 3600 s, cooldown 30 s, and a backoff jitter factor of exactly 1 (first backoff 5 s).
/// </summary>
public class SigningKeySnapshotProviderClockTests
{
    private static readonly TimeSpan _tick = TimeSpan.FromTicks(1);

    private static SkewableTimeProvider NewSkewableTime() => new(NewTime());

    // (a): a wall clock set back during a key-store outage does not extend trust past the maximum staleness of elapsed
    // time.
    [TestFixture]
    public class Given_the_wall_clock_set_back_while_the_key_store_fails
    {
        private SigningKeySnapshot _warm = null!;
        private SigningKeySnapshot _servedAtTheBound = null!;
        private SigningKeysUnavailableException _pastTheBound = null!;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            _warm = await provider.GetUsableAsync(CancellationToken.None);

            harness.Fails(new TimeoutException());
            time.StepWallClock(TimeSpan.FromHours(-2));
            time.Advance(TimeSpan.FromSeconds(3600));
            _servedAtTheBound = await provider.GetUsableAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromTicks(1));
            Func<Task> call = () => provider.GetUsableAsync(CancellationToken.None);
            _pastTheBound = (await call.Should().ThrowAsync<SigningKeysUnavailableException>()).Which;
        }

        [Test]
        public void It_serves_the_snapshot_at_exactly_the_bound() =>
            _servedAtTheBound.Should().BeSameAs(_warm);

        [Test]
        public void It_fails_closed_past_the_bound_of_elapsed_time() =>
            _pastTheBound.Reason.Should().Be(SigningKeysUnavailableReason.SnapshotExpired);
    }

    // (b): a wall clock set back does not keep a snapshot fresh once the refresh interval has elapsed, and (since R3.2,
    // with the retry gate on elapsed time) the overdue request admits one background reload.
    [TestFixture]
    public class Given_the_wall_clock_set_back_and_the_refresh_interval_elapsed
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeySnapshot _warm = null!;
        private SigningKeySnapshotState _state;
        private SigningKeySnapshot _served = null!;
        private SigningKeySnapshot? _afterwards;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);
            _warm = await provider.GetUsableAsync(CancellationToken.None);

            _harness.ReturnsKeys("key-1", "key-2");
            time.StepWallClock(TimeSpan.FromHours(-2));
            time.Advance(TimeSpan.FromSeconds(301));
            _state = provider.Status.State;
            Task reloaded = provider.AttemptStateChanged;
            _served = await provider.GetUsableAsync(CancellationToken.None);
            await reloaded.WaitAsync(TimeSpan.FromSeconds(10));
            _afterwards = provider.Current;
        }

        [Test]
        public void It_is_overdue() => _state.Should().Be(SigningKeySnapshotState.Overdue);

        [Test]
        public void It_serves_the_overdue_snapshot() => _served.Should().BeSameAs(_warm);

        [Test]
        public void It_admits_one_background_reload() => _harness.Calls.Should().HaveCount(2);

        [Test]
        public void It_publishes_the_reloaded_keys() => _afterwards!.ContainsKeyId("key-2").Should().BeTrue();
    }

    // (c), failing store: a wall clock set forward past the maximum staleness expires the snapshot at the next request.
    [TestFixture]
    public class Given_the_wall_clock_set_forward_while_the_key_store_fails
    {
        private SigningKeysUnavailableException _exception = null!;
        private int _storeCalls;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            await provider.GetUsableAsync(CancellationToken.None);

            harness.Fails(new TimeoutException());
            time.StepWallClock(TimeSpan.FromHours(2));
            Func<Task> call = () => provider.GetUsableAsync(CancellationToken.None);
            _exception = (await call.Should().ThrowAsync<SigningKeysUnavailableException>()).Which;
            _storeCalls = harness.Calls.Count;
        }

        [Test]
        public void It_fails_closed() =>
            _exception.Reason.Should().Be(SigningKeysUnavailableReason.SnapshotExpired);

        [Test]
        public void It_attempted_one_reload() => _storeCalls.Should().Be(2);
    }

    // (c), healthy store: the same step makes the next request await one load, which it is then served from.
    [TestFixture]
    public class Given_the_wall_clock_set_forward_with_a_healthy_key_store
    {
        private SigningKeySnapshot _warm = null!;
        private SigningKeySnapshot _served = null!;
        private int _storeCalls;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            _warm = await provider.GetUsableAsync(CancellationToken.None);

            harness.ReturnsKeys("key-1", "key-2");
            time.StepWallClock(TimeSpan.FromHours(2));
            _served = await provider.GetUsableAsync(CancellationToken.None);
            _storeCalls = harness.Calls.Count;
        }

        [Test]
        public void It_serves_a_newly_loaded_snapshot()
        {
            _served.Should().NotBeSameAs(_warm);
            _served.ContainsKeyId("key-2").Should().BeTrue();
        }

        [Test]
        public void It_loads_once() => _storeCalls.Should().Be(2);
    }

    // (d): expiry is not latched. Once a forward step is reversed, the age is the monotonic age again, and the same
    // snapshot is served without a load.
    [TestFixture]
    public class Given_a_forward_wall_clock_step_that_is_reversed_while_the_key_store_fails
    {
        private SigningKeySnapshot _warm = null!;
        private SigningKeysUnavailableException _duringTheStep = null!;
        private SigningKeySnapshot _afterTheReversal = null!;
        private int _storeCallsDuringTheStep;
        private int _storeCallsAfterTheReversal;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            _warm = await provider.GetUsableAsync(CancellationToken.None);

            harness.Fails(new TimeoutException());
            time.StepWallClock(TimeSpan.FromHours(2));
            Func<Task> call = () => provider.GetUsableAsync(CancellationToken.None);
            _duringTheStep = (await call.Should().ThrowAsync<SigningKeysUnavailableException>()).Which;
            _storeCallsDuringTheStep = harness.Calls.Count;

            time.StepWallClock(TimeSpan.FromHours(-2));
            _afterTheReversal = await provider.GetUsableAsync(CancellationToken.None);
            _storeCallsAfterTheReversal = harness.Calls.Count;
        }

        [Test]
        public void It_fails_closed_while_the_step_stands() =>
            _duringTheStep.Reason.Should().Be(SigningKeysUnavailableReason.SnapshotExpired);

        [Test]
        public void It_serves_the_same_snapshot_after_the_reversal() =>
            _afterTheReversal.Should().BeSameAs(_warm);

        [Test]
        public void It_loads_nothing_after_the_reversal() =>
            _storeCallsAfterTheReversal.Should().Be(_storeCallsDuringTheStep);
    }

    // (e): a step by itself starts nothing. Only the next evaluation sees it.
    [TestFixture]
    public class Given_a_wall_clock_step_and_no_request
    {
        private SigningKeyProviderStatus _before = null!;
        private SigningKeyProviderStatus _after = null!;
        private int _storeCallsBefore;
        private int _storeCallsAfter;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            await provider.GetUsableAsync(CancellationToken.None);
            _before = provider.Status;
            _storeCallsBefore = harness.Calls.Count;

            time.StepWallClock(TimeSpan.FromHours(2));
            _after = provider.Status;
            _storeCallsAfter = harness.Calls.Count;
        }

        [Test]
        public void It_starts_no_load() => _storeCallsAfter.Should().Be(_storeCallsBefore);

        [Test]
        public void It_makes_no_transition() => _after.StateVersion.Should().Be(_before.StateVersion);

        [Test]
        public void It_reports_the_state_as_of_the_read() =>
            _after.State.Should().Be(SigningKeySnapshotState.Expired);

        [Test]
        public void It_was_fresh_before_the_step() =>
            _before.State.Should().Be(SigningKeySnapshotState.Fresh);
    }

    // R3.2 (a) and (b): the unknown-key cooldown counts elapsed time. Set back, the wall clock cannot keep it closed; set
    // forward, it cannot open it early. The boundary is unchanged: suppressed one tick before, allowed at exactly it.
    [TestFixture(-2)]
    [TestFixture(2)]
    public class Given_a_wall_clock_step_inside_the_unknown_key_cooldown(int stepHours)
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome _oneTickBefore;
        private SigningKeyUnknownKeyOutcome _atTheCooldown;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None);

            _harness.ReturnsKeys("key-1", "key-2");
            time.StepWallClock(TimeSpan.FromHours(stepHours));
            time.Advance(TimeSpan.FromSeconds(30) - _tick);
            _oneTickBefore = await provider.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);

            time.Advance(_tick);
            _atTheCooldown = await provider.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);
        }

        [Test]
        public void It_suppresses_the_refresh_one_tick_before_the_cooldown_elapses() =>
            _oneTickBefore.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_refreshes_at_exactly_the_cooldown() =>
            _atTheCooldown.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedFound);

        [Test]
        public void It_reads_the_store_once_more() => _harness.Calls.Should().HaveCount(2);
    }

    // R3.2 (c): the bootstrap allowance is unchanged by a wall clock set forward. The first unknown-key refresh after an
    // empty first load is still inside the cooldown of elapsed time, so it spends the allowance; the next one is suppressed.
    // A cooldown measured on the wall clock would see the step as elapsed, keep the allowance, and spend it on the second.
    [TestFixture]
    public class Given_a_wall_clock_set_forward_before_the_bootstrap_refresh
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome _first;
        private SigningKeyUnknownKeyOutcome _second;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            _harness = new KeyRepositoryHarness(time);
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None); // empty first load: the allowance is granted

            time.StepWallClock(TimeSpan.FromHours(2));
            _first = await provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None);

            _harness.ReturnsKeys("key-1");
            _second = await provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None);
        }

        [Test]
        public void It_spends_the_allowance_on_the_first_refresh() =>
            _first.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedAbsent);

        [Test]
        public void It_suppresses_the_next_refresh_inside_the_cooldown() =>
            _second.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_reads_the_store_once_more() => _harness.Calls.Should().HaveCount(2);
    }

    // R3.2 (d): the retry delay after a failure counts elapsed time. A wall clock set back cannot keep the gate closed,
    // and one set forward cannot open it early. Refused one tick before the 5 s backoff, admitted at exactly it.
    [TestFixture(-2)]
    [TestFixture(2)]
    public class Given_a_wall_clock_step_during_the_retry_delay(int stepHours)
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyRefreshOutcome _oneTickBefore = null!;
        private SigningKeyRefreshOutcome _atTheDeadline = null!;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.Fails(new TimeoutException());
            using var provider = Provider(_harness, time);
            await provider.RefreshAsync(SigningKeyRefreshTrigger.Startup, CancellationToken.None);

            _harness.ReturnsKeys("key-1");
            time.StepWallClock(TimeSpan.FromHours(stepHours));
            time.Advance(TimeSpan.FromSeconds(5) - _tick);
            _oneTickBefore = await provider.RefreshAsync(
                SigningKeyRefreshTrigger.Timer,
                CancellationToken.None
            );

            time.Advance(_tick);
            _atTheDeadline = await provider.RefreshAsync(
                SigningKeyRefreshTrigger.Timer,
                CancellationToken.None
            );
        }

        [Test]
        public void It_refuses_one_tick_before_the_backoff_elapses() =>
            _oneTickBefore
                .Should()
                .BeOfType<SigningKeyRefreshOutcome.Refused>()
                .Which.Reason.Should()
                .Be(SigningKeyRefusalReason.RetryDelay);

        [Test]
        public void It_admits_at_exactly_the_backoff() =>
            _atTheDeadline.Should().BeOfType<SigningKeyRefreshOutcome.Succeeded>();

        [Test]
        public void It_reads_the_store_once_more() => _harness.Calls.Should().HaveCount(2);
    }
}
