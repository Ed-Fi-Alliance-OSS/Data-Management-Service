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
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys.SigningKeyTestSupport;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

/// <summary>
/// Spec §5 step 1.6 (a)–(i) and the scheduler review requirements: the refresh service over the real provider, a faked
/// repository, and fake time, with no requests unless a fixture says so. Defaults apply unless a fixture says otherwise:
/// refresh 300 s, cooldown 30 s, load timeout 10 s, and a jitter factor of exactly 1 for both the backoff (5, 10, 20,
/// 40, 60 s) and the refresh deadline. A test advances time only once the service is parked on the wait it expects.
/// </summary>
public class SigningKeyRefreshServiceTests
{
    private static readonly TimeSpan _tick = TimeSpan.FromTicks(1);

    private sealed class FakeDbException(string message) : DbException(message);

    /// <summary>
    /// One service over one provider, store and clock; created in SetUp, disposed in TearDown. The provider and the
    /// service read <see cref="Clock"/>, whose wall clock a fixture may step on its own; <see cref="Time"/> is the
    /// elapsed time beneath it, and store calls, refreshes and waits are recorded in it.
    /// </summary>
    private sealed class Scheduler
    {
        public Scheduler(Random? random = null)
        {
            Time = new SchedulerTimeProvider(Start);
            Clock = new SkewableTimeProvider(Time);
            Store = new KeyRepositoryHarness(Time);
            Inner = Provider(Store, Clock, random: random);
            Observed = new ObservedSnapshotProvider(Inner) { Clock = Time };
            Service = new SigningKeyRefreshService(
                Observed,
                Options.Create(new IdentityOptions()),
                Clock,
                Logger,
                random ?? new FixedRandom(0.5)
            );
        }

        public SchedulerTimeProvider Time { get; }

        public SkewableTimeProvider Clock { get; }

        public KeyRepositoryHarness Store { get; }

        public SigningKeySnapshotProvider Inner { get; }

        public ObservedSnapshotProvider Observed { get; }

        public ILogger<SigningKeyRefreshService> Logger { get; } =
            A.Fake<ILogger<SigningKeyRefreshService>>();

        public SigningKeyRefreshService Service { get; }

        public Task StartAsync() => Service.StartAsync(CancellationToken.None);

        public Task WaitForStoreCallsAsync(int count) => Store.WaitForCallsAsync(count);

        public Task WaitForVersionAsync(long version) =>
            WaitUntilAsync(
                () => Inner.Current?.Version >= version,
                () => $"Snapshot version {Inner.Current?.Version} did not reach {version}."
            );

        public Task WaitForFailuresAsync(int count) =>
            WaitUntilAsync(
                () => Inner.Status.ConsecutiveFailures >= count,
                () => $"{Inner.Status.ConsecutiveFailures} consecutive failures, not {count}."
            );

        public Task WaitForOutstandingAsync() =>
            WaitUntilAsync(
                () => Inner.Status is { StoreOperationOutstanding: true, LoadInFlight: false },
                () => "The store operation did not outlive its attempt."
            );

        /// <summary>Status reads over a quiet real-time window: a parked service makes none.</summary>
        public async Task<int> StatusReadsWhileIdleAsync()
        {
            await Task.Delay(100);
            int before = Observed.StatusReads;
            await Task.Delay(200);
            return Observed.StatusReads - before;
        }

        public IReadOnlyList<DateTimeOffset> CallTimes => [.. Store.Calls.Select(call => call.At)];

        public async Task StopAsync()
        {
            await Service.StopAsync(CancellationToken.None).Bounded();
            Service.Dispose();
            Inner.Dispose();
        }
    }

    /// <summary>Scripts the store by call number (1-based).</summary>
    private static Func<CancellationToken, Task<IEnumerable<PublicKeyInfo>>> ByCall(
        Func<int, Task<IEnumerable<PublicKeyInfo>>> behavior
    )
    {
        int calls = 0;
        return _ => behavior(Interlocked.Increment(ref calls));
    }

    private static Task<IEnumerable<PublicKeyInfo>> Keys(params string[] keyIds) =>
        Task.FromResult<IEnumerable<PublicKeyInfo>>([.. keyIds.Select(KeyRepositoryHarness.Row)]);

    private static TaskCompletionSource<IEnumerable<PublicKeyInfo>> Blocked() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // (a) startup load, then the refresh cadence.
    [TestFixture]
    public class Given_a_healthy_store
    {
        private Scheduler _scheduler = null!;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();

            await _scheduler.WaitForVersionAsync(1);
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(300));
            _scheduler.Time.Advance(TimeSpan.FromSeconds(300));
            await _scheduler.WaitForVersionAsync(2);
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(600));
            _scheduler.Time.Advance(TimeSpan.FromSeconds(300));
            await _scheduler.WaitForVersionAsync(3);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_loads_at_startup_then_at_each_refresh_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(300), Start.AddSeconds(600));

        [Test]
        public void It_labels_the_first_attempt_startup_and_the_rest_timer() =>
            _scheduler
                .Observed.Refreshes.Select(refresh => refresh.Trigger)
                .Should()
                .Equal(
                    SigningKeyRefreshTrigger.Startup,
                    SigningKeyRefreshTrigger.Timer,
                    SigningKeyRefreshTrigger.Timer
                );

        [Test]
        public void It_logs_each_tick_wake() =>
            MessagesAt(_scheduler.Logger, LogLevel.Debug)
                .Should()
                .Contain(message => message.Contains("(Tick)") && message.Contains("12:10:00.0000000"));
    }

    // (b) failure → the next wake is the retry deadline, not the tick; (d) the startup failure is not fatal.
    [TestFixture]
    public class Given_a_failing_startup
    {
        private Scheduler _scheduler = null!;
        private bool _stoppedAfterStartupFailure;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.Fails(new FakeDbException("store down"));
            await _scheduler.StartAsync();

            await _scheduler.WaitForFailuresAsync(1);
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(5));
            _stoppedAfterStartupFailure = _scheduler.Service.ExecuteTask!.IsCompleted;

            _scheduler.Time.Advance(TimeSpan.FromSeconds(5));
            await _scheduler.WaitForFailuresAsync(2);
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(15));
            _scheduler.Time.Advance(TimeSpan.FromSeconds(10));
            await _scheduler.WaitForFailuresAsync(3);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_keeps_running_after_the_startup_failure() =>
            _stoppedAfterStartupFailure.Should().BeFalse();

        [Test]
        public void It_retries_at_each_retry_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(5), Start.AddSeconds(15));

        [Test]
        public void It_never_waits_for_the_tick() =>
            _scheduler.Time.Waits.Should().NotContain(wait => wait >= Start.AddSeconds(300));
    }

    // (c) stopping while parked: the loop ends and no further load starts.
    [TestFixture]
    public class Given_a_stopped_service
    {
        private Scheduler _scheduler = null!;
        private TaskStatus _executeStatus;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(300));

            await _scheduler.Service.StopAsync(CancellationToken.None).Bounded();
            _executeStatus = _scheduler.Service.ExecuteTask!.Status;
            _scheduler.Time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(100);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_ends_the_loop_without_a_fault() =>
            _executeStatus.Should().Be(TaskStatus.RanToCompletion);

        [Test]
        public void It_starts_no_further_load() => _scheduler.Store.Calls.Should().HaveCount(1);
    }

    // (c) stopping while the startup attempt is in flight: the stop does not wait for the load.
    [TestFixture]
    public class Given_a_service_stopped_during_a_load
    {
        private Scheduler _scheduler = null!;
        private TaskStatus _executeStatus;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.Gate();
            await _scheduler.StartAsync();
            await _scheduler.WaitForStoreCallsAsync(1);

            await _scheduler.Service.StopAsync(CancellationToken.None).Bounded();
            _executeStatus = _scheduler.Service.ExecuteTask!.Status;
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_ends_the_loop_without_a_fault() =>
            _executeStatus.Should().Be(TaskStatus.RanToCompletion);
    }

    // (e) the store recovers at R right after a failure at the backoff cap, with the jitter at its maximum and no
    // requests: the first attempt after R starts at the retry deadline and succeeds within R + 72 s + LoadTimeout.
    [TestFixture]
    public class Given_the_store_recovers_after_repeated_startup_failures
    {
        private Scheduler _scheduler = null!;
        private DateTimeOffset _recoveredAt;
        private DateTimeOffset _retryDeadline;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler(new FixedRandom(1 - 1e-9));
            _scheduler.Store.Fails(new FakeDbException("store down"));
            await _scheduler.StartAsync();

            // Failures 1–6; the fifth and sixth are at the 60 s cap, scaled by ≈ 1.2.
            for (int failures = 1; failures < 6; failures++)
            {
                await _scheduler.WaitForFailuresAsync(failures);
                DateTimeOffset next = _scheduler.Inner.NextAttemptAt;
                await _scheduler.Time.WaitForWaitAsync(next);
                _scheduler.Time.SetUtcNow(next);
            }

            await _scheduler.WaitForFailuresAsync(6);
            _retryDeadline = _scheduler.Inner.NextAttemptAt;
            await _scheduler.Time.WaitForWaitAsync(_retryDeadline);

            _recoveredAt = _scheduler.Time.GetUtcNow() + _tick;
            _scheduler.Time.SetUtcNow(_recoveredAt);
            _scheduler.Store.ReturnsKeys("key-1");
            _scheduler.Time.SetUtcNow(_retryDeadline);
            await _scheduler.WaitForVersionAsync(1);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_reached_the_backoff_cap() =>
            (_retryDeadline - _recoveredAt)
                .Should()
                .BeCloseTo(TimeSpan.FromSeconds(72), TimeSpan.FromSeconds(0.01));

        [Test]
        public void It_recovers_at_the_retry_deadline() =>
            _scheduler.Inner.Current!.RetrievedAt.Should().Be(_retryDeadline);

        [Test]
        public void It_recovers_within_the_bound() =>
            _scheduler
                .Inner.Current!.RetrievedAt.Should()
                .BeOnOrBefore(_recoveredAt + TimeSpan.FromSeconds(72) + TimeSpan.FromSeconds(10));

        [Test]
        public void It_made_one_call_per_attempt() => _scheduler.Store.Calls.Should().HaveCount(7);
    }

    // (f) a retired key disappears once the timer drives a refresh past T_prop, with no manual refresh.
    [TestFixture]
    public class Given_a_key_retired_after_startup
    {
        private Scheduler _scheduler = null!;
        private bool _presentBefore;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1", "key-2");
            await _scheduler.StartAsync();
            await _scheduler.WaitForVersionAsync(1);
            _presentBefore = _scheduler.Inner.Current!.ContainsKeyId("key-2");

            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(300));
            _scheduler.Time.Advance(TimeSpan.FromSeconds(300 + 10));
            await _scheduler.WaitForVersionAsync(2);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_had_the_key_before() => _presentBefore.Should().BeTrue();

        [Test]
        public void It_no_longer_trusts_the_retired_key() =>
            _scheduler.Inner.Current!.ContainsKeyId("key-2").Should().BeFalse();

        [Test]
        public void It_keeps_the_active_key() =>
            _scheduler.Inner.Current!.ContainsKeyId("key-1").Should().BeTrue();
    }

    // (g) a request-triggered failure during the sleep moves the deadline: the signal wakes the service, which then
    // waits for the new retry deadline instead of the tick, without waking in a loop.
    [TestFixture]
    public class Given_a_request_triggered_failure_during_the_sleep
    {
        private Scheduler _scheduler = null!;
        private int _statusReadsWhileParked;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(300));

            // 31 s: past the cooldown, an unknown kid triggers a load that fails; the retry deadline is 36 s.
            _scheduler.Time.Advance(TimeSpan.FromSeconds(31));
            _scheduler.Store.Fails(new FakeDbException("store down"));
            await _scheduler.Inner.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None).Bounded();
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(36));
            _statusReadsWhileParked = await _scheduler.StatusReadsWhileIdleAsync();

            _scheduler.Store.ReturnsKeys("key-1", "key-2");
            _scheduler.Time.Advance(TimeSpan.FromSeconds(5));
            await _scheduler.WaitForVersionAsync(2);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_retries_at_the_moved_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(31), Start.AddSeconds(36));

        [Test]
        public void It_does_not_wake_in_a_loop() => _statusReadsWhileParked.Should().Be(0);

        [Test]
        public void It_logs_the_signal_wake_with_the_new_deadline() =>
            MessagesAt(_scheduler.Logger, LogLevel.Debug)
                .Should()
                .Contain(message => message.Contains("(Signal)") && message.Contains("12:00:36.0000000"));

        [Test]
        public void It_logs_the_retry_deadline_wake() =>
            MessagesAt(_scheduler.Logger, LogLevel.Debug)
                .Should()
                .Contain(message => message.Contains("(RetryDeadline)"));
    }

    // (h) the retry deadline reached exactly: nothing a tick before it, the attempt at exactly it.
    [TestFixture]
    public class Given_the_retry_deadline_is_reached_exactly
    {
        private Scheduler _scheduler = null!;
        private int _callsJustBefore;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.Behavior = ByCall(call =>
                call == 1
                    ? Task.FromException<IEnumerable<PublicKeyInfo>>(new FakeDbException("store down"))
                    : Keys("key-1")
            );
            await _scheduler.StartAsync();
            await _scheduler.WaitForFailuresAsync(1);
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(5));

            _scheduler.Time.Advance(TimeSpan.FromSeconds(5) - _tick);
            await Task.Delay(100);
            _callsJustBefore = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(_tick);
            await _scheduler.WaitForVersionAsync(1);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_does_not_start_before_the_deadline() => _callsJustBefore.Should().Be(1);

        [Test]
        public void It_starts_at_exactly_the_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(5));
    }

    // (h) past equality: with a snapshot published, a request-triggered attempt outlives its deadline (retry deadline
    // 46 s) and its operation ends at 50 s. The due time stays the retry deadline, now past, so the service loads at
    // 50 s; it never switches to the refresh deadline (300 s).
    [TestFixture]
    public class Given_the_retry_deadline_passed_while_an_operation_was_outstanding
    {
        private Scheduler _scheduler = null!;
        private int _callsWhileOutstanding;
        private int _statusReadsWhileOutstanding;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            TaskCompletionSource<IEnumerable<PublicKeyInfo>> blocked = Blocked();
            _scheduler.Store.Behavior = ByCall(call =>
                call switch
                {
                    1 => Keys("key-1"),
                    2 => blocked.Task, // ignores its token
                    _ => Keys("key-1", "key-2"),
                }
            );
            await _scheduler.StartAsync();
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(300));

            _scheduler.Time.Advance(TimeSpan.FromSeconds(31));
            Task<SigningKeyUnknownKeyOutcome> unknownKey = _scheduler.Inner.TryRefreshForUnknownKeyAsync(
                "key-2",
                CancellationToken.None
            );
            await _scheduler.WaitForStoreCallsAsync(2);
            _scheduler.Time.Advance(TimeSpan.FromSeconds(10));
            await unknownKey.Bounded();
            await _scheduler.WaitForOutstandingAsync();

            _scheduler.Time.Advance(TimeSpan.FromSeconds(9));
            _statusReadsWhileOutstanding = await _scheduler.StatusReadsWhileIdleAsync();
            _callsWhileOutstanding = _scheduler.Store.Calls.Count;

            blocked.SetResult([KeyRepositoryHarness.Row("key-late")]);
            await _scheduler.WaitForVersionAsync(2);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_starts_nothing_while_the_operation_is_outstanding() =>
            _callsWhileOutstanding.Should().Be(2);

        [Test]
        public void It_waits_without_spinning_on_the_past_deadline() =>
            _statusReadsWhileOutstanding.Should().Be(0);

        [Test]
        public void It_loads_when_the_operation_ends() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(31), Start.AddSeconds(50));

        [Test]
        public void It_publishes_the_new_load_not_the_late_result() =>
            _scheduler.Inner.Current!.ContainsKeyId("key-late").Should().BeFalse();
    }

    // Review requirement: startup times out (10 s), the backoff elapses (15 s) with the operation still outstanding,
    // the late result arrives at 20 s, and the service recovers at once — no requests at any point.
    [TestFixture]
    public class Given_a_startup_timeout_whose_operation_outlives_the_backoff
    {
        private Scheduler _scheduler = null!;
        private SigningKeyProviderStatus _whileOutstanding = null!;
        private int _statusReadsWhileOutstanding;
        private int _callsWhileOutstanding;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            TaskCompletionSource<IEnumerable<PublicKeyInfo>> blocked = Blocked();
            _scheduler.Store.Behavior = ByCall(call => call == 1 ? blocked.Task : Keys("key-new"));
            await _scheduler.StartAsync();
            await _scheduler.WaitForStoreCallsAsync(1);

            _scheduler.Time.Advance(TimeSpan.FromSeconds(10));
            await _scheduler.WaitForFailuresAsync(1);
            await _scheduler.WaitForOutstandingAsync();

            _scheduler.Time.Advance(TimeSpan.FromSeconds(10));
            _statusReadsWhileOutstanding = await _scheduler.StatusReadsWhileIdleAsync();
            _callsWhileOutstanding = _scheduler.Store.Calls.Count;
            _whileOutstanding = _scheduler.Inner.Status;

            blocked.SetResult([KeyRepositoryHarness.Row("key-late")]);
            await _scheduler.WaitForVersionAsync(1);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_is_past_the_retry_deadline_while_outstanding() =>
            _whileOutstanding.NextAttemptAt.Should().Be(Start.AddSeconds(15));

        [Test]
        public void It_starts_no_overlapping_load() => _callsWhileOutstanding.Should().Be(1);

        [Test]
        public void It_waits_without_spinning_on_the_expired_deadline() =>
            _statusReadsWhileOutstanding.Should().Be(0);

        [Test]
        public void It_recovers_when_the_late_operation_ends() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(20));

        [Test]
        public void It_discards_the_late_result() =>
            _scheduler.Inner.Current!.ContainsKeyId("key-late").Should().BeFalse();

        [Test]
        public void It_publishes_the_recovery_load() =>
            _scheduler.Inner.Current!.ContainsKeyId("key-new").Should().BeTrue();

        [Test]
        public void It_asked_for_no_load_while_outstanding() =>
            _scheduler
                .Observed.Refreshes.Should()
                .Equal(
                    (Start, SigningKeyRefreshTrigger.Startup),
                    (Start.AddSeconds(20), SigningKeyRefreshTrigger.Timer)
                );
    }

    // Review requirement: the signal is captured before the status is read. The outstanding operation ends right after
    // the service reads a status that still shows it running; a service that read the signal afterwards would wait
    // for the next transition, which never comes, and miss the 15 s retry deadline.
    [TestFixture]
    public class Given_the_operation_ends_between_the_status_read_and_the_wait
    {
        private Scheduler _scheduler = null!;
        private bool _released;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            TaskCompletionSource<IEnumerable<PublicKeyInfo>> blocked = Blocked();
            _scheduler.Store.Behavior = ByCall(call => call == 1 ? blocked.Task : Keys("key-new"));
            _scheduler.Observed.AfterStatusRead(
                status => status is { StoreOperationOutstanding: true, LoadInFlight: false },
                () =>
                {
                    Task release = _scheduler.Inner.AttemptStateChanged;
                    blocked.SetResult([KeyRepositoryHarness.Row("key-late")]);
                    _released = release.Wait(TimeSpan.FromSeconds(10));
                }
            );
            await _scheduler.StartAsync();
            await _scheduler.WaitForStoreCallsAsync(1);

            _scheduler.Time.Advance(TimeSpan.FromSeconds(10));
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(15));
            _scheduler.Time.Advance(TimeSpan.FromSeconds(5));
            await _scheduler.WaitForVersionAsync(1);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_released_the_operation_inside_the_window() => _released.Should().BeTrue();

        [Test]
        public void It_recovers_at_the_retry_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(15));
    }

    // (i) success authorizes nothing: after the startup success the gate is eligible at once, but the next load is due
    // only at the refresh deadline.
    [TestFixture]
    public class Given_a_successful_load
    {
        private Scheduler _scheduler = null!;
        private int _callsAfterSuccess;
        private int _callsJustBeforeTheDeadline;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(300));
            await Task.Delay(200);
            _callsAfterSuccess = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(TimeSpan.FromSeconds(300) - _tick);
            await Task.Delay(100);
            _callsJustBeforeTheDeadline = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(_tick);
            await _scheduler.WaitForVersionAsync(2);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_does_not_reload_on_success() => _callsAfterSuccess.Should().Be(1);

        [Test]
        public void It_does_not_reload_before_the_refresh_deadline() =>
            _callsJustBeforeTheDeadline.Should().Be(1);

        [Test]
        public void It_reloads_at_exactly_the_refresh_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(300));
    }

    // (i) a request-triggered success during the sleep signals the service, which recomputes the refresh deadline from
    // that success (400 s) and starts nothing at the signal or at the old deadline (300 s).
    [TestFixture]
    public class Given_a_request_triggered_success_during_the_sleep
    {
        private Scheduler _scheduler = null!;
        private int _callsAfterTheSignal;
        private int _callsAtTheOldDeadline;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(300));

            _scheduler.Time.Advance(TimeSpan.FromSeconds(100));
            _scheduler.Store.ReturnsKeys("key-1", "key-2");
            await _scheduler.Inner.TryRefreshForUnknownKeyAsync("key-2", CancellationToken.None).Bounded();
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(400));
            await Task.Delay(200);
            _callsAfterTheSignal = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(TimeSpan.FromSeconds(200));
            await Task.Delay(100);
            _callsAtTheOldDeadline = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(TimeSpan.FromSeconds(100));
            await _scheduler.WaitForVersionAsync(3);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_starts_nothing_at_the_signal() => _callsAfterTheSignal.Should().Be(2);

        [Test]
        public void It_starts_nothing_at_the_old_deadline() => _callsAtTheOldDeadline.Should().Be(2);

        [Test]
        public void It_reloads_at_the_recomputed_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(100), Start.AddSeconds(400));
    }

    // (m) the service reads a status whose refresh deadline (300 s) has elapsed; before it is admitted, an unknown-kid
    // request publishes fresh keys. Admission is conditional on the observed state, so the provider refuses and the
    // service waits for the new deadline (600 s) instead of loading again.
    [TestFixture]
    public class Given_a_request_publishes_between_the_status_read_and_admission
    {
        private Scheduler _scheduler = null!;
        private SigningKeyUnknownKeyOutcome? _requestOutcome;
        private int _callsAfterTheRace;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(300));

            _scheduler.Store.ReturnsKeys("key-1", "key-2");
            _scheduler.Observed.AfterStatusRead(
                status =>
                    status is { LastOutcome: SigningKeyRefreshOutcome.Succeeded, LoadInFlight: false }
                    && _scheduler.Time.GetUtcNow() >= Start.AddSeconds(300),
                () =>
                {
                    Task<SigningKeyUnknownKeyOutcome> request = _scheduler.Inner.TryRefreshForUnknownKeyAsync(
                        "key-2",
                        CancellationToken.None
                    );
                    _requestOutcome = request.Wait(TimeSpan.FromSeconds(10)) ? request.Result : null;
                }
            );
            _scheduler.Time.Advance(TimeSpan.FromSeconds(300));
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(600));
            await Task.Delay(200);
            _callsAfterTheRace = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(TimeSpan.FromSeconds(300));
            await _scheduler.WaitForVersionAsync(3);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_published_from_the_request_inside_the_window() =>
            _requestOutcome.Should().Be(SigningKeyUnknownKeyOutcome.RefreshedFound);

        [Test]
        public void It_asked_for_admission_from_the_stale_status() =>
            _scheduler
                .Observed.Refreshes.Should()
                .Contain((Start.AddSeconds(300), SigningKeyRefreshTrigger.Timer));

        [Test]
        public void It_starts_no_additional_load() => _callsAfterTheRace.Should().Be(2);

        [Test]
        public void It_reloads_at_the_new_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(300), Start.AddSeconds(600));
    }

    // (n) a source that throws ObjectDisposedException while the provider is alive: an ordinary retrieval failure, so
    // the service keeps running, retries at the retry deadline, and recovers without requests.
    [TestFixture]
    public class Given_a_source_that_throws_ObjectDisposedException_once
    {
        private Scheduler _scheduler = null!;
        private SigningKeyRefreshOutcome? _firstOutcome;
        private bool _runningAfterTheFailure;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.Behavior = ByCall(call =>
                call == 1
                    ? Task.FromException<IEnumerable<PublicKeyInfo>>(
                        new ObjectDisposedException("DbConnection")
                    )
                    : Keys("key-1")
            );
            await _scheduler.StartAsync();
            await _scheduler.WaitForFailuresAsync(1);
            _firstOutcome = _scheduler.Inner.Status.LastOutcome;
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(5));
            _runningAfterTheFailure = !_scheduler.Service.ExecuteTask!.IsCompleted;

            _scheduler.Time.Advance(TimeSpan.FromSeconds(5));
            await _scheduler.WaitForVersionAsync(1);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_records_a_retrieval_failure() =>
            _firstOutcome
                .Should()
                .BeOfType<SigningKeyRefreshOutcome.Failed>()
                .Which.Exception.Should()
                .BeOfType<ObjectDisposedException>();

        [Test]
        public void It_keeps_running() => _runningAfterTheFailure.Should().BeTrue();

        [Test]
        public void It_recovers_at_the_retry_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(5));
    }

    [TestFixture]
    public class Given_the_lowest_refresh_jitter
    {
        private Scheduler _scheduler = null!;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler(new FixedRandom(0.0));
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.WaitForVersionAsync(1);
            await _scheduler.Time.WaitForWaitAsync(Start.AddSeconds(270));
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_waits_ninety_percent_of_the_refresh_interval() =>
            _scheduler.Time.Waits.Should().Equal(Start.AddSeconds(270));
    }

    [TestFixture]
    public class Given_a_disposed_provider
    {
        private Scheduler _scheduler = null!;
        private TaskStatus _executeStatus;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Inner.Dispose();
            await _scheduler.StartAsync();
            await WaitUntilAsync(
                () => _scheduler.Service.ExecuteTask?.IsCompleted == true,
                () => "The service kept running over a disposed provider."
            );
            _executeStatus = _scheduler.Service.ExecuteTask!.Status;
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_ends_the_loop() => _executeStatus.Should().Be(TaskStatus.RanToCompletion);

        [Test]
        public void It_never_calls_the_store() => _scheduler.Store.Calls.Should().BeEmpty();
    }

    /// <summary>The wait the service requests for a positive remainder: whole milliseconds, rounded up, at least one.</summary>
    private static TimeSpan RoundedUp(TimeSpan remaining) =>
        TimeSpan.FromTicks(
            Math.Max(1, (remaining.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond)
                * TimeSpan.TicksPerMillisecond
        );

    private static bool IsWholeMilliseconds(TimeSpan duration) =>
        duration.Ticks % TimeSpan.TicksPerMillisecond == 0 && duration >= TimeSpan.FromMilliseconds(1);

    /// <summary>A status-read cap far above what a correct service needs between the arming and the end of a fixture.</summary>
    private const int SpinCap = 20;

    /// <summary>A jitter sample that makes both the refresh deadline and the first backoff fall between two milliseconds.</summary>
    private const double FractionalSample = 0.5000083333;

    // Review remediation R3.2 (e): a failure, then the wall clock set back while the service is parked on the retry
    // deadline. The wait is relative and the retry delay counts elapsed time, so the attempt starts at exactly the 5 s
    // backoff of elapsed time, not 2 h later.
    [TestFixture]
    public class Given_a_failure_and_then_the_wall_clock_set_back
    {
        private Scheduler _scheduler = null!;
        private int _callsJustBefore;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.Behavior = ByCall(call =>
                call == 1
                    ? Task.FromException<IEnumerable<PublicKeyInfo>>(new FakeDbException("store down"))
                    : Keys("key-1")
            );
            await _scheduler.StartAsync();
            await _scheduler.WaitForFailuresAsync(1);
            await _scheduler.Time.Parked(wait => wait.DueAt == Start.AddSeconds(5)).Bounded();

            _scheduler.Clock.StepWallClock(TimeSpan.FromHours(-2));
            _scheduler.Time.Advance(TimeSpan.FromSeconds(5) - _tick);
            _callsJustBefore = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(_tick);
            await _scheduler.WaitForVersionAsync(1);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_does_not_start_before_the_retry_deadline() => _callsJustBefore.Should().Be(1);

        [Test]
        public void It_starts_at_exactly_the_retry_deadline_of_elapsed_time() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(5));
    }

    // R3.2 (f): a success, then the wall clock set back while the service is parked on the refresh deadline. The step
    // wakes nothing, and the scheduled load starts at exactly 300 s of elapsed time.
    [TestFixture]
    public class Given_a_success_and_then_the_wall_clock_set_back
    {
        private Scheduler _scheduler = null!;
        private int _callsJustBefore;
        private int _waitsJustBefore;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.WaitForVersionAsync(1);
            await _scheduler.Time.Parked(wait => wait.DueAt == Start.AddSeconds(300)).Bounded();

            _scheduler.Clock.StepWallClock(TimeSpan.FromHours(-2));
            _scheduler.Time.Advance(TimeSpan.FromSeconds(300) - _tick);
            _callsJustBefore = _scheduler.Store.Calls.Count;
            _waitsJustBefore = _scheduler.Time.CreatedWaits.Count;

            _scheduler.Time.Advance(_tick);
            await _scheduler.WaitForVersionAsync(2);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_does_not_reload_before_the_refresh_deadline() => _callsJustBefore.Should().Be(1);

        [Test]
        public void It_does_not_recompute_on_the_step() => _waitsJustBefore.Should().Be(1);

        [Test]
        public void It_reloads_at_exactly_the_refresh_deadline_of_elapsed_time() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(300));
    }

    // R3.2 (g1): the wall clock set forward while the service is parked on its refresh timer wakes nothing. A request
    // then finds the snapshot expired and awaits a load; its publication signals the service, which moves to the new
    // snapshot's deadline (100 s + 300 s).
    [TestFixture]
    public class Given_a_forward_step_while_parked_and_then_a_request
    {
        private Scheduler _scheduler = null!;
        private int _callsAtTheStep;
        private SigningKeySnapshot _served = null!;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.WaitForVersionAsync(1);
            await _scheduler.Time.Parked(wait => wait.DueAt == Start.AddSeconds(300)).Bounded();
            _scheduler.Time.Advance(TimeSpan.FromSeconds(100));

            _scheduler.Observed.ArmSpinDetector(SpinCap);
            _scheduler.Clock.StepWallClock(TimeSpan.FromHours(2));
            _callsAtTheStep = _scheduler.Store.Calls.Count;

            _scheduler.Store.ReturnsKeys("key-1", "key-2");
            _served = await _scheduler.Inner.GetUsableAsync(CancellationToken.None).Bounded();
            await _scheduler
                .Time.Parked(wait =>
                    wait.CreatedAt == Start.AddSeconds(100) && wait.DueTime == TimeSpan.FromSeconds(300)
                )
                .Bounded();

            _scheduler.Time.Advance(TimeSpan.FromSeconds(300));
            await _scheduler.WaitForVersionAsync(3);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_starts_nothing_at_the_step() => _callsAtTheStep.Should().Be(1);

        [Test]
        public void It_serves_the_request_from_a_new_load() =>
            _served.ContainsKeyId("key-2").Should().BeTrue();

        [Test]
        public void It_reloads_at_the_new_snapshots_deadline() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(100), Start.AddSeconds(400));

        [Test]
        public void It_does_not_spin() => _scheduler.Observed.SpinDetected.IsCompleted.Should().BeFalse();
    }

    // R3.2 (g2): the same step with no request. The pending timer fires at 300 s of elapsed time; the service then finds
    // the snapshot due, by its combined age, and starts exactly one load. The next snapshot's ages agree again, so the
    // cadence returns to normal: the next wait is a plain 300 s.
    [TestFixture]
    public class Given_a_forward_step_while_parked_and_no_request
    {
        private Scheduler _scheduler = null!;
        private int _callsAtTheStep;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.WaitForVersionAsync(1);
            await _scheduler.Time.Parked(wait => wait.DueAt == Start.AddSeconds(300)).Bounded();

            _scheduler.Observed.ArmSpinDetector(SpinCap);
            _scheduler.Clock.StepWallClock(TimeSpan.FromHours(2));
            _callsAtTheStep = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(TimeSpan.FromSeconds(300));
            await _scheduler
                .Time.Parked(wait =>
                    wait.CreatedAt == Start.AddSeconds(300) && wait.DueTime == TimeSpan.FromSeconds(300)
                )
                .Bounded();
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_starts_nothing_at_the_step() => _callsAtTheStep.Should().Be(1);

        [Test]
        public void It_loads_once_when_the_pending_timer_fires() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(300));

        [Test]
        public void It_returns_to_the_normal_cadence() =>
            _scheduler
                .Time.CreatedWaits.Should()
                .Equal(
                    new SchedulerWait(Start, TimeSpan.FromSeconds(300)),
                    new SchedulerWait(Start.AddSeconds(300), TimeSpan.FromSeconds(300))
                );

        [Test]
        public void It_does_not_spin() => _scheduler.Observed.SpinDetected.IsCompleted.Should().BeFalse();
    }

    // R3.2 (h): the wall clock set forward during a backoff opens nothing early. A cold request 2 s into the 5 s backoff
    // is refused without a store call, and the service retries at exactly 5 s of elapsed time.
    [TestFixture]
    public class Given_a_forward_step_during_a_backoff
    {
        private Scheduler _scheduler = null!;
        private SigningKeysUnavailableException _refused = null!;
        private int _callsAfterTheRequest;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            _scheduler.Store.Behavior = ByCall(call =>
                call == 1
                    ? Task.FromException<IEnumerable<PublicKeyInfo>>(new FakeDbException("store down"))
                    : Keys("key-1")
            );
            await _scheduler.StartAsync();
            await _scheduler.WaitForFailuresAsync(1);
            await _scheduler.Time.Parked(wait => wait.DueAt == Start.AddSeconds(5)).Bounded();

            _scheduler.Time.Advance(TimeSpan.FromSeconds(2));
            _scheduler.Clock.StepWallClock(TimeSpan.FromHours(2));
            Func<Task> request = () => _scheduler.Inner.GetUsableAsync(CancellationToken.None);
            _refused = (await request.Should().ThrowAsync<SigningKeysUnavailableException>()).Which;
            _callsAfterTheRequest = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(TimeSpan.FromSeconds(3));
            await _scheduler.WaitForVersionAsync(1);
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_refuses_the_request_during_the_backoff() =>
            _refused.Outcome.Should().BeOfType<SigningKeyRefreshOutcome.Refused>();

        [Test]
        public void It_makes_no_store_call_for_the_request() => _callsAfterTheRequest.Should().Be(1);

        [Test]
        public void It_retries_at_exactly_the_backoff_of_elapsed_time() =>
            _scheduler.CallTimes.Should().Equal(Start, Start.AddSeconds(5));
    }

    // R3.2 (i): a refresh deadline between two milliseconds. The service waits a whole number of milliseconds, rounded
    // up, so its timer is never due before the deadline; nothing starts before the deadline; the load starts when the
    // rounded wait ends; the status is read a bounded number of times. A service that passed the fraction to Task.Delay
    // would have its timer truncated to the millisecond before the deadline, wake early, and recompute without waiting
    // until time moved on or the spin detector stopped it.
    [TestFixture]
    public class Given_a_refresh_deadline_between_two_milliseconds
    {
        // The refresh deadline drawn for the sample, by the service's own formula.
        private readonly TimeSpan _refreshAfter =
            TimeSpan.FromSeconds(300) * (1 - 0.1 + (2 * 0.1 * FractionalSample));
        private Scheduler _scheduler = null!;
        private int _callsJustBefore;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler(new FixedRandom(FractionalSample));
            _scheduler.Store.ReturnsKeys("key-1");
            await _scheduler.StartAsync();
            await _scheduler.WaitForVersionAsync(1);
            await _scheduler.Time.Parked(wait => wait.CreatedAt == Start).Bounded();
            _scheduler.Observed.ArmSpinDetector(SpinCap);

            _scheduler.Time.Advance(_refreshAfter - _tick);
            _callsJustBefore = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(RoundedUp(_refreshAfter) - (_refreshAfter - _tick));
            await WaitUntilAsync(
                () => _scheduler.Inner.Current?.Version >= 2 || _scheduler.Observed.SpinDetected.IsCompleted,
                () => "Neither a second load nor a spin happened."
            );
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_uses_a_deadline_between_two_milliseconds() =>
            (_refreshAfter.Ticks % TimeSpan.TicksPerMillisecond).Should().NotBe(0);

        [Test]
        public void It_waits_whole_milliseconds() =>
            _scheduler.Time.CreatedWaits.Should().OnlyContain(wait => IsWholeMilliseconds(wait.DueTime));

        [Test]
        public void It_never_wakes_before_the_deadline() =>
            _scheduler.Time.CreatedWaits[0].DueAt.Should().BeOnOrAfter(Start + _refreshAfter);

        [Test]
        public void It_does_not_start_before_the_deadline() => _callsJustBefore.Should().Be(1);

        [Test]
        public void It_starts_when_the_rounded_wait_ends() =>
            _scheduler.CallTimes.Should().Equal(Start, Start + RoundedUp(_refreshAfter));

        [Test]
        public void It_does_not_spin()
        {
            _scheduler.Observed.SpinDetected.IsCompleted.Should().BeFalse();
            _scheduler.Observed.StatusReadsSinceArmed.Should().BeLessThanOrEqualTo(SpinCap);
        }
    }

    // R3.2 (j): a stationary clock half a millisecond before the retry deadline. The startup attempt times out at 10 s and
    // its store operation outlives it, so the service waits on the signal alone. The fake clock is advanced to 14.9995 s
    // and held; the operation then ends, and its signal makes the service recompute with 0.5 ms left. The synchronization
    // point is the wait created by that recomputation. A correct service parks on a 1 ms timer and reads the status a
    // bounded number of times; one that truncates the wait recomputes without waiting until the spin detector stops it.
    [TestFixture]
    public class Given_a_retry_deadline_half_a_millisecond_away_on_a_stationary_clock
    {
        private static readonly TimeSpan _remainder = TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2);
        private Scheduler _scheduler = null!;
        private SchedulerWait? _waitAtTheRemainder;
        private bool _spunAtTheRemainder;
        private int _readsAtTheRemainder;
        private int _callsAtTheRemainder;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler();
            TaskCompletionSource<IEnumerable<PublicKeyInfo>> blocked = Blocked();
            _scheduler.Store.Behavior = ByCall(call => call == 1 ? blocked.Task : Keys("key-1"));
            await _scheduler.StartAsync();
            await _scheduler.WaitForStoreCallsAsync(1);

            _scheduler.Time.Advance(TimeSpan.FromSeconds(10));
            await _scheduler.WaitForFailuresAsync(1);
            await _scheduler.WaitForOutstandingAsync();
            _scheduler.Time.Advance(TimeSpan.FromSeconds(5) - _remainder);

            DateTimeOffset atTheRemainder = Start.AddSeconds(15) - _remainder;
            _scheduler.Observed.ArmSpinDetector(SpinCap);
            Task<SchedulerWait> parked = _scheduler.Time.Parked(wait => wait.CreatedAt == atTheRemainder);
            blocked.SetResult([KeyRepositoryHarness.Row("key-late")]);
            await Task.WhenAny(parked, _scheduler.Observed.SpinDetected).Bounded();

            _waitAtTheRemainder = parked.IsCompleted ? parked.Result : null;
            _spunAtTheRemainder = _scheduler.Observed.SpinDetected.IsCompleted;
            _readsAtTheRemainder = _scheduler.Observed.StatusReadsSinceArmed;
            _callsAtTheRemainder = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(
                () => _scheduler.Store.Calls.Count >= 2 || _scheduler.Observed.SpinDetected.IsCompleted,
                () => "Neither the retry nor a spin happened."
            );
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_parks_on_a_whole_millisecond_at_the_remainder() =>
            _waitAtTheRemainder
                .Should()
                .Be(new SchedulerWait(Start.AddSeconds(15) - _remainder, TimeSpan.FromMilliseconds(1)));

        [Test]
        public void It_does_not_spin_at_the_remainder()
        {
            _spunAtTheRemainder.Should().BeFalse();
            _readsAtTheRemainder.Should().BeLessThanOrEqualTo(SpinCap);
        }

        [Test]
        public void It_starts_nothing_at_the_remainder() => _callsAtTheRemainder.Should().Be(1);

        [Test]
        public void It_retries_once_when_the_millisecond_has_elapsed() =>
            _scheduler
                .CallTimes.Should()
                .Equal(Start, Start.AddSeconds(15) - _remainder + TimeSpan.FromMilliseconds(1));
    }

    // R3.2 (k): (i) on the retry path. A first backoff between two milliseconds: whole-millisecond waits never due before
    // the backoff has elapsed, no retry before then, the retry when the rounded wait ends, and bounded status reads.
    [TestFixture]
    public class Given_a_backoff_between_two_milliseconds
    {
        // The first backoff drawn for the sample, by the provider's own formula.
        private readonly TimeSpan _backoff =
            TimeSpan.FromSeconds(5) * (1 - 0.2 + (2 * 0.2 * FractionalSample));
        private Scheduler _scheduler = null!;
        private int _callsJustBefore;

        [SetUp]
        public async Task Act()
        {
            _scheduler = new Scheduler(new FixedRandom(FractionalSample));
            _scheduler.Store.Behavior = ByCall(call =>
                call == 1
                    ? Task.FromException<IEnumerable<PublicKeyInfo>>(new FakeDbException("store down"))
                    : Keys("key-1")
            );
            await _scheduler.StartAsync();
            await _scheduler.WaitForFailuresAsync(1);
            await _scheduler.Time.Parked(wait => wait.CreatedAt == Start).Bounded();
            _scheduler.Observed.ArmSpinDetector(SpinCap);

            _scheduler.Time.Advance(_backoff - _tick);
            _callsJustBefore = _scheduler.Store.Calls.Count;

            _scheduler.Time.Advance(RoundedUp(_backoff) - (_backoff - _tick));
            await WaitUntilAsync(
                () => _scheduler.Inner.Current is not null || _scheduler.Observed.SpinDetected.IsCompleted,
                () => "Neither the retry nor a spin happened."
            );
        }

        [TearDown]
        public Task TearDown() => _scheduler.StopAsync();

        [Test]
        public void It_uses_a_backoff_between_two_milliseconds() =>
            (_backoff.Ticks % TimeSpan.TicksPerMillisecond).Should().NotBe(0);

        [Test]
        public void It_waits_whole_milliseconds() =>
            _scheduler.Time.CreatedWaits.Should().OnlyContain(wait => IsWholeMilliseconds(wait.DueTime));

        [Test]
        public void It_never_wakes_before_the_backoff() =>
            _scheduler.Time.CreatedWaits[0].DueAt.Should().BeOnOrAfter(Start + _backoff);

        [Test]
        public void It_does_not_retry_before_the_backoff() => _callsJustBefore.Should().Be(1);

        [Test]
        public void It_retries_when_the_rounded_wait_ends() =>
            _scheduler.CallTimes.Should().Equal(Start, Start + RoundedUp(_backoff));

        [Test]
        public void It_does_not_spin()
        {
            _scheduler.Observed.SpinDetected.IsCompleted.Should().BeFalse();
            _scheduler.Observed.StatusReadsSinceArmed.Should().BeLessThanOrEqualTo(SpinCap);
        }
    }
}
