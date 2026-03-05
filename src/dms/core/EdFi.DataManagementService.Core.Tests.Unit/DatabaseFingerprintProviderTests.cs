// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

[TestFixture]
[Parallelizable]
public class DatabaseFingerprintProviderTests
{
    private static DatabaseFingerprint CreateFingerprint(string hash = "abc123") =>
        new("1.0", hash, 42, new byte[32]);

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
}
