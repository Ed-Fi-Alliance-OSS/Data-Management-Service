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
/// age, so a wall-clock step can never extend trust in a snapshot. Defaults apply: refresh 300 s, max staleness 3600 s.
/// The cooldown and retry gate still compare wall-clock time until R3.2.
/// </summary>
public class SigningKeySnapshotProviderClockTests
{
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

    // (b): a wall clock set back does not keep a snapshot fresh once the refresh interval has elapsed. Admission of the
    // background load that an overdue snapshot requests is pinned in R3.2, where the retry gate moves to elapsed time.
    [TestFixture]
    public class Given_the_wall_clock_set_back_and_the_refresh_interval_elapsed
    {
        private SigningKeySnapshot _warm = null!;
        private SigningKeySnapshotState _state;
        private SigningKeySnapshot _served = null!;

        [SetUp]
        public async Task Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            _warm = await provider.GetUsableAsync(CancellationToken.None);

            time.StepWallClock(TimeSpan.FromHours(-2));
            time.Advance(TimeSpan.FromSeconds(301));
            _state = provider.Status.State;
            _served = await provider.GetUsableAsync(CancellationToken.None);
        }

        [Test]
        public void It_is_overdue() => _state.Should().Be(SigningKeySnapshotState.Overdue);

        [Test]
        public void It_serves_the_overdue_snapshot() => _served.Should().BeSameAs(_warm);
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
}
