// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

[TestFixture]
[Parallelizable]
public class DatabaseFingerprintProviderTests
{
    private static DatabaseFingerprint CreateFingerprint(string hash = "abc123") =>
        new("1.0", hash, 42, new byte[32].ToImmutableArray());

    private static async Task<Exception?> CatchExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_First_Call_For_A_Connection_String : DatabaseFingerprintProviderTests
    {
        private DatabaseFingerprint? _result;
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).Returns(CreateFingerprint());

            var provider = new DatabaseFingerprintProvider(_reader);
            _result = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_invokes_reader_exactly_once()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_returns_the_fingerprint()
        {
            _result.Should().NotBeNull();
            _result!.EffectiveSchemaHash.Should().Be("abc123");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Second_Call_For_Same_Connection_String : DatabaseFingerprintProviderTests
    {
        private DatabaseFingerprint? _result1;
        private DatabaseFingerprint? _result2;
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).Returns(CreateFingerprint());

            var provider = new DatabaseFingerprintProvider(_reader);
            _result1 = await provider.GetFingerprintAsync("conn1");
            _result2 = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_invokes_reader_exactly_once()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_returns_the_same_fingerprint()
        {
            _result2.Should().BeSameAs(_result1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Calls_For_Different_Connection_Strings : DatabaseFingerprintProviderTests
    {
        private DatabaseFingerprint? _result1;
        private DatabaseFingerprint? _result2;
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).Returns(CreateFingerprint("hash1"));
            A.CallTo(() => _reader.ReadFingerprintAsync("conn2")).Returns(CreateFingerprint("hash2"));

            var provider = new DatabaseFingerprintProvider(_reader);
            _result1 = await provider.GetFingerprintAsync("conn1");
            _result2 = await provider.GetFingerprintAsync("conn2");
        }

        [Test]
        public void It_invokes_reader_for_each_connection_string()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedOnceExactly();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn2")).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_returns_distinct_fingerprints()
        {
            _result1!.EffectiveSchemaHash.Should().Be("hash1");
            _result2!.EffectiveSchemaHash.Should().Be("hash2");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_First_Calls_For_Same_Connection_String : DatabaseFingerprintProviderTests
    {
        private DatabaseFingerprint?[] _results = [];
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IDatabaseFingerprintReader>();
            var tcs = new TaskCompletionSource<DatabaseFingerprint?>();

            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).ReturnsLazily(() => tcs.Task);

            var provider = new DatabaseFingerprintProvider(_reader);

            var tasks = Enumerable.Range(0, 10).Select(_ => provider.GetFingerprintAsync("conn1")).ToArray();

            tcs.SetResult(CreateFingerprint());
            _results = await Task.WhenAll(tasks);
        }

        [Test]
        public void It_invokes_reader_exactly_once()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_returns_same_result_for_all_callers()
        {
            _results.Should().AllSatisfy(r => r.Should().NotBeNull());
            _results.Select(r => r!.EffectiveSchemaHash).Distinct().Should().ContainSingle();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_First_Call_Returns_Null_Then_Database_Is_Provisioned_Later
        : DatabaseFingerprintProviderTests
    {
        private DatabaseFingerprint? _result1;
        private DatabaseFingerprint? _result2;
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            int callCount = 0;
            _reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1"))
                .ReturnsLazily(() =>
                {
                    callCount++;
                    return callCount == 1
                        ? Task.FromResult<DatabaseFingerprint?>(null)
                        : Task.FromResult<DatabaseFingerprint?>(CreateFingerprint("provisioned"));
                });

            var provider = new DatabaseFingerprintProvider(_reader);

            _result1 = await provider.GetFingerprintAsync("conn1");
            _result2 = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_returns_null_on_first_call()
        {
            _result1.Should().BeNull();
        }

        [Test]
        public void It_returns_the_cached_null_on_second_call()
        {
            _result2.Should().BeNull();
        }

        [Test]
        public void It_invokes_reader_once()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_First_Call_Throws_A_Transient_Reader_Failure_Then_Database_Recovers
        : DatabaseFingerprintProviderTests
    {
        private Exception? _firstException;
        private DatabaseFingerprint? _secondResult;
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            int callCount = 0;
            _reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1"))
                .ReturnsLazily(() =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        throw new TimeoutException("connection refused");
                    }
                    return Task.FromResult<DatabaseFingerprint?>(CreateFingerprint("recovered"));
                });

            var provider = new DatabaseFingerprintProvider(_reader);
            _firstException = await CatchExceptionAsync(provider.GetFingerprintAsync("conn1"));

            _secondResult = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_throws_on_first_call()
        {
            _firstException.Should().NotBeNull();
            _firstException.Should().BeOfType<TimeoutException>();
            _firstException!.Message.Should().Be("connection refused");
        }

        [Test]
        public void It_retries_and_returns_fingerprint_on_second_call()
        {
            _secondResult.Should().NotBeNull();
            _secondResult!.EffectiveSchemaHash.Should().Be("recovered");
        }

        [Test]
        public void It_invokes_reader_twice()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedTwiceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_First_Call_Throws_A_Permanent_Fingerprint_Validation_Error_And_The_Database_Is_Repaired
        : DatabaseFingerprintProviderTests
    {
        private Exception? _firstException;
        private Exception? _secondException;
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            int callCount = 0;
            _reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1"))
                .ReturnsLazily(() =>
                {
                    callCount++;
                    return callCount == 1
                        ? Task.FromException<DatabaseFingerprint?>(
                            new DatabaseFingerprintValidationException("malformed fingerprint")
                        )
                        : Task.FromResult<DatabaseFingerprint?>(CreateFingerprint("recovered"));
                });

            var provider = new DatabaseFingerprintProvider(_reader);

            _firstException = await CatchExceptionAsync(provider.GetFingerprintAsync("conn1"));
            _secondException = await CatchExceptionAsync(provider.GetFingerprintAsync("conn1"));
        }

        [Test]
        public void It_returns_the_validation_failure_on_each_call()
        {
            _firstException
                .Should()
                .BeOfType<DatabaseFingerprintValidationException>()
                .Which.Message.Should()
                .Be("malformed fingerprint");
            _secondException
                .Should()
                .BeOfType<DatabaseFingerprintValidationException>()
                .Which.Message.Should()
                .Be("malformed fingerprint");
        }

        [Test]
        public void It_does_not_retry_after_the_permanent_validation_failure()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_keeps_returning_the_cached_validation_failure_until_process_restart()
        {
            _secondException.Should().BeOfType<DatabaseFingerprintValidationException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Null_Result_And_Second_Call_For_Same_Connection_String
        : DatabaseFingerprintProviderTests
    {
        private DatabaseFingerprint? _result1;
        private DatabaseFingerprint? _result2;
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            int callCount = 0;
            _reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1"))
                .ReturnsLazily(() =>
                {
                    callCount++;
                    return callCount == 1
                        ? Task.FromResult<DatabaseFingerprint?>(null)
                        : Task.FromResult<DatabaseFingerprint?>(CreateFingerprint("provisioned"));
                });

            var provider = new DatabaseFingerprintProvider(_reader);

            _result1 = await provider.GetFingerprintAsync("conn1");
            _result2 = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_returns_null_on_first_call()
        {
            _result1.Should().BeNull();
        }

        [Test]
        public void It_returns_null_on_second_call()
        {
            _result2.Should().BeNull();
        }

        [Test]
        public void It_invokes_reader_exactly_once()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_Calls_After_A_Null_Result_Is_Cached : DatabaseFingerprintProviderTests
    {
        private DatabaseFingerprint?[] _results = [];
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1"))
                .Returns(Task.FromResult<DatabaseFingerprint?>(null));

            var provider = new DatabaseFingerprintProvider(_reader);

            await provider.GetFingerprintAsync("conn1");

            var tasks = Enumerable.Range(0, 10).Select(_ => provider.GetFingerprintAsync("conn1")).ToArray();

            _results = await Task.WhenAll(tasks);
        }

        [Test]
        public void It_invokes_reader_exactly_once()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_returns_null_for_all_callers()
        {
            _results.Should().AllSatisfy(r => r.Should().BeNull());
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Faulted_Read_With_A_Delayed_Concurrent_Awaiter_And_A_Successful_Retry
        : DatabaseFingerprintProviderTests
    {
        private DatabaseFingerprint? _cachedResult;
        private Exception? _delayedException;
        private Exception? _firstException;
        private IDatabaseFingerprintReader _reader = null!;
        private DatabaseFingerprint? _retryResult;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IDatabaseFingerprintReader>();

            var firstRead = new TaskCompletionSource<DatabaseFingerprint?>();
            var recoveredFingerprint = CreateFingerprint("recovered");
            int callCount = 0;

            A.CallTo(() => _reader.ReadFingerprintAsync("conn1"))
                .ReturnsLazily(() =>
                {
                    callCount++;

                    return callCount switch
                    {
                        1 => firstRead.Task,
                        2 => Task.FromResult<DatabaseFingerprint?>(recoveredFingerprint),
                        _ => Task.FromResult<DatabaseFingerprint?>(
                            CreateFingerprint($"unexpected-{callCount}")
                        ),
                    };
                });

            var provider = new DatabaseFingerprintProvider(_reader);

            var firstCallerTask = provider.GetFingerprintAsync("conn1");
            var delayedContext = new DeferredSynchronizationContext();
            var delayedCallerTask = RunOnSynchronizationContext(
                delayedContext,
                () => provider.GetFingerprintAsync("conn1")
            );

            firstRead.SetException(new TimeoutException("connection refused"));

            _firstException = await CatchExceptionAsync(firstCallerTask);
            _retryResult = await provider.GetFingerprintAsync("conn1");

            delayedContext.ExecutePostedCallbacks();
            _delayedException = await CatchExceptionAsync(delayedCallerTask);

            _cachedResult = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_retries_once_and_returns_the_recovered_fingerprint()
        {
            _retryResult.Should().NotBeNull();
            _retryResult!.EffectiveSchemaHash.Should().Be("recovered");
        }

        [Test]
        public void It_keeps_the_recovered_fingerprint_cached_after_the_delayed_faulted_awaiter_finishes()
        {
            _cachedResult.Should().BeSameAs(_retryResult);
        }

        [Test]
        public void It_returns_the_original_failure_to_each_faulted_awaiter()
        {
            _firstException
                .Should()
                .BeOfType<TimeoutException>()
                .Which.Message.Should()
                .Be("connection refused");
            _delayedException
                .Should()
                .BeOfType<TimeoutException>()
                .Which.Message.Should()
                .Be("connection refused");
        }

        [Test]
        public void It_invokes_reader_only_for_the_fault_and_the_successful_retry()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedTwiceExactly();
        }

        private static Task<T> RunOnSynchronizationContext<T>(
            DeferredSynchronizationContext synchronizationContext,
            Func<Task<T>> action
        )
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);

            try
            {
                return action();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }

        private sealed class DeferredSynchronizationContext : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

            public override void Post(SendOrPostCallback d, object? state)
            {
                lock (_callbacks)
                {
                    _callbacks.Enqueue((d, state));
                }
            }

            public void ExecutePostedCallbacks()
            {
                while (TryDequeue(out var callback))
                {
                    callback.Callback(callback.State);
                }
            }

            private bool TryDequeue(out (SendOrPostCallback Callback, object? State) callback)
            {
                lock (_callbacks)
                {
                    if (_callbacks.Count == 0)
                    {
                        callback = default;
                        return false;
                    }

                    callback = _callbacks.Dequeue();
                    return true;
                }
            }
        }
    }
}
