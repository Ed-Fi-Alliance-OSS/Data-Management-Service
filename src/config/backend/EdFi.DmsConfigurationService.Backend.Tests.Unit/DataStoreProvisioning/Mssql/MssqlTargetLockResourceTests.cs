// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// The application lock resource of D-12 on SQL Server: the fixed prefix plus the target name folded to lower
/// case, because <c>sp_getapplock</c> compares resources byte for byte; and the lock settings the engine holds,
/// range-checked at construction.
/// </summary>
public class MssqlTargetLockResourceTests
{
    [TestFixture]
    public class Given_target_names
    {
        private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
            "edfi_configurationservice",
            []
        );

        private string _mixedCase = string.Empty;
        private string _longest = string.Empty;

        [SetUp]
        public void Setup()
        {
            _mixedCase = ResourceFor("Edfi_DS_001");
            _longest = ResourceFor("E" + new string('x', DmsTargetDatabaseName.MaxLength - 1));
        }

        [Test]
        public void It_is_the_prefix_plus_the_lowercased_name() =>
            _mixedCase.Should().Be("dms-provision-target:v1:edfi_ds_001");

        [Test]
        public void It_fits_sp_getapplock_for_the_longest_name() =>
            _longest
                .Length.Should()
                .Be(24 + DmsTargetDatabaseName.MaxLength)
                .And.BeLessThanOrEqualTo(MssqlTargetLockSession.MaximumResourceLength);

        private static string ResourceFor(string candidate) =>
            DmsTargetDatabaseName.TryCreate(candidate, _protectedNames, out DmsTargetDatabaseName name)
            == DmsTargetNameValidation.Valid
                ? MssqlTargetLockSession.LockResourceFor(name)
                : throw new InvalidOperationException(
                    "The candidate does not satisfy the target name rules."
                );
    }

    [TestFixture]
    public class Given_lock_settings
    {
        [Test]
        public void It_keeps_values_in_range() =>
            MssqlEngineSettingsSample
                .With(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(20),
                    TimeSpan.FromMinutes(7)
                )
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        LockAcquireTimeout = TimeSpan.FromSeconds(5),
                        LockHeartbeatInterval = TimeSpan.FromSeconds(10),
                        SessionTerminationTimeout = TimeSpan.FromSeconds(20),
                        RestoreTimeout = TimeSpan.FromMinutes(7),
                    }
                );

        [TestCase(0, 5, 30, 300)]
        [TestCase(3601, 5, 30, 300)]
        [TestCase(5, 0, 30, 300)]
        [TestCase(5, 3601, 30, 300)]
        [TestCase(5, 5, 0, 300)]
        [TestCase(5, 5, 3601, 300)]
        [TestCase(5, 5, 30, 0)]
        [TestCase(5, 5, 30, 21601)]
        public void It_refuses_values_out_of_range(
            int acquireSeconds,
            int heartbeatSeconds,
            int terminationSeconds,
            int restoreSeconds
        ) =>
            FluentActions
                .Invoking(() =>
                    MssqlEngineSettingsSample.With(
                        TimeSpan.FromSeconds(acquireSeconds),
                        TimeSpan.FromSeconds(heartbeatSeconds),
                        TimeSpan.FromSeconds(terminationSeconds),
                        TimeSpan.FromSeconds(restoreSeconds)
                    )
                )
                .Should()
                .Throw<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_missing_staging_settings() =>
            FluentActions
                .Invoking(() =>
                    new MssqlDmsEngineSettings(
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromMinutes(5),
                        null!
                    )
                )
                .Should()
                .Throw<ArgumentNullException>();
    }
}
