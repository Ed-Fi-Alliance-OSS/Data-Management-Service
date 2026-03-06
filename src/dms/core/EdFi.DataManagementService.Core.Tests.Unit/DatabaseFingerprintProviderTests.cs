// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

[TestFixture]
[Parallelizable]
public class DatabaseFingerprintProviderTests
{
    private static DatabaseFingerprint CreateFingerprint(string hash = "abc123") =>
        new("1.0", hash, 42, new byte[32]);

    private static IOptions<AppSettings> DefaultAppSettings(int negativeCacheTtlSeconds = 30) =>
        Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                DatabaseFingerprintNegativeCacheTtlSeconds = negativeCacheTtlSeconds,
            }
        );

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

            var provider = new DatabaseFingerprintProvider(
                _reader,
                DefaultAppSettings(),
                TimeProvider.System
            );
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

            var provider = new DatabaseFingerprintProvider(
                _reader,
                DefaultAppSettings(),
                TimeProvider.System
            );
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

            var provider = new DatabaseFingerprintProvider(
                _reader,
                DefaultAppSettings(),
                TimeProvider.System
            );
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

            var provider = new DatabaseFingerprintProvider(
                _reader,
                DefaultAppSettings(),
                TimeProvider.System
            );

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
    public class Given_First_Call_Returns_Null_Then_Database_Provisioned_After_TTL
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

            var fakeTime = new FakeTimeProvider();
            var provider = new DatabaseFingerprintProvider(_reader, DefaultAppSettings(), fakeTime);

            _result1 = await provider.GetFingerprintAsync("conn1");
            fakeTime.Advance(TimeSpan.FromSeconds(31)); // Expire the negative cache
            _result2 = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_returns_null_on_first_call()
        {
            _result1.Should().BeNull();
        }

        [Test]
        public void It_retries_and_returns_fingerprint_on_second_call()
        {
            _result2.Should().NotBeNull();
            _result2!.EffectiveSchemaHash.Should().Be("provisioned");
        }

        [Test]
        public void It_invokes_reader_twice()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedTwiceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_First_Call_Throws_Then_Database_Recovers : DatabaseFingerprintProviderTests
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
                        throw new InvalidOperationException("connection refused");
                    }
                    return Task.FromResult<DatabaseFingerprint?>(CreateFingerprint("recovered"));
                });

            var provider = new DatabaseFingerprintProvider(
                _reader,
                DefaultAppSettings(),
                TimeProvider.System
            );

            try
            {
                await provider.GetFingerprintAsync("conn1");
            }
            catch (InvalidOperationException ex)
            {
                _firstException = ex;
            }

            _secondResult = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_throws_on_first_call()
        {
            _firstException.Should().NotBeNull();
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
    public class Given_Null_Result_And_Second_Call_Within_TTL : DatabaseFingerprintProviderTests
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

            var fakeTime = new FakeTimeProvider();
            var provider = new DatabaseFingerprintProvider(_reader, DefaultAppSettings(), fakeTime);

            _result1 = await provider.GetFingerprintAsync("conn1");
            fakeTime.Advance(TimeSpan.FromSeconds(10)); // Still within 30s TTL
            _result2 = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_returns_null_on_first_call()
        {
            _result1.Should().BeNull();
        }

        [Test]
        public void It_returns_cached_null_on_second_call()
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
    public class Given_Null_Result_And_Second_Call_After_TTL_Expires : DatabaseFingerprintProviderTests
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

            var fakeTime = new FakeTimeProvider();
            var provider = new DatabaseFingerprintProvider(_reader, DefaultAppSettings(), fakeTime);

            _result1 = await provider.GetFingerprintAsync("conn1");
            fakeTime.Advance(TimeSpan.FromSeconds(31)); // Past 30s TTL
            _result2 = await provider.GetFingerprintAsync("conn1");
        }

        [Test]
        public void It_returns_null_on_first_call()
        {
            _result1.Should().BeNull();
        }

        [Test]
        public void It_returns_fingerprint_after_ttl_expires()
        {
            _result2.Should().NotBeNull();
            _result2!.EffectiveSchemaHash.Should().Be("provisioned");
        }

        [Test]
        public void It_invokes_reader_twice()
        {
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1")).MustHaveHappenedTwiceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_Calls_During_Negative_Cache : DatabaseFingerprintProviderTests
    {
        private DatabaseFingerprint?[] _results = [];
        private IDatabaseFingerprintReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => _reader.ReadFingerprintAsync("conn1"))
                .Returns(Task.FromResult<DatabaseFingerprint?>(null));

            var fakeTime = new FakeTimeProvider();
            var provider = new DatabaseFingerprintProvider(_reader, DefaultAppSettings(), fakeTime);

            // Prime the negative cache
            await provider.GetFingerprintAsync("conn1");

            fakeTime.Advance(TimeSpan.FromSeconds(5)); // Still within TTL

            // 10 concurrent calls within TTL
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
}
