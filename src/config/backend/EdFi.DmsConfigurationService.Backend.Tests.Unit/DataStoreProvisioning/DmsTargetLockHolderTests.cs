// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The gate, heartbeat, loss, and disposal semantics of the provider-neutral holder (D-12, round-4
/// finding 1, revision 5a): mutual exclusion without any waiter order, a skipped heartbeat is not loss, a
/// deadline is not loss, loss cancels the in-flight command, and disposal waits for the active command
/// and is idempotent. Every wait is on a deterministic signal or on the fake clock.
/// </summary>
public class DmsTargetLockHolderTests
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _probeDeadline = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _commandDeadline = TimeSpan.FromMinutes(1);
    private static readonly DmsServerSessionId _session = new(
        42,
        new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)
    );

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    private static FakeDmsTargetLockHolder Holder(FakeTimeProvider clock) =>
        new(_session, _interval, _probeDeadline, clock);

    [TestFixture]
    public class Given_a_heartbeat_becomes_due_while_a_command_holds_the_gate
    {
        private FakeTimeProvider _clock = null!;
        private FakeDmsTargetLockHolder _holder = null!;
        private CancellationToken _commandToken;
        private int _skippedWhileBlocked;
        private int _probesWhileBlocked;
        private bool _lostWhileBlocked;
        private bool _commandCancelledWhileBlocked;
        private int _result;
        private int _probesAfterRelease;

        [SetUp]
        public async Task Setup()
        {
            _clock = Clock();
            _holder = Holder(_clock);
            _holder.StartHeartbeat();
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> command = _holder.RunAsync(
                async token =>
                {
                    _commandToken = token;
                    await release.Task;
                    return 7;
                },
                _commandDeadline,
                CancellationToken.None
            );

            _clock.Advance(_interval);
            _skippedWhileBlocked = _holder.Heartbeat.SkippedProbes;
            _probesWhileBlocked = _holder.Heartbeat.CompletedProbes;
            _lostWhileBlocked = _holder.IsLost;
            _commandCancelledWhileBlocked = _commandToken.IsCancellationRequested;

            release.SetResult();
            _result = await command;

            _clock.Advance(_interval);
            _probesAfterRelease = _holder.Heartbeat.CompletedProbes;
        }

        [TearDown]
        public async Task TearDown() => await _holder.DisposeAsync();

        [Test]
        public void It_skips_the_probe_instead_of_waiting() =>
            (_skippedWhileBlocked, _probesWhileBlocked).Should().Be((1, 0));

        [Test]
        public void It_declares_no_loss() => _lostWhileBlocked.Should().BeFalse();

        [Test]
        public void It_cancels_nothing() => (_commandCancelledWhileBlocked, _result).Should().Be((false, 7));

        [Test]
        public void It_probes_once_the_command_has_completed() => _probesAfterRelease.Should().Be(1);
    }

    [TestFixture]
    public class Given_two_commands_are_issued_concurrently
    {
        private FakeDmsTargetLockHolder _holder = null!;
        private readonly Lock _gate = new();
        private List<string> _started = [];
        private Dictionary<string, TaskCompletionSource> _signals = new();
        private Dictionary<string, TaskCompletionSource> _releases = new();
        private int _active;
        private int _maxActive;
        private int _startedAfterIssue;
        private string[] _results = [];

        [SetUp]
        public async Task Setup()
        {
            _holder = Holder(Clock());
            _started = [];
            _signals = new Dictionary<string, TaskCompletionSource>();
            _releases = new Dictionary<string, TaskCompletionSource>();
            _active = 0;
            _maxActive = 0;
            foreach (string id in new[] { "A", "B" })
            {
                _signals[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _releases[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            Task<string> first = _holder.RunAsync(Command("A"), _commandDeadline, CancellationToken.None);
            Task<string> second = _holder.RunAsync(Command("B"), _commandDeadline, CancellationToken.None);

            string runningFirst;
            lock (_gate)
            {
                _startedAfterIssue = _started.Count;
                runningFirst = _started[0];
            }

            string runningSecond = runningFirst == "A" ? "B" : "A";
            _releases[runningFirst].SetResult();
            await _signals[runningSecond].Task;
            _releases[runningSecond].SetResult();
            _results = await Task.WhenAll(first, second);
        }

        [TearDown]
        public async Task TearDown() => await _holder.DisposeAsync();

        private Func<CancellationToken, Task<string>> Command(string id) =>
            async _ =>
            {
                int active = Interlocked.Increment(ref _active);
                lock (_gate)
                {
                    _maxActive = Math.Max(_maxActive, active);
                    _started.Add(id);
                }

                _signals[id].SetResult();
                try
                {
                    await _releases[id].Task;
                    return id;
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            };

        [Test]
        public void It_starts_exactly_one_of_them_first() => _startedAfterIssue.Should().Be(1);

        [Test]
        public void It_never_runs_them_at_the_same_time() => _maxActive.Should().Be(1);

        [Test]
        public void It_completes_both() => _results.Should().BeEquivalentTo("A", "B");
    }

    [TestFixture]
    public class Given_disposal_is_requested_while_a_command_is_active
    {
        private FakeDmsTargetLockHolder _holder = null!;
        private bool _disposedBeforeTheCommandCompleted;
        private int _releasesAfterFirstDispose;
        private int _releasesAfterSecondDispose;
        private Func<Task> _commandAfterDispose = () => Task.CompletedTask;

        [SetUp]
        public async Task Setup()
        {
            _holder = Holder(Clock());
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> command = _holder.RunAsync(
                async _ =>
                {
                    await release.Task;
                    return 1;
                },
                _commandDeadline,
                CancellationToken.None
            );

            Task dispose = _holder.DisposeAsync().AsTask();
            _disposedBeforeTheCommandCompleted = dispose.IsCompleted;

            release.SetResult();
            await command;
            await dispose;
            _releasesAfterFirstDispose = _holder.ReleaseCount;

            await _holder.DisposeAsync();
            _releasesAfterSecondDispose = _holder.ReleaseCount;

            _commandAfterDispose = () =>
                _holder.RunAsync(_ => Task.FromResult(0), _commandDeadline, CancellationToken.None);
        }

        [TearDown]
        public async Task TearDown() => await _holder.DisposeAsync();

        [Test]
        public void It_waits_for_the_active_command() =>
            _disposedBeforeTheCommandCompleted.Should().BeFalse();

        [Test]
        public void It_releases_once_after_the_command() => _releasesAfterFirstDispose.Should().Be(1);

        [Test]
        public void It_is_safe_to_dispose_again() => _releasesAfterSecondDispose.Should().Be(1);

        [Test]
        public async Task It_refuses_commands_after_disposal() =>
            await _commandAfterDispose.Should().ThrowAsync<ObjectDisposedException>();
    }

    [TestFixture]
    public class Given_loss_is_declared_while_a_command_is_in_flight
    {
        private FakeDmsTargetLockHolder _holder = null!;
        private Func<Task> _command = () => Task.CompletedTask;
        private bool _commandTokenCancelled;
        private bool _laterCommandRan;
        private Func<Task> _laterCommand = () => Task.CompletedTask;
        private Func<Task> _dispose = () => Task.CompletedTask;

        [SetUp]
        public async Task Setup()
        {
            _holder = Holder(Clock());
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken commandToken = CancellationToken.None;
            Task<int> command = _holder.RunAsync(
                async token =>
                {
                    commandToken = token;
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 1;
                },
                _commandDeadline,
                CancellationToken.None
            );
            await started.Task;

            _holder.Lose();
            _command = () => command;
            try
            {
                await command;
            }
            catch (OperationCanceledException)
            {
                // Expected; asserted below.
            }

            _commandTokenCancelled = commandToken.IsCancellationRequested;
            _laterCommand = () =>
                _holder.RunAsync(
                    _ =>
                    {
                        _laterCommandRan = true;
                        return Task.FromResult(0);
                    },
                    _commandDeadline,
                    CancellationToken.None
                );
            _dispose = () => _holder.DisposeAsync().AsTask();
        }

        [TearDown]
        public async Task TearDown() => await _holder.DisposeAsync();

        [Test]
        public void It_cancels_the_lost_token() =>
            _holder.LostToken.IsCancellationRequested.Should().BeTrue();

        [Test]
        public async Task It_cancels_the_in_flight_command()
        {
            await _command.Should().ThrowAsync<OperationCanceledException>();
            _commandTokenCancelled.Should().BeTrue();
        }

        [Test]
        public async Task It_refuses_later_commands_without_running_them()
        {
            await _laterCommand.Should().ThrowAsync<OperationCanceledException>();
            _laterCommandRan.Should().BeFalse();
        }

        [Test]
        public async Task It_disposes_safely_after_loss()
        {
            await _dispose.Should().NotThrowAsync();
            _holder.ReleaseCount.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_commands_fail
    {
        private FakeDmsTargetLockHolder _connectionFailure = null!;
        private FakeDmsTargetLockHolder _otherFailure = null!;
        private FakeDmsTargetLockHolder _brokenAfterCommand = null!;
        private Func<Task> _connectionFailureCommand = () => Task.CompletedTask;
        private Func<Task> _otherFailureCommand = () => Task.CompletedTask;

        [SetUp]
        public async Task Setup()
        {
            FakeTimeProvider clock = Clock();
            _connectionFailure = Holder(clock);
            _otherFailure = Holder(clock);
            _brokenAfterCommand = Holder(clock);

            _connectionFailureCommand = () =>
                _connectionFailure.RunAsync<int>(
                    _ => throw new TestConnectionException(),
                    _commandDeadline,
                    CancellationToken.None
                );
            _otherFailureCommand = () =>
                _otherFailure.RunAsync<int>(
                    _ => throw new InvalidOperationException("not a connection error"),
                    _commandDeadline,
                    CancellationToken.None
                );
            await _connectionFailureCommand.Should().ThrowAsync<TestConnectionException>();
            await _otherFailureCommand.Should().ThrowAsync<InvalidOperationException>();

            await _brokenAfterCommand.RunAsync(
                _ =>
                {
                    _brokenAfterCommand.Broken = true;
                    return Task.FromResult(0);
                },
                _commandDeadline,
                CancellationToken.None
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            await _connectionFailure.DisposeAsync();
            await _otherFailure.DisposeAsync();
            await _brokenAfterCommand.DisposeAsync();
        }

        [Test]
        public void It_declares_loss_on_a_connection_class_failure() =>
            _connectionFailure.IsLost.Should().BeTrue();

        [Test]
        public void It_declares_no_loss_on_another_failure() => _otherFailure.IsLost.Should().BeFalse();

        [Test]
        public void It_declares_loss_when_the_connection_reports_broken_after_a_command() =>
            _brokenAfterCommand.IsLost.Should().BeTrue();
    }

    [TestFixture]
    public class Given_the_heartbeat_probe_runs
    {
        private FakeDmsTargetLockHolder _connectionFailure = null!;
        private FakeDmsTargetLockHolder _lockNotHeld = null!;
        private FakeDmsTargetLockHolder _otherFailure = null!;
        private FakeDmsTargetLockHolder _healthy = null!;

        [SetUp]
        public void Setup()
        {
            FakeTimeProvider clock = Clock();
            _connectionFailure = Holder(clock);
            _connectionFailure.Probe = _ => throw new TestConnectionException();
            _lockNotHeld = Holder(clock);
            _lockNotHeld.Probe = _ => Task.FromResult(false);
            _otherFailure = Holder(clock);
            _otherFailure.Probe = _ => throw new InvalidOperationException("not a connection error");
            _healthy = Holder(clock);

            foreach (
                FakeDmsTargetLockHolder holder in new[]
                {
                    _connectionFailure,
                    _lockNotHeld,
                    _otherFailure,
                    _healthy,
                }
            )
            {
                holder.StartHeartbeat();
            }

            clock.Advance(_interval);
            clock.Advance(_interval);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _connectionFailure.DisposeAsync();
            await _lockNotHeld.DisposeAsync();
            await _otherFailure.DisposeAsync();
            await _healthy.DisposeAsync();
        }

        [Test]
        public void It_declares_loss_on_a_connection_class_probe_failure() =>
            _connectionFailure.IsLost.Should().BeTrue();

        [Test]
        public void It_declares_loss_when_the_lock_is_no_longer_held() =>
            _lockNotHeld.IsLost.Should().BeTrue();

        [Test]
        public void It_stops_probing_after_loss() =>
            _connectionFailure.Heartbeat.CompletedProbes.Should().Be(1);

        [Test]
        public void It_declares_no_loss_on_another_probe_failure_and_keeps_probing() =>
            (_otherFailure.IsLost, _otherFailure.Heartbeat.CompletedProbes).Should().Be((false, 2));

        [Test]
        public void It_probes_a_healthy_holder_every_interval() =>
            (_healthy.IsLost, _healthy.Heartbeat.CompletedProbes).Should().Be((false, 2));
    }

    [TestFixture]
    public class Given_a_probe_exceeds_its_deadline
    {
        private FakeDmsTargetLockHolder _holder = null!;
        private int _commandResult;

        [SetUp]
        public async Task Setup()
        {
            FakeTimeProvider clock = Clock();
            _holder = Holder(clock);
            _holder.Probe = BlockedProbe();
            _holder.StartHeartbeat();

            clock.Advance(_interval);
            clock.Advance(_probeDeadline);

            // A command waits for the gate, so its completion proves the cancelled probe released it.
            _commandResult = await _holder.RunAsync(
                _ => Task.FromResult(9),
                _commandDeadline,
                CancellationToken.None
            );
        }

        [TearDown]
        public async Task TearDown() => await _holder.DisposeAsync();

        private static Func<CancellationToken, Task<bool>> BlockedProbe() =>
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            };

        [Test]
        public void It_declares_no_loss() => _holder.IsLost.Should().BeFalse();

        [Test]
        public void It_counts_the_probe_as_complete() => _holder.Heartbeat.CompletedProbes.Should().Be(1);

        [Test]
        public void It_leaves_the_holder_usable() => _commandResult.Should().Be(9);
    }

    [TestFixture]
    public class Given_a_command_exceeds_its_deadline
    {
        private FakeDmsTargetLockHolder _holder = null!;
        private Func<Task> _command = () => Task.CompletedTask;
        private int _laterResult;

        [SetUp]
        public async Task Setup()
        {
            FakeTimeProvider clock = Clock();
            _holder = Holder(clock);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> command = _holder.RunAsync(
                async token =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 1;
                },
                _commandDeadline,
                CancellationToken.None
            );
            await started.Task;

            clock.Advance(_commandDeadline);
            _command = () => command;
            try
            {
                await command;
            }
            catch (OperationCanceledException)
            {
                // Expected; asserted below.
            }

            _laterResult = await _holder.RunAsync(
                _ => Task.FromResult(3),
                _commandDeadline,
                CancellationToken.None
            );
        }

        [TearDown]
        public async Task TearDown() => await _holder.DisposeAsync();

        [Test]
        public async Task It_cancels_that_command() =>
            await _command.Should().ThrowAsync<OperationCanceledException>();

        [Test]
        public void It_declares_no_loss() => _holder.IsLost.Should().BeFalse();

        [Test]
        public void It_leaves_the_holder_usable() => _laterResult.Should().Be(3);
    }

    [TestFixture]
    public class Given_the_release_fails_during_disposal
    {
        private FakeDmsTargetLockHolder _holder = null!;
        private Func<Task> _dispose = () => Task.CompletedTask;

        [SetUp]
        public void Setup()
        {
            _holder = Holder(Clock());
            _holder.ReleaseThrows = true;
            _dispose = () => _holder.DisposeAsync().AsTask();
        }

        [TearDown]
        public async Task TearDown() => await _holder.DisposeAsync();

        [Test]
        public async Task It_never_throws()
        {
            await _dispose.Should().NotThrowAsync();
            _holder.ReleaseCount.Should().Be(1);
        }
    }

    /// <summary>
    /// The dispatch boundary: a command refused before it is invoked, for the caller's cancellation or for loss
    /// while the heartbeat probe holds the gate, is reported as not dispatched and never runs; a command that
    /// was invoked and is then cancelled is a plain cancellation, because it may have reached the connection.
    /// </summary>
    [TestFixture]
    public class Given_a_command_is_stopped_before_or_after_it_is_invoked
    {
        private readonly List<FakeDmsTargetLockHolder> _holders = [];
        private Exception? _cancelledAtTheGate;
        private Exception? _lostAtTheGate;
        private Exception? _cancelledWhileRunning;
        private int _refusedCommandsInvoked;

        [SetUp]
        public async Task Setup()
        {
            TaskCompletionSource<bool> cancelledProbe = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            FakeDmsTargetLockHolder cancelled = GateHeldByProbe(cancelledProbe.Task);
            using CancellationTokenSource caller = new();
            Task<int> cancelledCommand = cancelled.RunAsync(Refused, _commandDeadline, caller.Token);
            await caller.CancelAsync();
            _cancelledAtTheGate = await Record.ExceptionAsync(() => cancelledCommand);
            cancelledProbe.SetResult(true);

            // The probe finds the lock no longer held while the command waits behind it.
            TaskCompletionSource<bool> losingProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDmsTargetLockHolder lost = GateHeldByProbe(losingProbe.Task);
            Task<int> lostCommand = lost.RunAsync(Refused, _commandDeadline, CancellationToken.None);
            losingProbe.SetResult(false);
            _lostAtTheGate = await Record.ExceptionAsync(() => lostCommand);

            FakeDmsTargetLockHolder running = Holder(Clock());
            _holders.Add(running);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource runningCaller = new();
            Task<int> runningCommand = running.RunAsync(
                async token =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 1;
                },
                _commandDeadline,
                runningCaller.Token
            );
            await started.Task;
            await runningCaller.CancelAsync();
            _cancelledWhileRunning = await Record.ExceptionAsync(() => runningCommand);
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (FakeDmsTargetLockHolder holder in _holders)
            {
                await holder.DisposeAsync();
            }
        }

        private Task<int> Refused(CancellationToken _)
        {
            Interlocked.Increment(ref _refusedCommandsInvoked);
            return Task.FromResult(1);
        }

        private FakeDmsTargetLockHolder GateHeldByProbe(Task<bool> probe)
        {
            FakeTimeProvider clock = Clock();
            FakeDmsTargetLockHolder holder = Holder(clock);
            _holders.Add(holder);
            holder.Probe = _ => probe;
            holder.StartHeartbeat();
            clock.Advance(_interval);
            return holder;
        }

        [Test]
        public void It_reports_a_cancellation_at_the_gate_as_not_dispatched() =>
            _cancelledAtTheGate.Should().BeOfType<DmsCommandNotDispatchedException>();

        [Test]
        public void It_reports_loss_at_the_gate_as_not_dispatched()
        {
            _lostAtTheGate.Should().BeOfType<DmsCommandNotDispatchedException>();
            _holders[1].IsLost.Should().BeTrue();
        }

        [Test]
        public void It_never_invokes_a_refused_command() => _refusedCommandsInvoked.Should().Be(0);

        [Test]
        public void It_keeps_an_invoked_command_cancellation_in_doubt() =>
            _cancelledWhileRunning
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .And.NotBeOfType<DmsCommandNotDispatchedException>();
    }
}
