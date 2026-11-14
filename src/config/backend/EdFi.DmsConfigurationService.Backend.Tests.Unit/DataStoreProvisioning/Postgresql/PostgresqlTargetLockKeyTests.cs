// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The advisory lock key of D-12: the first eight bytes of the SHA-256 of the lock resource, read big-endian,
/// with the target name folded to lower case. The vector and its <c>pg_locks</c> split were computed outside
/// the implementation (Python <c>hashlib</c> and <c>struct</c>), so the operator procedure (7.6) and the
/// holder agree on the resource. The readiness probe resource (7.3 item 7) has its own vector and is
/// unreachable from any target name.
/// </summary>
public class PostgresqlTargetLockKeyTests
{
    [TestFixture]
    public class Given_target_names
    {
        private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
            "edfi_configurationservice",
            []
        );

        private long _lower;
        private long _mixedCase;
        private long _other;

        [SetUp]
        public void Setup()
        {
            _lower = KeyFor("edfi_ds_001");
            _mixedCase = KeyFor("Edfi_DS_001");
            _other = KeyFor("edfi_ds_002");
        }

        [Test]
        public void It_matches_the_independently_computed_vector() =>
            _lower.Should().Be(-3035865596365888889L);

        [Test]
        public void It_folds_the_name_to_lower_case() => _mixedCase.Should().Be(_lower);

        [Test]
        public void It_differs_between_names() => _other.Should().NotBe(_lower);

        [Test]
        public void It_splits_into_the_pg_locks_columns_the_operator_procedure_names() =>
            ((uint)((ulong)_lower >> 32), (uint)(ulong)_lower).Should().Be((3588124755u, 650650247u));

        private static long KeyFor(string candidate) =>
            DmsTargetDatabaseName.TryCreate(candidate, _protectedNames, out DmsTargetDatabaseName name)
            == DmsTargetNameValidation.Valid
                ? PostgresqlTargetLockSession.LockKeyFor(name)
                : throw new InvalidOperationException(
                    "The candidate does not satisfy the target name rules."
                );
    }

    [TestFixture]
    public class Given_the_readiness_probe_resource
    {
        [Test]
        public void It_matches_the_independently_computed_vector_of_the_probe_resource() =>
            PostgresqlTargetLockSession.ProbeLockKey.Should().Be(5742783533976127761L);

        [Test]
        public void It_is_a_resource_no_target_name_can_produce() =>
            DmsTargetDatabaseName
                .TryCreate(
                    PostgresqlTargetLockSession.ProbeResource,
                    ProtectedDatabaseNames.Create("edfi_configurationservice", []),
                    out _
                )
                .Should()
                .NotBe(DmsTargetNameValidation.Valid);
    }
}
