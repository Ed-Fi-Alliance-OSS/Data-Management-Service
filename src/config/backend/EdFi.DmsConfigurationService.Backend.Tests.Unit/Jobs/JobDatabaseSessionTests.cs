// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobDatabaseSessionTests
{
    /// <summary>An open connection that records when, and how often, it is disposed.</summary>
    public sealed class RecordingConnection : DbConnection
    {
        private int _disposeCount;
        private long _disposedAt;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public long DisposedAt => Interlocked.Read(ref _disposedAt);

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "recording";

        public override string DataSource => "recording";

        public override string ServerVersion => "0";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() { }

        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Exchange(ref _disposedAt, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _disposeCount);
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A session over a recording connection that collects every hand-over's cleanup.</summary>
    public sealed class SessionUnderTest
    {
        private readonly List<Task<JobDatabaseSessionCleanup>> _cleanups = [];

        public SessionUnderTest()
        {
            Session = new JobDatabaseSession(
                Connection,
                new JobDatabaseSessionHooks
                {
                    HandedOver = cleanup =>
                    {
                        lock (_cleanups)
                        {
                            _cleanups.Add(cleanup);
                        }
                    },
                }
            );
        }

        public RecordingConnection Connection { get; } = new();

        public JobDatabaseSession Session { get; }

        public int HandOverCount
        {
            get
            {
                lock (_cleanups)
                {
                    return _cleanups.Count;
                }
            }
        }

        public async Task<JobDatabaseSessionCleanup> CleanupAsync()
        {
            Task<JobDatabaseSessionCleanup> cleanup;
            lock (_cleanups)
            {
                cleanup = _cleanups.Single();
            }

            return await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// When one run's callback and operation ended. Each SetUp creates its own, so a callback still running from an
    /// earlier run of the same fixture cannot write into a later one.
    /// </summary>
    public sealed class EndTimes
    {
        private long _callback;
        private long _operation;

        public long Callback => Interlocked.Read(ref _callback);

        public long Operation => Interlocked.Read(ref _operation);

        public void CallbackEnded() => Interlocked.Exchange(ref _callback, Stopwatch.GetTimestamp());

        public void OperationEnded() => Interlocked.Exchange(ref _operation, Stopwatch.GetTimestamp());
    }

    private static async Task<Exception?> ThrownByAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [TestFixture]
    public class Given_an_operation_that_finishes_within_its_deadline
    {
        private SessionUnderTest _subject = null!;
        private int _result;

        [SetUp]
        public async Task Setup()
        {
            _subject = new SessionUnderTest();
            _result = await _subject.Session.RunAsync(
                "Probe",
                async _ =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
                    return 42;
                },
                JobDeadline.Start(TimeSpan.FromSeconds(5)),
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_the_result() => _result.Should().Be(42);

        [Test]
        public void It_keeps_the_session_with_the_caller()
        {
            _subject.Session.HandedOver.Should().BeFalse();
            _subject.HandOverCount.Should().Be(0);
            _subject.Connection.DisposeCount.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_an_operation_whose_cancellation_callback_blocks
    {
        private SessionUnderTest _subject = null!;
        private Exception? _thrown;
        private TimeSpan _elapsed;
        private JobDatabaseSessionCleanup _cleanup = null!;
        private long _callbackEndedWhenCleanupFinished;
        private long _operationEndedWhenCleanupFinished;

        [SetUp]
        public async Task Setup()
        {
            _subject = new SessionUnderTest();
            EndTimes ends = new();

            long started = Stopwatch.GetTimestamp();
            _thrown = await ThrownByAsync(() =>
                _subject.Session.RunAsync(
                    "Probe",
                    async token =>
                    {
                        // A provider whose cancellation callback blocks past the end of the operation itself: the
                        // callback runs from about 0.3 s to 1.5 s, and the operation ends at about 0.8 s.
                        token.Register(() =>
                        {
                            Thread.Sleep(TimeSpan.FromMilliseconds(1200));
                            ends.CallbackEnded();
                        });
                        await Task.Delay(TimeSpan.FromMilliseconds(800), CancellationToken.None);
                        ends.OperationEnded();
                        return 0;
                    },
                    JobDeadline.Start(TimeSpan.FromMilliseconds(300)),
                    CancellationToken.None
                )
            );
            _elapsed = Stopwatch.GetElapsedTime(started);
            _cleanup = await _subject.CleanupAsync();

            // Read when cleanup has finished: an end that has not happened yet reads as zero.
            _callbackEndedWhenCleanupFinished = ends.Callback;
            _operationEndedWhenCleanupFinished = ends.Operation;
        }

        [Test]
        public void It_returns_at_the_deadline_without_waiting_for_the_callback()
        {
            _thrown.Should().BeOfType<TimeoutException>();
            _elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(900), "the callback blocks for 1.2 s");
        }

        [Test]
        public void It_hands_the_session_over_once() => _subject.HandOverCount.Should().Be(1);

        [Test]
        public void It_releases_the_connection_only_after_both_the_callback_and_the_operation_end()
        {
            _callbackEndedWhenCleanupFinished.Should().BePositive("cleanup waited for the blocked callback");
            _operationEndedWhenCleanupFinished.Should().BePositive("cleanup waited for the operation");
            _subject.Connection.DisposeCount.Should().Be(1);
            _subject.Connection.DisposedAt.Should().BeGreaterThanOrEqualTo(_callbackEndedWhenCleanupFinished);
            _subject
                .Connection.DisposedAt.Should()
                .BeGreaterThanOrEqualTo(_operationEndedWhenCleanupFinished);
        }

        [Test]
        public void It_reports_that_neither_the_cancellation_nor_the_operation_failed() =>
            _cleanup.Should().Be(new JobDatabaseSessionCleanup(null, null));
    }

    [TestFixture]
    public class Given_an_operation_whose_cancellation_callback_throws
    {
        private readonly InvalidOperationException _callbackFailure = new("the cancellation callback failed");
        private SessionUnderTest _subject = null!;
        private Exception? _thrown;
        private TimeSpan _elapsed;
        private long _operationEndedWhenCleanupFinished;
        private JobDatabaseSessionCleanup _cleanup = null!;
        private Exception? _reuse;

        [SetUp]
        public async Task Setup()
        {
            _subject = new SessionUnderTest();
            EndTimes ends = new();

            long started = Stopwatch.GetTimestamp();
            _thrown = await ThrownByAsync(() =>
                _subject.Session.RunAsync(
                    "Probe",
                    async token =>
                    {
                        token.Register(() => throw _callbackFailure);
                        await Task.Delay(TimeSpan.FromMilliseconds(800), CancellationToken.None);
                        ends.OperationEnded();
                        return 0;
                    },
                    JobDeadline.Start(TimeSpan.FromMilliseconds(300)),
                    CancellationToken.None
                )
            );
            _elapsed = Stopwatch.GetElapsedTime(started);
            _cleanup = await _subject.CleanupAsync();
            _operationEndedWhenCleanupFinished = ends.Operation;

            // After the hand-over, the caller's own cleanup and any further use must not touch the session.
            await _subject.Session.EndAsync(
                JobDeadline.Start(TimeSpan.FromSeconds(5)),
                _ => Task.CompletedTask
            );
            _reuse = await ThrownByAsync(() =>
                _subject.Session.RunAsync(
                    "Probe",
                    _ => Task.FromResult(0),
                    JobDeadline.Start(TimeSpan.FromSeconds(5)),
                    CancellationToken.None
                )
            );
        }

        [Test]
        public void It_returns_the_timeout_at_the_deadline_rather_than_the_callback_failure()
        {
            _thrown.Should().BeOfType<TimeoutException>();
            _elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(700));
        }

        [Test]
        public void It_observes_the_callback_failure_in_cleanup() =>
            _cleanup
                .Cancellation.Should()
                .BeOfType<AggregateException>()
                .Which.InnerExceptions.Should()
                .ContainSingle()
                .Which.Should()
                .BeSameAs(_callbackFailure);

        [Test]
        public void It_releases_the_connection_once_after_the_operation_ends()
        {
            _operationEndedWhenCleanupFinished.Should().BePositive("cleanup waited for the operation");
            _subject.Connection.DisposeCount.Should().Be(1);
            _subject
                .Connection.DisposedAt.Should()
                .BeGreaterThanOrEqualTo(_operationEndedWhenCleanupFinished);
        }

        [Test]
        public void It_keeps_the_session_with_cleanup()
        {
            _subject.HandOverCount.Should().Be(1);
            _subject.Session.HandedOver.Should().BeTrue();
            _reuse.Should().BeOfType<InvalidOperationException>();
        }
    }

    [TestFixture]
    public class Given_an_operation_abandoned_by_the_callers_token
    {
        private SessionUnderTest _subject = null!;
        private Exception? _thrown;
        private TimeSpan _elapsed;
        private bool _operationSawCancellation;

        [SetUp]
        public async Task Setup()
        {
            _subject = new SessionUnderTest();
            _operationSawCancellation = false;
            using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(200));

            long started = Stopwatch.GetTimestamp();
            _thrown = await ThrownByAsync(() =>
                _subject.Session.RunAsync(
                    "Probe",
                    async token =>
                    {
                        token.Register(() => _operationSawCancellation = true);
                        await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None);
                        return 0;
                    },
                    JobDeadline.Start(TimeSpan.FromSeconds(5)),
                    caller.Token
                )
            );
            _elapsed = Stopwatch.GetElapsedTime(started);
            await _subject.CleanupAsync();
        }

        [Test]
        public void It_stops_waiting_when_the_caller_cancels()
        {
            _thrown.Should().BeAssignableTo<OperationCanceledException>();
            _elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        }

        [Test]
        public void It_asks_the_pending_operation_to_cancel_from_cleanup()
        {
            _subject.HandOverCount.Should().Be(1);
            _operationSawCancellation.Should().BeTrue();
            _subject.Connection.DisposeCount.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_a_deadline_cancellation_whose_callback_throws
    {
        private bool _cancelled;

        [SetUp]
        public async Task Setup()
        {
            using JobDeadlineCancellation cancellation = JobDeadline
                .Start(TimeSpan.FromMilliseconds(100))
                .CreateCancellation(CancellationToken.None);
            cancellation.Token.Register(() => throw new InvalidOperationException("the callback failed"));

            // A timer-driven cancellation that rethrew the callback's exception would end the test process here.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            _cancelled = cancellation.IsCancellationRequested;
        }

        [Test]
        public void It_cancels_the_token_at_the_deadline_without_an_unhandled_exception() =>
            _cancelled.Should().BeTrue();
    }
}
