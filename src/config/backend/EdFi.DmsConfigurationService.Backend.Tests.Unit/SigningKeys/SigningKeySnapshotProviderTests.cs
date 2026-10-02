// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys.SigningKeyTestSupport;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

/// <summary>
/// Spec §5 step 1.5 (a)–(q): the provider over a faked repository and fake time, with no timer. Defaults apply unless
/// a fixture says otherwise: refresh 300 s, max staleness 3600 s, cooldown 30 s, load timeout 10 s, and a jitter
/// factor of exactly 1 (backoff 5, 10, 20, 40, 60 s).
/// </summary>
public class SigningKeySnapshotProviderTests
{
    private sealed class FakeDbException(string message) : DbException(message);

    // (a): 32 callers arrive while the cold load runs and 32 more just after it publishes. Every caller must receive
    // the one published snapshot from the one load; a provider that loads on every call fails the second batch.
    [TestFixture]
    public class Given_64_concurrent_cold_callers
    {
        private KeyRepositoryHarness _harness = null!;
        private int _callsWhileGated;
        private SigningKeySnapshot[] _results = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            var gate = _harness.Gate();
            using var provider = Provider(_harness, time);

            Task<SigningKeySnapshot>[] callers =
            [
                .. Enumerable
                    .Range(0, 32)
                    .Select(_ => Task.Run(() => provider.GetUsableAsync(CancellationToken.None))),
            ];
            await _harness.WaitForCallsAsync(1);
            await Task.Delay(100);
            _callsWhileGated = _harness.Calls.Count;

            gate.SetResult([KeyRepositoryHarness.Row("key-1")]);
            SigningKeySnapshot[] duringTheLoad = await Task.WhenAll(callers).Bounded();

            Task<SigningKeySnapshot>[] lateCallers =
            [
                .. Enumerable
                    .Range(0, 32)
                    .Select(_ => Task.Run(() => provider.GetUsableAsync(CancellationToken.None))),
            ];
            SigningKeySnapshot[] afterTheLoad = await Task.WhenAll(lateCallers).Bounded();
            _results = [.. duringTheLoad, .. afterTheLoad];
        }

        [Test]
        public void It_shares_one_load_while_it_runs() => _callsWhileGated.Should().Be(1);

        [Test]
        public void It_reads_the_store_once() => _harness.Calls.Should().HaveCount(1);

        [Test]
        public void It_gives_every_caller_the_same_snapshot() =>
            _results.Should().OnlyContain(snapshot => ReferenceEquals(snapshot, _results[0]));

        [Test]
        public void It_answers_all_64() => _results.Should().HaveCount(64);
    }

    // (b)
    [TestFixture]
    public class Given_a_waiter_canceled_during_a_shared_cold_load
    {
        private KeyRepositoryHarness _harness = null!;
        private Exception? _canceledWaiter;
        private SigningKeySnapshot _survivor = null!;
        private SigningKeyProviderStatus _status = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            var gate = _harness.Gate();
            using var provider = Provider(_harness, time);
            using CancellationTokenSource waiterToken = new();

            Task<SigningKeySnapshot> canceled = provider.GetUsableAsync(waiterToken.Token);
            Task<SigningKeySnapshot> survivor = provider.GetUsableAsync(CancellationToken.None);

            await waiterToken.CancelAsync();
            try
            {
                await canceled.Bounded();
            }
            catch (OperationCanceledException exception)
            {
                _canceledWaiter = exception;
            }

            gate.SetResult([KeyRepositoryHarness.Row("key-1")]);
            _survivor = await survivor.Bounded();
            _status = provider.Status;
        }

        [Test]
        public void It_fails_only_the_canceled_waiter() =>
            _canceledWaiter.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_leaves_the_shared_load_running() =>
            _harness.Calls.Single().Token.IsCancellationRequested.Should().BeFalse();

        [Test]
        public void It_completes_the_other_waiter() => _survivor.ContainsKeyId("key-1").Should().BeTrue();

        [Test]
        public void It_counts_no_failure() => _status.ConsecutiveFailures.Should().Be(0);

        [Test]
        public void It_records_the_load_as_a_success() =>
            _status.LastOutcome.Should().BeOfType<SigningKeyRefreshOutcome.Succeeded>();
    }

    // (c)
    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_a_load_that_outlives_the_load_timeout(bool storeHonorsCancellation)
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeysUnavailableException _exception = null!;
        private SigningKeyProviderStatus _status = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            if (storeHonorsCancellation)
            {
                _harness.Gate();
            }
            else
            {
                _harness.Behavior = _ => new TaskCompletionSource<IEnumerable<PublicKeyInfo>>().Task;
            }

            using var provider = Provider(_harness, time);

            Task<SigningKeySnapshot> caller = provider.GetUsableAsync(CancellationToken.None);
            await _harness.WaitForCallsAsync(1);
            time.Advance(TimeSpan.FromSeconds(10));

            Func<Task> wait = () => caller.Bounded();
            _exception = (await wait.Should().ThrowAsync<SigningKeysUnavailableException>()).Which;
            _status = provider.Status;
        }

        [Test]
        public void It_cancels_the_store_operation() =>
            _harness.Calls.Single().Token.IsCancellationRequested.Should().BeTrue();

        [Test]
        public void It_reports_no_snapshot() =>
            _exception.Reason.Should().Be(SigningKeysUnavailableReason.NoSnapshot);

        [Test]
        public void It_is_a_retrieval_failure() =>
            _exception
                .Outcome.Should()
                .BeOfType<SigningKeyRefreshOutcome.Failed>()
                .Which.Kind.Should()
                .Be(SigningKeyFailureKind.Retrieval);

        // The deadline participates in the failure and backoff policy like any other failure.
        [Test]
        public void It_counts_the_failure() => _status.ConsecutiveFailures.Should().Be(1);

        [Test]
        public void It_closes_the_gate_for_the_first_backoff() =>
            _status.NextAttemptAt.Should().Be(Start + TimeSpan.FromSeconds(15));
    }

    // (d)
    [TestFixture]
    public class Given_an_overdue_snapshot
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeySnapshot _warm = null!;
        private SigningKeySnapshot _served = null!;
        private SigningKeySnapshot? _afterwards;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);
            _warm = await provider.GetUsableAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(301));
            _harness.ReturnsKeys("key-1", "key-2");
            Task reloaded = provider.AttemptStateChanged;
            _served = await provider.GetUsableAsync(CancellationToken.None);
            await reloaded.WaitAsync(TimeSpan.FromSeconds(10));
            _afterwards = provider.Current;

            // The reload published a fresh snapshot, so this call needs no load.
            await provider.GetUsableAsync(CancellationToken.None);
        }

        [Test]
        public void It_serves_the_overdue_snapshot() => _served.Should().BeSameAs(_warm);

        [Test]
        public void It_requests_exactly_one_load() => _harness.Calls.Should().HaveCount(2);

        [Test]
        public void It_publishes_the_reloaded_keys() => _afterwards!.ContainsKeyId("key-2").Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_overdue_snapshot_while_the_gate_is_closed
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeySnapshot _warm = null!;
        private SigningKeySnapshot _served = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);
            _warm = await provider.GetUsableAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(301));
            _harness.Fails(new TimeoutException());
            Task failed = provider.AttemptStateChanged;
            await provider.GetUsableAsync(CancellationToken.None); // overdue: the reload fails, gate closes for 5 s
            await failed.WaitAsync(TimeSpan.FromSeconds(10));

            time.Advance(TimeSpan.FromSeconds(4));
            _served = await provider.GetUsableAsync(CancellationToken.None);
        }

        [Test]
        public void It_serves_the_overdue_snapshot() => _served.Should().BeSameAs(_warm);

        [Test]
        public void It_makes_no_store_call_during_the_retry_delay() => _harness.Calls.Should().HaveCount(2);
    }

    // (e)
    [TestFixture]
    public class Given_an_expired_snapshot_and_a_store_that_answers
    {
        private bool _completedBeforeTheStoreAnswered;
        private SigningKeySnapshot _result = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            await provider.GetUsableAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(3601));
            var gate = harness.Gate();
            Task<SigningKeySnapshot> caller = provider.GetUsableAsync(CancellationToken.None);
            await Task.Delay(50);
            _completedBeforeTheStoreAnswered = caller.IsCompleted;

            gate.SetResult([KeyRepositoryHarness.Row("key-2")]);
            _result = await caller.Bounded();
        }

        [Test]
        public void It_awaits_the_load() => _completedBeforeTheStoreAnswered.Should().BeFalse();

        [Test]
        public void It_returns_the_new_snapshot() =>
            _result.Keys.Select(key => key.KeyId).Should().Equal("key-2");
    }

    [TestFixture]
    public class Given_an_expired_snapshot_and_a_failing_store
    {
        private SigningKeysUnavailableException _exception = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            await provider.GetUsableAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(3601));
            harness.Fails(new TimeoutException());

            Func<Task> call = () => provider.GetUsableAsync(CancellationToken.None);
            _exception = (await call.Should().ThrowAsync<SigningKeysUnavailableException>()).Which;
        }

        [Test]
        public void It_reports_the_expired_snapshot() =>
            _exception.Reason.Should().Be(SigningKeysUnavailableReason.SnapshotExpired);
    }

    // (f): whatever the driver throws is a retrieval failure.
    [TestFixture(typeof(TimeoutException))]
    [TestFixture(typeof(FakeDbException))]
    [TestFixture(typeof(InvalidOperationException))]
    [TestFixture(typeof(OperationCanceledException))]
    public class Given_a_refresh_whose_store_throws(Type exceptionType)
    {
        private Exception _thrown = null!;
        private SigningKeySnapshot _before = null!;
        private SigningKeyRefreshOutcome _outcome = null!;
        private SigningKeyProviderStatus _status = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            _before = await provider.GetUsableAsync(CancellationToken.None);

            _thrown = (Exception)Activator.CreateInstance(exceptionType, "store failure")!;
            harness.Fails(_thrown);
            _outcome = await provider.RefreshAsync(SigningKeyRefreshTrigger.Timer, CancellationToken.None);
            _status = provider.Status;
        }

        [Test]
        public void It_is_a_retrieval_failure() =>
            _outcome
                .Should()
                .BeOfType<SigningKeyRefreshOutcome.Failed>()
                .Which.Kind.Should()
                .Be(SigningKeyFailureKind.Retrieval);

        [Test]
        public void It_keeps_the_cause() =>
            ((SigningKeyRefreshOutcome.Failed)_outcome).Exception.Should().BeSameAs(_thrown);

        [Test]
        public void It_keeps_the_previous_snapshot() => _status.Current.Should().BeSameAs(_before);

        [Test]
        public void It_counts_the_failure() => _status.ConsecutiveFailures.Should().Be(1);
    }

    // (g)
    [TestFixture]
    public class Given_a_store_with_no_active_keys
    {
        private SigningKeyRefreshOutcome _outcome = null!;
        private ILogger<SigningKeySnapshotProvider> _logger = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.Returns();
            _logger = A.Fake<ILogger<SigningKeySnapshotProvider>>();
            using var provider = Provider(harness, time, logger: _logger);
            _outcome = await provider.RefreshAsync(SigningKeyRefreshTrigger.Startup, CancellationToken.None);
        }

        [Test]
        public void It_succeeds_with_zero_keys() =>
            _outcome.Should().BeOfType<SigningKeyRefreshOutcome.Succeeded>().Which.KeyCount.Should().Be(0);

        // An empty key set served on purpose is distinguishable from a swallowed failure (AC 4).
        [Test]
        public void It_warns_that_the_snapshot_is_empty() =>
            MessagesAt(_logger, LogLevel.Warning)
                .Should()
                .Equal(
                    "Signing-key snapshot 1 is empty: the Database store holds no active signing key, so every token will be rejected"
                );
    }

    [TestFixture]
    public class Given_key_records_none_of_which_can_be_used
    {
        private SigningKeySnapshot _before = null!;
        private SigningKeyRefreshOutcome _outcome = null!;
        private SigningKeyProviderStatus _status = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            _before = await provider.GetUsableAsync(CancellationToken.None);

            harness.Returns(KeyRepositoryHarness.Garbage("bad-1"), KeyRepositoryHarness.Garbage("bad-2"));
            _outcome = await provider.RefreshAsync(SigningKeyRefreshTrigger.Timer, CancellationToken.None);
            _status = provider.Status;
        }

        [Test]
        public void It_is_a_processing_failure() =>
            _outcome
                .Should()
                .BeOfType<SigningKeyRefreshOutcome.Failed>()
                .Which.Kind.Should()
                .Be(SigningKeyFailureKind.Processing);

        [Test]
        public void It_keeps_the_previous_snapshot() => _status.Current.Should().BeSameAs(_before);

        [Test]
        public void It_counts_the_failure() => _status.ConsecutiveFailures.Should().Be(1);
    }

    [TestFixture]
    public class Given_some_key_records_that_cannot_be_used
    {
        private SigningKeyRefreshOutcome.Succeeded _outcome = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.Returns(KeyRepositoryHarness.Row("key-1"), KeyRepositoryHarness.Garbage("bad-1"));
            using var provider = Provider(harness, time);
            _outcome = (SigningKeyRefreshOutcome.Succeeded)
                await provider.RefreshAsync(SigningKeyRefreshTrigger.Startup, CancellationToken.None);
        }

        [Test]
        public void It_publishes_the_usable_keys() =>
            _outcome.Snapshot.Keys.Select(key => key.KeyId).Should().Equal("key-1");

        [Test]
        public void It_records_the_discarded_record() => _outcome.DiscardedEntryCount.Should().Be(1);
    }

    // (h)
    [TestFixture]
    public class Given_an_unknown_key_id_when_the_gate_is_open_and_the_cooldown_elapsed
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome _outcome;
        private SigningKeySnapshot? _current;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(31));
            _harness.ReturnsKeys("key-1", "key-2");
            _outcome = await provider.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);
            _current = provider.Current;
        }

        [Test]
        public void It_finds_the_new_key() =>
            _outcome.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedFound);

        [Test]
        public void It_loads_once() => _harness.Calls.Should().HaveCount(2);

        [Test]
        public void It_publishes_the_new_key() => _current!.ContainsKeyId("key-2").Should().BeTrue();
    }

    // (i)
    [TestFixture]
    public class Given_a_key_retired_before_a_refresh
    {
        private SigningKeySnapshot? _current;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1", "key-2");
            using var provider = Provider(harness, time);
            await provider.GetUsableAsync(CancellationToken.None);

            harness.ReturnsKeys("key-1");
            await provider.RefreshAsync(SigningKeyRefreshTrigger.Timer, CancellationToken.None);
            _current = provider.Current;
        }

        [Test]
        public void It_removes_the_retired_key() => _current!.ContainsKeyId("key-2").Should().BeFalse();

        [Test]
        public void It_keeps_the_remaining_key() => _current!.ContainsKeyId("key-1").Should().BeTrue();
    }

    // (j)
    [TestFixture]
    public class Given_successive_unknown_key_requests_within_one_cooldown
    {
        private KeyRepositoryHarness _harness = null!;
        private List<SigningKeyUnknownKeyOutcome> _firstWindow = null!;
        private int _callsAfterFirstWindow;
        private SigningKeyUnknownKeyOutcome _justBeforeTheCooldownEnds;
        private SigningKeyUnknownKeyOutcome _atTheCooldownEnd;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(31));

            _firstWindow = [];
            for (int request = 0; request < 100; request++)
            {
                _firstWindow.Add(
                    await provider.TryRefreshForUnknownKeyAsync("unknown", CancellationToken.None)
                );
            }

            _callsAfterFirstWindow = _harness.Calls.Count;

            time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
            _justBeforeTheCooldownEnds = await provider.TryRefreshForUnknownKeyAsync(
                "unknown",
                CancellationToken.None
            );
            time.Advance(TimeSpan.FromTicks(1));
            _atTheCooldownEnd = await provider.TryRefreshForUnknownKeyAsync(
                "unknown",
                CancellationToken.None
            );
        }

        [Test]
        public void It_loads_for_the_first_request() =>
            _firstWindow[0].Should().Be(SigningKeyUnknownKeyOutcome.RefreshedAbsent);

        [Test]
        public void It_suppresses_the_other_99() =>
            _firstWindow
                .Skip(1)
                .Should()
                .OnlyContain(outcome => outcome == SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_reads_the_store_once_in_the_window() => _callsAfterFirstWindow.Should().Be(2);

        [Test]
        public void It_still_suppresses_just_before_the_cooldown_ends() =>
            _justBeforeTheCooldownEnds.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_loads_again_once_the_cooldown_has_passed() =>
            _atTheCooldownEnd.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedAbsent);

        [Test]
        public void It_reads_the_store_one_more_time() => _harness.Calls.Should().HaveCount(3);
    }

    [TestFixture]
    public class Given_an_unknown_key_id_while_the_gate_is_closed
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome _outcome;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.Fails(new TimeoutException());
            using var provider = Provider(
                _harness,
                time,
                new IdentityOptions { SigningKeyUnknownKeyRefreshCooldownSeconds = 1 }
            );
            await provider.RefreshAsync(SigningKeyRefreshTrigger.Startup, CancellationToken.None); // gate closed 5 s

            time.Advance(TimeSpan.FromSeconds(2)); // cooldown over, gate still closed
            _outcome = await provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None);
        }

        [Test]
        public void It_is_refused_by_the_gate() =>
            _outcome.Should().Be(SigningKeyUnknownKeyOutcome.RefusedGate);

        [Test]
        public void It_makes_no_store_call() => _harness.Calls.Should().HaveCount(1);
    }

    // (k)
    [TestFixture]
    public class Given_invalid_settings
    {
        [Test]
        public void It_refuses_to_construct()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);

            Action create = () =>
                Provider(harness, time, new IdentityOptions { SigningKeyLoadTimeoutSeconds = 0 }).Dispose();

            create.Should().Throw<OptionsValidationException>();
        }
    }

    // (l)
    [TestFixture]
    public class Given_waves_of_cold_requests_while_the_store_keeps_failing
    {
        private KeyRepositoryHarness _harness = null!;
        private int _requests;
        private int _unavailable;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.Fails(new TimeoutException());
            using var provider = Provider(_harness, time);

            for (int wave = 0; wave <= 900; wave++)
            {
                Task[] requests =
                [
                    .. Enumerable
                        .Range(0, 50)
                        .Select(async _ =>
                        {
                            Interlocked.Increment(ref _requests);
                            try
                            {
                                await provider.GetUsableAsync(CancellationToken.None);
                            }
                            catch (SigningKeysUnavailableException)
                            {
                                Interlocked.Increment(ref _unavailable);
                            }
                        }),
                ];
                await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));

                // A gate that stops refusing turns every request into a store call; stop at the first extra call so
                // that regression fails in seconds instead of issuing tens of thousands of loads.
                if (_harness.Calls.Count > 5)
                {
                    break;
                }

                time.Advance(TimeSpan.FromMilliseconds(100));
            }
        }

        // Attempts at 0, 5, 15, 35 and 75 s: four backoff intervals (5, 10, 20, 40 s) elapse within 90 s, so
        // loads == intervals elapsed + 1, and no request between attempts reaches the store.
        [Test]
        public void It_reads_the_store_only_at_each_retry_deadline() =>
            _harness
                .Calls.Select(call => call.At - Start)
                .Should()
                .Equal(
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(35),
                    TimeSpan.FromSeconds(75)
                );

        [Test]
        public void It_answers_every_request_as_unavailable() =>
            _unavailable.Should().Be(_requests).And.Be(45_050);
    }

    // (m)
    [TestFixture]
    public class Given_a_store_that_recovers_after_a_failure
    {
        private bool _refusedBeforeTheDeadline;
        private SigningKeySnapshot _recovered = null!;
        private SigningKeyProviderStatus _status = null!;
        private KeyRepositoryHarness _harness = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.Fails(new TimeoutException());
            using var provider = Provider(_harness, time);
            await provider.RefreshAsync(SigningKeyRefreshTrigger.Startup, CancellationToken.None);

            _harness.ReturnsKeys("key-1");
            time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
            try
            {
                await provider.GetUsableAsync(CancellationToken.None);
            }
            catch (SigningKeysUnavailableException exception)
                when (exception.Outcome is SigningKeyRefreshOutcome.Refused)
            {
                _refusedBeforeTheDeadline = true;
            }

            time.Advance(TimeSpan.FromTicks(1));
            _recovered = await provider.GetUsableAsync(CancellationToken.None);
            _status = provider.Status;
        }

        [Test]
        public void It_refuses_until_the_retry_deadline() => _refusedBeforeTheDeadline.Should().BeTrue();

        [Test]
        public void It_succeeds_at_the_next_eligible_attempt() =>
            _recovered.ContainsKeyId("key-1").Should().BeTrue();

        [Test]
        public void It_resets_the_failure_count() => _status.ConsecutiveFailures.Should().Be(0);

        [Test]
        public void It_opens_the_gate_immediately() =>
            _status.NextAttemptAt.Should().Be(Start + TimeSpan.FromSeconds(5));

        [Test]
        public void It_read_the_store_twice() => _harness.Calls.Should().HaveCount(2);
    }

    // (o)
    [TestFixture]
    public class Given_a_failing_store_around_the_max_staleness
    {
        private SigningKeySnapshot _warm = null!;
        private SigningKeySnapshot _servedAtTheBound = null!;
        private SigningKeysUnavailableException _pastTheBound = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            _warm = await provider.GetUsableAsync(CancellationToken.None);

            harness.Fails(new TimeoutException());
            time.Advance(TimeSpan.FromSeconds(3600));
            Task failed = provider.AttemptStateChanged;
            _servedAtTheBound = await provider.GetUsableAsync(CancellationToken.None);
            await failed.WaitAsync(TimeSpan.FromSeconds(10)); // the background reload at the bound fails

            time.Advance(TimeSpan.FromTicks(1));
            Func<Task> call = () => provider.GetUsableAsync(CancellationToken.None);
            _pastTheBound = (await call.Should().ThrowAsync<SigningKeysUnavailableException>()).Which;
        }

        [Test]
        public void It_serves_the_snapshot_at_exactly_the_bound() =>
            _servedAtTheBound.Should().BeSameAs(_warm);

        [Test]
        public void It_fails_closed_past_the_bound() =>
            _pastTheBound.Reason.Should().Be(SigningKeysUnavailableReason.SnapshotExpired);
    }

    // (p)
    [TestFixture]
    public class Given_a_rotated_key_seen_inside_the_cooldown
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome _insideCooldown;
        private bool _presentInsideCooldown;
        private SigningKeyUnknownKeyOutcome _afterCooldown;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None);

            _harness.ReturnsKeys("key-1", "key-2");
            time.Advance(TimeSpan.FromSeconds(10));
            _insideCooldown = await provider.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);
            _presentInsideCooldown = provider.Current!.ContainsKeyId("key-2");

            time.Advance(TimeSpan.FromSeconds(20));
            _afterCooldown = await provider.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);
        }

        [Test]
        public void It_suppresses_the_refresh() =>
            _insideCooldown.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_leaves_the_key_absent() => _presentInsideCooldown.Should().BeFalse();

        [Test]
        public void It_accepts_the_key_after_the_cooldown() =>
            _afterCooldown.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedFound);

        [Test]
        public void It_reads_the_store_only_after_the_cooldown() => _harness.Calls.Should().HaveCount(2);
    }

    // Bootstrap allowance, 1: a fresh store. The startup load publishes no key, the first key is inserted afterwards,
    // and the first request that carries it, inside the cooldown, spends the one allowance and finds the key. The
    // cooldown then applies as usual.
    [TestFixture]
    public class Given_the_first_key_inserted_after_an_empty_startup_load
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome _firstRequest;
        private bool _presentAfterFirstRequest;
        private SigningKeyUnknownKeyOutcome _nextUnknownKey;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys();
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None); // first successful load at 0 s: no key

            _harness.ReturnsKeys("key-1");
            time.Advance(TimeSpan.FromSeconds(1));
            _firstRequest = await provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None);
            _presentAfterFirstRequest = provider.Current!.ContainsKeyId("key-1");

            _harness.ReturnsKeys("key-1", "key-2");
            time.Advance(TimeSpan.FromSeconds(1));
            _nextUnknownKey = await provider.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);
        }

        [Test]
        public void It_accepts_the_inserted_key_inside_the_cooldown() =>
            _firstRequest.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedFound);

        [Test]
        public void It_publishes_the_inserted_key() => _presentAfterFirstRequest.Should().BeTrue();

        [Test]
        public void It_applies_the_cooldown_to_the_next_unknown_key() =>
            _nextUnknownKey.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_reads_the_store_once_for_the_allowance() => _harness.Calls.Should().HaveCount(2);
    }

    // Bootstrap allowance, 2. While the store stays empty, a stream of unknown key ids gets only one early refresh, and
    // the rest wait for the cooldown. The empty snapshot published after the cooldown does not restore the allowance.
    [TestFixture]
    public class Given_successive_unknown_key_ids_while_the_store_stays_empty
    {
        private KeyRepositoryHarness _harness = null!;
        private readonly List<SigningKeyUnknownKeyOutcome> _insideFirstCooldown = [];
        private int _callsInsideFirstCooldown;
        private SigningKeyUnknownKeyOutcome _afterCooldown;
        private SigningKeyUnknownKeyOutcome _insideNextCooldown;

        [SetUp]
        public async Task Act()
        {
            _insideFirstCooldown.Clear();
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys();
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None); // first successful load at 0 s: no key

            for (int i = 1; i <= 20; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1)); // 1 s .. 20 s: the cooldown from the 1 s refresh still runs
                _insideFirstCooldown.Add(
                    await provider.TryRefreshForUnknownKeyAsync($"unknown-{i}", CancellationToken.None)
                );
            }
            _callsInsideFirstCooldown = _harness.Calls.Count;

            time.Advance(TimeSpan.FromSeconds(11)); // 31 s: 30 s after the 1 s refresh completed
            _afterCooldown = await provider.TryRefreshForUnknownKeyAsync(
                "unknown-21",
                CancellationToken.None
            );

            time.Advance(TimeSpan.FromSeconds(1));
            _insideNextCooldown = await provider.TryRefreshForUnknownKeyAsync(
                "unknown-22",
                CancellationToken.None
            );
        }

        [Test]
        public void It_refreshes_early_once() =>
            _insideFirstCooldown[0].Should().Be(SigningKeyUnknownKeyOutcome.RefreshedAbsent);

        [Test]
        public void It_suppresses_the_others_inside_the_cooldown() =>
            _insideFirstCooldown
                .Skip(1)
                .Should()
                .AllBeEquivalentTo(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_reads_the_store_once_inside_the_cooldown() => _callsInsideFirstCooldown.Should().Be(2);

        [Test]
        public void It_refreshes_again_after_the_cooldown() =>
            _afterCooldown.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedAbsent);

        [Test]
        public void It_does_not_restore_the_allowance_on_a_later_empty_publication() =>
            _insideNextCooldown.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_reads_the_store_once_per_permitted_refresh() => _harness.Calls.Should().HaveCount(3);
    }

    // Bootstrap allowance, 3: an unknown key id spends the allowance before the first key is inserted. A token signed
    // with the inserted key is then suppressed inside the cooldown, and accepted once it expires.
    [TestFixture]
    public class Given_the_bootstrap_allowance_spent_before_the_first_key_is_inserted
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome _spendingRequest;
        private SigningKeyUnknownKeyOutcome _insideCooldown;
        private bool _presentInsideCooldown;
        private SigningKeyUnknownKeyOutcome _afterCooldown;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys();
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None); // first successful load at 0 s: no key

            time.Advance(TimeSpan.FromSeconds(1));
            _spendingRequest = await provider.TryRefreshForUnknownKeyAsync("forged", CancellationToken.None);

            _harness.ReturnsKeys("key-1");
            time.Advance(TimeSpan.FromSeconds(2));
            _insideCooldown = await provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None);
            _presentInsideCooldown = provider.Current!.ContainsKeyId("key-1");

            time.Advance(TimeSpan.FromSeconds(28)); // 31 s: 30 s after the 1 s refresh completed
            _afterCooldown = await provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None);
        }

        [Test]
        public void It_spends_the_allowance_on_the_first_unknown_key() =>
            _spendingRequest.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedAbsent);

        [Test]
        public void It_suppresses_the_inserted_key_inside_the_cooldown() =>
            _insideCooldown.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_leaves_the_key_absent_inside_the_cooldown() =>
            _presentInsideCooldown.Should().BeFalse();

        [Test]
        public void It_accepts_the_key_after_the_cooldown() =>
            _afterCooldown.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedFound);

        [Test]
        public void It_reads_the_store_only_for_permitted_refreshes() => _harness.Calls.Should().HaveCount(3);
    }

    // Bootstrap allowance, 4a. Concurrent unknown-key requests while the allowance is unspent share one refresh, so
    // they cannot spend more than the one allowance.
    [TestFixture]
    public class Given_concurrent_unknown_key_requests_after_an_empty_startup_load
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome[] _concurrent = null!;
        private SigningKeyUnknownKeyOutcome _nextUnknownKey;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys();
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None); // first successful load at 0 s: no key

            time.Advance(TimeSpan.FromSeconds(1));
            var gate = _harness.Gate();
            Task<SigningKeyUnknownKeyOutcome>[] callers =
            [
                .. Enumerable
                    .Range(0, 8)
                    .Select(_ =>
                        Task.Run(() => provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None))
                    ),
            ];
            await _harness.WaitForCallsAsync(2);
            gate.SetResult([KeyRepositoryHarness.Row("key-1")]);
            _concurrent = await Task.WhenAll(callers);

            _harness.ReturnsKeys("key-1", "key-2");
            time.Advance(TimeSpan.FromSeconds(1));
            _nextUnknownKey = await provider.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);
        }

        [Test]
        public void It_gives_every_caller_the_shared_refresh() =>
            _concurrent.Should().AllBeEquivalentTo(SigningKeyUnknownKeyOutcome.RefreshedFound);

        [Test]
        public void It_reads_the_store_once_for_all_of_them() => _harness.Calls.Should().HaveCount(2);

        [Test]
        public void It_applies_the_cooldown_afterwards() =>
            _nextUnknownKey.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);
    }

    // Bootstrap allowance, 4b: a bootstrap refresh that fails spends the allowance and keeps the failure backoff.
    [TestFixture]
    public class Given_a_failing_bootstrap_refresh
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome _bootstrapRefresh;
        private int _consecutiveFailures;
        private TimeSpan _retryDelay;
        private SigningKeyUnknownKeyOutcome _nextUnknownKey;
        private bool _snapshotStillUsable;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys();
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None); // first successful load at 0 s: no key

            _harness.Fails(new TimeoutException());
            time.Advance(TimeSpan.FromSeconds(1));
            _bootstrapRefresh = await provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None);
            _consecutiveFailures = provider.Status.ConsecutiveFailures;
            _retryDelay = provider.NextAttemptAt - time.GetUtcNow();

            _harness.ReturnsKeys("key-1");
            time.Advance(TimeSpan.FromSeconds(1));
            _nextUnknownKey = await provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None);
            _snapshotStillUsable = provider.Current is { Keys.Count: 0 };
        }

        [Test]
        public void It_reports_the_failed_refresh() =>
            _bootstrapRefresh.Should().Be(SigningKeyUnknownKeyOutcome.RefreshFailed);

        [Test]
        public void It_counts_the_failure() => _consecutiveFailures.Should().Be(1);

        [Test]
        public void It_keeps_the_failure_backoff() => _retryDelay.Should().Be(TimeSpan.FromSeconds(5));

        [Test]
        public void It_spends_the_allowance() =>
            _nextUnknownKey.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_keeps_the_empty_snapshot() => _snapshotStillUsable.Should().BeTrue();

        [Test]
        public void It_reads_the_store_once_for_the_allowance() => _harness.Calls.Should().HaveCount(2);
    }

    // Bootstrap allowance, not granted: the first successful load found a key. A later empty publication grants
    // nothing, so an unknown key inside the cooldown is suppressed as on any populated store.
    [TestFixture]
    public class Given_a_store_emptied_after_a_first_load_with_a_key
    {
        private KeyRepositoryHarness _harness = null!;
        private bool _emptyAfterReload;
        private SigningKeyUnknownKeyOutcome _unknownKey;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None); // first successful load at 0 s: one key

            _harness.ReturnsKeys();
            time.Advance(TimeSpan.FromSeconds(31));
            await provider.RefreshAsync(SigningKeyRefreshTrigger.Timer, CancellationToken.None);
            _emptyAfterReload = provider.Current is { Keys.Count: 0 };

            _harness.ReturnsKeys("key-2");
            time.Advance(TimeSpan.FromSeconds(1));
            _unknownKey = await provider.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);
        }

        [Test]
        public void It_publishes_the_empty_reload() => _emptyAfterReload.Should().BeTrue();

        [Test]
        public void It_suppresses_the_unknown_key_inside_the_cooldown() =>
            _unknownKey.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_reads_the_store_only_for_the_loads() => _harness.Calls.Should().HaveCount(2);
    }

    // (q)
    [TestFixture]
    public class Given_two_providers_over_one_store
    {
        private SigningKeyUnknownKeyOutcome _eligible;
        private SigningKeyUnknownKeyOutcome _suppressed;
        private SigningKeyUnknownKeyOutcome _suppressedLater;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var providerA = Provider(harness, time);
            using var providerB = Provider(harness, time);

            await providerA.GetUsableAsync(CancellationToken.None); // A loads at 0 s
            time.Advance(TimeSpan.FromSeconds(20));
            await providerB.GetUsableAsync(CancellationToken.None); // B loads at 20 s

            time.Advance(TimeSpan.FromSeconds(11)); // 31 s: A's cooldown is over, B's is not
            harness.ReturnsKeys("key-1", "key-2");
            _eligible = await providerA.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);
            _suppressed = await providerB.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(19)); // 50 s: B's cooldown is over
            _suppressedLater = await providerB.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None);
        }

        [Test]
        public void It_lets_the_eligible_provider_accept_the_key() =>
            _eligible.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedFound);

        [Test]
        public void It_keeps_the_other_provider_suppressed() =>
            _suppressed.Should().Be(SigningKeyUnknownKeyOutcome.SuppressedCooldown);

        [Test]
        public void It_lets_the_other_provider_accept_after_its_own_cooldown() =>
            _suppressedLater.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedFound);
    }

    [TestFixture]
    public class Given_a_known_key_id
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyUnknownKeyOutcome _outcome;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);
            await provider.GetUsableAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(31));
            _outcome = await provider.TryRefreshForUnknownKeyAsync("key-1", CancellationToken.None);
        }

        [Test]
        public void It_reports_it_present() =>
            _outcome.Should().Be(SigningKeyUnknownKeyOutcome.AlreadyPresent);

        [Test]
        public void It_does_not_load() => _harness.Calls.Should().HaveCount(1);
    }

    [TestFixture]
    public class Given_consecutive_failures
    {
        private List<TimeSpan> _delays = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.Fails(new TimeoutException());
            using var provider = Provider(harness, time);

            _delays = [];
            for (int failure = 0; failure < 7; failure++)
            {
                await provider.RefreshAsync(SigningKeyRefreshTrigger.Timer, CancellationToken.None);
                TimeSpan delay = provider.NextAttemptAt - time.GetUtcNow();
                _delays.Add(delay);
                time.Advance(delay);
            }
        }

        [Test]
        public void It_doubles_the_delay_from_five_seconds_up_to_sixty() =>
            _delays.Select(delay => delay.TotalSeconds).Should().Equal(5, 10, 20, 40, 60, 60, 60);
    }

    [TestFixture]
    public class Given_the_jitter_extremes
    {
        private TimeSpan _lowest;
        private TimeSpan _highest;

        [SetUp]
        public async Task Act()
        {
            _lowest = await FirstDelay(0.0);
            _highest = await FirstDelay(0.999_999);
        }

        private static async Task<TimeSpan> FirstDelay(double sample)
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.Fails(new TimeoutException());
            using var provider = new SigningKeySnapshotProvider(
                new DatabaseSigningKeySource(
                    harness.Repository,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseSigningKeySource>.Instance
                ),
                Options.Create(new IdentityOptions()),
                time,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SigningKeySnapshotProvider>.Instance,
                new FixedRandom(sample)
            );
            await provider.RefreshAsync(SigningKeyRefreshTrigger.Timer, CancellationToken.None);
            return provider.NextAttemptAt - time.GetUtcNow();
        }

        [Test]
        public void It_shortens_by_at_most_twenty_percent() => _lowest.Should().Be(TimeSpan.FromSeconds(4));

        [Test]
        public void It_lengthens_by_less_than_twenty_percent() =>
            _highest
                .Should()
                .BeGreaterThan(TimeSpan.FromSeconds(5.99))
                .And.BeLessThan(TimeSpan.FromSeconds(6));
    }

    [TestFixture]
    public class Given_a_failed_load_with_logging
    {
        private ILogger<SigningKeySnapshotProvider> _logger = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.Fails(new TimeoutException("Timeout during reading attempt"));
            _logger = A.Fake<ILogger<SigningKeySnapshotProvider>>();
            using var provider = Provider(harness, time, logger: _logger);
            await provider.RefreshAsync(SigningKeyRefreshTrigger.Startup, CancellationToken.None);
        }

        [Test]
        public void It_logs_one_error_with_category_trigger_count_delay_and_cause() =>
            MessagesAt(_logger, LogLevel.Error)
                .Should()
                .Equal(
                    "Signing-key load failed (Startup): category SigningKeyStore, kind Retrieval, consecutive failures 1, next attempt in 5.0 s. TimeoutException: Timeout during reading attempt"
                );
    }

    [TestFixture]
    public class Given_a_load_in_flight
    {
        private bool _inFlightWhileGated;
        private bool _signalCompletedWhileGated;
        private bool _signalCompletedAfterwards;
        private bool _nextSignalCompleted;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            var gate = harness.Gate();
            using var provider = Provider(harness, time);

            Task signal = provider.AttemptStateChanged;
            Task<SigningKeyRefreshOutcome> refresh = provider.RefreshAsync(
                SigningKeyRefreshTrigger.Startup,
                CancellationToken.None
            );
            _inFlightWhileGated = provider.Status.LoadInFlight;
            _signalCompletedWhileGated = signal.IsCompleted;

            gate.SetResult([KeyRepositoryHarness.Row("key-1")]);
            await refresh.Bounded();
            await signal.WaitAsync(TimeSpan.FromSeconds(10));
            _signalCompletedAfterwards = signal.IsCompleted;
            _nextSignalCompleted = provider.AttemptStateChanged.IsCompleted;
        }

        [Test]
        public void It_reports_the_load_in_flight() => _inFlightWhileGated.Should().BeTrue();

        [Test]
        public void It_does_not_signal_before_the_attempt_completes() =>
            _signalCompletedWhileGated.Should().BeFalse();

        [Test]
        public void It_signals_when_the_attempt_completes() => _signalCompletedAfterwards.Should().BeTrue();

        [Test]
        public void It_arms_a_new_signal_for_the_next_transition() => _nextSignalCompleted.Should().BeFalse();
    }

    [TestFixture]
    public class Given_the_provider_is_disposed_during_a_load
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.Gate();
            var provider = Provider(_harness, time);

            Task<SigningKeyRefreshOutcome> refresh = provider.RefreshAsync(
                SigningKeyRefreshTrigger.Timer,
                CancellationToken.None
            );
            await _harness.WaitForCallsAsync(1);
            provider.Dispose();
            _outcome = await refresh.Bounded();
        }

        [Test]
        public void It_cancels_the_store_operation() =>
            _harness.Calls.Single().Token.IsCancellationRequested.Should().BeTrue();

        [Test]
        public void It_ends_the_attempt_as_a_retrieval_failure() =>
            _outcome
                .Should()
                .BeOfType<SigningKeyRefreshOutcome.Failed>()
                .Which.Kind.Should()
                .Be(SigningKeyFailureKind.Retrieval);
    }

    // Review finding 1: a store operation that ignores cancellation outlives its deadline and the whole backoff. The
    // single-flight slot keeps owning it: the timeout is recorded once, no overlapping store call starts, and its late
    // result is discarded. Recovery follows once it finishes.
    [TestFixture]
    public class Given_a_store_operation_that_outlives_its_deadline_and_the_backoff
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeysUnavailableException _timeout = null!;
        private SigningKeyProviderStatus _afterTimeout = null!;
        private SigningKeysUnavailableException _requestWhileOutstanding = null!;
        private SigningKeyRefreshOutcome _refreshWhileOutstanding = null!;
        private int _callsWhileOutstanding;
        private SigningKeyProviderStatus _whileOutstanding = null!;
        private SigningKeySnapshot? _afterLateResult;
        private SigningKeyProviderStatus _afterRelease = null!;
        private SigningKeySnapshot _recovered = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            TaskCompletionSource<IEnumerable<PublicKeyInfo>> blocked = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            int calls = 0;
            _harness.Behavior = _ =>
                Interlocked.Increment(ref calls) == 1
                    ? blocked.Task // ignores its token
                    : Task.FromResult<IEnumerable<PublicKeyInfo>>([KeyRepositoryHarness.Row("key-new")]);
            using var provider = Provider(_harness, time);

            // 0 s: cold request; 10 s: the deadline ends the attempt, the operation keeps running.
            Task<SigningKeySnapshot> cold = provider.GetUsableAsync(CancellationToken.None);
            await _harness.WaitForCallsAsync(1);
            time.Advance(TimeSpan.FromSeconds(10));
            Func<Task> coldWait = () => cold.Bounded();
            _timeout = (await coldWait.Should().ThrowAsync<SigningKeysUnavailableException>()).Which;
            _afterTimeout = provider.Status;

            // 16 s: past the 5 s backoff, with the first operation still running.
            time.Advance(TimeSpan.FromSeconds(6));
            Func<Task> request = () => provider.GetUsableAsync(CancellationToken.None);
            _requestWhileOutstanding = (
                await request.Should().ThrowAsync<SigningKeysUnavailableException>()
            ).Which;
            _refreshWhileOutstanding = await provider.RefreshAsync(
                SigningKeyRefreshTrigger.Timer,
                CancellationToken.None
            );
            _callsWhileOutstanding = _harness.Calls.Count;
            _whileOutstanding = provider.Status;

            // The first operation finally answers; its result is late and discarded.
            Task released = provider.AttemptStateChanged;
            blocked.SetResult([KeyRepositoryHarness.Row("key-late")]);
            await released.WaitAsync(TimeSpan.FromSeconds(10));
            _afterLateResult = provider.Current;
            _afterRelease = provider.Status;

            _recovered = await provider.GetUsableAsync(CancellationToken.None).Bounded();
        }

        [Test]
        public void It_fails_the_caller_at_the_deadline() =>
            _timeout
                .Outcome.Should()
                .BeOfType<SigningKeyRefreshOutcome.Failed>()
                .Which.Exception.Should()
                .BeOfType<TimeoutException>();

        [Test]
        public void It_keeps_owning_the_running_operation() =>
            _afterTimeout.StoreOperationOutstanding.Should().BeTrue();

        [Test]
        public void It_refuses_requests_while_the_operation_runs() =>
            _requestWhileOutstanding
                .Outcome.Should()
                .BeOfType<SigningKeyRefreshOutcome.Refused>()
                .Which.Reason.Should()
                .Be(SigningKeyRefusalReason.OperationOutstanding);

        [Test]
        public void It_refuses_scheduled_refreshes_while_the_operation_runs() =>
            _refreshWhileOutstanding
                .Should()
                .BeOfType<SigningKeyRefreshOutcome.Refused>()
                .Which.Reason.Should()
                .Be(SigningKeyRefusalReason.OperationOutstanding);

        [Test]
        public void It_starts_no_overlapping_store_call() => _callsWhileOutstanding.Should().Be(1);

        [Test]
        public void It_records_the_timeout_once() => _whileOutstanding.ConsecutiveFailures.Should().Be(1);

        [Test]
        public void It_discards_the_late_result() => _afterLateResult.Should().BeNull();

        [Test]
        public void It_releases_the_slot_when_the_operation_finishes() =>
            _afterRelease.StoreOperationOutstanding.Should().BeFalse();

        [Test]
        public void It_changes_the_state_version_when_the_operation_finishes() =>
            _afterRelease.StateVersion.Should().NotBe(_whileOutstanding.StateVersion);

        [Test]
        public void It_recovers_with_a_new_load() =>
            _recovered.Keys.Select(key => key.KeyId).Should().Equal("key-new");

        [Test]
        public void It_made_exactly_two_store_calls() => _harness.Calls.Should().HaveCount(2);
    }

    // Review finding 2: caller A reads "no snapshot", then (inside its first clock read) caller B loads and publishes.
    // A's decision to load is made again under the gate lock, so A uses B's snapshot; a second, redundant load would
    // fail here and wrongly answer A as unavailable.
    [TestFixture]
    public class Given_a_caller_overtaken_by_a_publication
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeySnapshot? _overtaking;
        private SigningKeySnapshot _overtaken = null!;

        [SetUp]
        public async Task Act()
        {
            InterceptingTimeProvider time = new(Start);
            _harness = new KeyRepositoryHarness(time);
            int calls = 0;
            _harness.Behavior = _ =>
                Interlocked.Increment(ref calls) == 1
                    ? Task.FromResult<IEnumerable<PublicKeyInfo>>([KeyRepositoryHarness.Row("key-1")])
                    : Task.FromException<IEnumerable<PublicKeyInfo>>(new TimeoutException("redundant load"));
            using var provider = Provider(_harness, time);

            time.OnNextUtcNow(() =>
                _overtaking = provider
                    .GetUsableAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .GetAwaiter()
                    .GetResult()
            );
            _overtaken = await provider.GetUsableAsync(CancellationToken.None).Bounded();
        }

        [Test]
        public void It_reads_the_store_once() => _harness.Calls.Should().HaveCount(1);

        [Test]
        public void It_gives_the_overtaken_caller_the_published_snapshot() =>
            _overtaken.Should().BeSameAs(_overtaking);

        [Test]
        public void It_published_the_key() => _overtaken.ContainsKeyId("key-1").Should().BeTrue();
    }

    // Review finding 3: a source whose synchronous work runs past the load deadline and then returns a completed result.
    // With a delivered deadline the attempt ends on cancellation; with a late timer only the elapsed-time check on the
    // result can reject it.
    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_a_source_that_crosses_the_deadline_before_answering(bool deadlineTimerFires)
    {
        private SigningKeySnapshot _before = null!;
        private SigningKeyRefreshOutcome _outcome = null!;
        private SigningKeyProviderStatus _status = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = deadlineTimerFires ? NewTime() : new LateTimerTimeProvider(Start);
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            using var provider = Provider(harness, time);
            _before = await provider.GetUsableAsync(CancellationToken.None);

            harness.Behavior = _ =>
            {
                time.Advance(TimeSpan.FromSeconds(11));
                return Task.FromResult<IEnumerable<PublicKeyInfo>>([KeyRepositoryHarness.Row("key-2")]);
            };
            _outcome = await provider
                .RefreshAsync(SigningKeyRefreshTrigger.Timer, CancellationToken.None)
                .Bounded();
            _status = provider.Status;
        }

        [Test]
        public void It_rejects_the_late_result_as_a_timeout() =>
            _outcome
                .Should()
                .BeOfType<SigningKeyRefreshOutcome.Failed>()
                .Which.Exception.Should()
                .BeOfType<TimeoutException>();

        [Test]
        public void It_is_a_retrieval_failure() =>
            ((SigningKeyRefreshOutcome.Failed)_outcome).Kind.Should().Be(SigningKeyFailureKind.Retrieval);

        [Test]
        public void It_keeps_the_previous_snapshot() => _status.Current.Should().BeSameAs(_before);

        [Test]
        public void It_counts_one_failure() => _status.ConsecutiveFailures.Should().Be(1);

        [Test]
        public void It_applies_the_backoff() =>
            _status.NextAttemptAt.Should().Be(Start + TimeSpan.FromSeconds(16));
    }

    // Scheduler review finding 1: a conditional refresh decided from an older status is refused once another caller
    // has changed the state, and calls nothing.
    [TestFixture]
    public class Given_a_conditional_refresh_after_the_state_changed
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);

            long observed = provider.Status.StateVersion;
            await provider.GetUsableAsync(CancellationToken.None).Bounded();
            _outcome = await provider
                .RefreshIfUnchangedAsync(SigningKeyRefreshTrigger.Timer, observed, CancellationToken.None)
                .Bounded();
        }

        [Test]
        public void It_refuses_because_the_state_changed() =>
            _outcome
                .Should()
                .BeOfType<SigningKeyRefreshOutcome.Refused>()
                .Which.Reason.Should()
                .Be(SigningKeyRefusalReason.StateChanged);

        [Test]
        public void It_does_not_call_the_store() => _harness.Calls.Should().HaveCount(1);
    }

    [TestFixture]
    public class Given_a_conditional_refresh_in_the_observed_state
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeyRefreshOutcome _outcome = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1");
            using var provider = Provider(_harness, time);

            await provider.GetUsableAsync(CancellationToken.None).Bounded();
            _outcome = await provider
                .RefreshIfUnchangedAsync(
                    SigningKeyRefreshTrigger.Timer,
                    provider.Status.StateVersion,
                    CancellationToken.None
                )
                .Bounded();
        }

        [Test]
        public void It_loads() => _outcome.Should().BeOfType<SigningKeyRefreshOutcome.Succeeded>();

        [Test]
        public void It_calls_the_store() => _harness.Calls.Should().HaveCount(2);
    }

    [TestFixture]
    public class Given_the_state_version_across_transitions
    {
        private SigningKeyProviderStatus _before = null!;
        private SigningKeyProviderStatus _started = null!;
        private SigningKeyProviderStatus _completed = null!;
        private SigningKeyProviderStatus _disposed = null!;

        [SetUp]
        public async Task Act()
        {
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            var gate = harness.Gate();
            var provider = Provider(harness, time);

            _before = provider.Status;
            Task<SigningKeySnapshot> load = provider.GetUsableAsync(CancellationToken.None);
            await harness.WaitForCallsAsync(1);
            _started = provider.Status;
            gate.SetResult([KeyRepositoryHarness.Row("key-1")]);
            await load.Bounded();
            _completed = provider.Status;
            provider.Dispose();
            _disposed = provider.Status;
        }

        [Test]
        public void It_changes_when_an_attempt_starts() =>
            _started.StateVersion.Should().NotBe(_before.StateVersion);

        [Test]
        public void It_changes_when_an_attempt_completes() =>
            _completed.StateVersion.Should().NotBe(_started.StateVersion);

        [Test]
        public void It_changes_at_disposal() =>
            _disposed.StateVersion.Should().NotBe(_completed.StateVersion);

        [Test]
        public void It_reports_disposal_only_after_it() =>
            new[] { _before, _started, _completed, _disposed }
                .Select(status => status.ProviderDisposed)
                .Should()
                .Equal(false, false, false, true);
    }

    [TestFixture]
    public class Given_a_source_that_blocks_synchronously
    {
        private ManualResetEventSlim _release = null!;
        private bool _returnedPromptly;
        private SigningKeysUnavailableException _exception = null!;

        [SetUp]
        public async Task Act()
        {
            // A new event per test: TearDown disposes it, and NUnit reuses the fixture instance across tests.
            _release = new ManualResetEventSlim();
            FakeTimeProvider time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.Behavior = _ =>
            {
                _release.Wait(TimeSpan.FromSeconds(30));
                return Task.FromResult<IEnumerable<PublicKeyInfo>>([KeyRepositoryHarness.Row("key-1")]);
            };
            using var provider = Provider(harness, time);

            // StartNew keeps the outer task: it completes when GetUsableAsync returns, not when its task completes.
            Task<Task<SigningKeySnapshot>> invocation = Task.Factory.StartNew(
                () => provider.GetUsableAsync(CancellationToken.None),
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default
            );
            _returnedPromptly =
                await Task.WhenAny(invocation, Task.Delay(TimeSpan.FromSeconds(5))) == invocation;

            await harness.WaitForCallsAsync(1);
            time.Advance(TimeSpan.FromSeconds(10));
            Func<Task> wait = () => invocation.Result.Bounded();
            _exception = (await wait.Should().ThrowAsync<SigningKeysUnavailableException>()).Which;
        }

        [TearDown]
        public void TearDown()
        {
            _release.Set();
            _release.Dispose();
        }

        [Test]
        public void It_does_not_run_the_source_on_the_callers_thread() => _returnedPromptly.Should().BeTrue();

        [Test]
        public void It_fails_the_caller_at_the_deadline() =>
            _exception
                .Outcome.Should()
                .BeOfType<SigningKeyRefreshOutcome.Failed>()
                .Which.Exception.Should()
                .BeOfType<TimeoutException>();
    }
}
