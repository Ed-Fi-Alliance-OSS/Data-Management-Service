// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

[TestFixture]
[Parallelizable]
public class DatabaseFingerprintEqualityTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_Two_Fingerprints_With_Identical_Values : DatabaseFingerprintEqualityTests
    {
        private DatabaseFingerprint _fingerprint1 = null!;
        private DatabaseFingerprint _fingerprint2 = null!;

        [SetUp]
        public void Setup()
        {
            var hash = new byte[32];
            hash[0] = 0xAB;
            hash[31] = 0xCD;

            _fingerprint1 = new DatabaseFingerprint("1.0", "abc123", 42, hash.ToImmutableArray());
            _fingerprint2 = new DatabaseFingerprint("1.0", "abc123", 42, hash.ToImmutableArray());
        }

        [Test]
        public void It_should_be_equal()
        {
            _fingerprint1.Should().Be(_fingerprint2);
        }

        [Test]
        public void It_should_have_same_hash_code()
        {
            _fingerprint1.GetHashCode().Should().Be(_fingerprint2.GetHashCode());
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Two_Fingerprints_With_Different_ResourceKeySeedHash : DatabaseFingerprintEqualityTests
    {
        private DatabaseFingerprint _fingerprint1 = null!;
        private DatabaseFingerprint _fingerprint2 = null!;

        [SetUp]
        public void Setup()
        {
            var hash1 = new byte[32];
            hash1[0] = 0xAB;
            var hash2 = new byte[32];
            hash2[0] = 0xCD;

            _fingerprint1 = new DatabaseFingerprint("1.0", "abc123", 42, hash1.ToImmutableArray());
            _fingerprint2 = new DatabaseFingerprint("1.0", "abc123", 42, hash2.ToImmutableArray());
        }

        [Test]
        public void It_should_not_be_equal()
        {
            _fingerprint1.Should().NotBe(_fingerprint2);
        }
    }
}
