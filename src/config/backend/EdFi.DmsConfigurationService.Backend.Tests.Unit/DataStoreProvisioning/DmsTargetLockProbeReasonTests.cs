// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// F-02 at startup: both readiness lock probes map an acquisition that held nothing to one reason. A definite refusal
/// of the login or the lock request is <c>LockUnavailable</c> with the engine's diagnostic, exactly as an
/// infrastructure failure is, because the <c>TargetServer</c> probe already reports an authentication refusal as
/// <c>StartupValidationFailed</c>; contention is <c>LockTimeout</c>; an acquired lock is no reason.
/// </summary>
public class DmsTargetLockProbeReasonTests
{
    private static readonly DmsProvisioningDiagnostic _postgresqlRefusal = new(
        "Npgsql.PostgresException",
        "28000",
        PostgresqlTargetLockSession.AcquireStep
    );

    private static readonly DmsProvisioningDiagnostic _mssqlRefusal = new(
        "Microsoft.Data.SqlClient.SqlException",
        "18470",
        MssqlTargetLockSession.AcquireStep
    );

    private static readonly DmsProvisioningDiagnostic _infrastructure = new(
        "System.TimeoutException",
        null,
        "OpenHolderSession"
    );

    private static FakeDmsTargetLockHolder Holder() =>
        new(
            new DmsServerSessionId(1, DateTimeOffset.UnixEpoch),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            TimeProvider.System
        );

    [TestFixture]
    public class Given_each_acquisition_result_of_the_postgresql_probe
    {
        private DmsReadinessReason? _accessDenied;
        private DmsReadinessReason? _failure;
        private DmsReadinessReason? _timeout;
        private DmsReadinessReason? _acquired;

        [SetUp]
        public async Task Setup()
        {
            await using FakeDmsTargetLockHolder holder = Holder();
            _accessDenied = PostgresqlDmsTargetEngine.ProbeLockReasonOf(
                new DmsTargetLockResult.AccessDenied(_postgresqlRefusal)
            );
            _failure = PostgresqlDmsTargetEngine.ProbeLockReasonOf(
                new DmsTargetLockResult.Failure(_infrastructure)
            );
            _timeout = PostgresqlDmsTargetEngine.ProbeLockReasonOf(new DmsTargetLockResult.Timeout());
            _acquired = PostgresqlDmsTargetEngine.ProbeLockReasonOf(new DmsTargetLockResult.Acquired(holder));
        }

        [Test]
        public void It_reports_a_refused_acquisition_as_lock_unavailable_with_its_diagnostic() =>
            _accessDenied
                .Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.LockUnavailable,
                        PostgresqlEnvironment.TargetLockProbe,
                        _postgresqlRefusal
                    )
                );

        [Test]
        public void It_reports_an_infrastructure_failure_as_lock_unavailable_with_its_diagnostic() =>
            _failure
                .Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.LockUnavailable,
                        PostgresqlEnvironment.TargetLockProbe,
                        _infrastructure
                    )
                );

        [Test]
        public void It_reports_contention_as_lock_timeout() =>
            _timeout!.Code.Should().Be(DmsProvisioningFailureCode.LockTimeout);

        [Test]
        public void It_reports_no_reason_for_an_acquired_lock() => _acquired.Should().BeNull();
    }

    [TestFixture]
    public class Given_each_acquisition_result_of_the_sql_server_probe
    {
        private DmsReadinessReason? _accessDenied;
        private DmsReadinessReason? _failure;
        private DmsReadinessReason? _timeout;
        private DmsReadinessReason? _acquired;

        [SetUp]
        public async Task Setup()
        {
            await using FakeDmsTargetLockHolder holder = Holder();
            _accessDenied = MssqlDmsTargetEngine.ProbeLockReasonOf(
                new DmsTargetLockResult.AccessDenied(_mssqlRefusal)
            );
            _failure = MssqlDmsTargetEngine.ProbeLockReasonOf(
                new DmsTargetLockResult.Failure(_infrastructure)
            );
            _timeout = MssqlDmsTargetEngine.ProbeLockReasonOf(new DmsTargetLockResult.Timeout());
            _acquired = MssqlDmsTargetEngine.ProbeLockReasonOf(new DmsTargetLockResult.Acquired(holder));
        }

        [Test]
        public void It_reports_a_refused_acquisition_as_lock_unavailable_with_its_diagnostic() =>
            _accessDenied
                .Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.LockUnavailable,
                        MssqlEnvironment.TargetLockProbe,
                        _mssqlRefusal
                    )
                );

        [Test]
        public void It_reports_an_infrastructure_failure_as_lock_unavailable_with_its_diagnostic() =>
            _failure
                .Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.LockUnavailable,
                        MssqlEnvironment.TargetLockProbe,
                        _infrastructure
                    )
                );

        [Test]
        public void It_reports_contention_as_lock_timeout() =>
            _timeout!.Code.Should().Be(DmsProvisioningFailureCode.LockTimeout);

        [Test]
        public void It_reports_no_reason_for_an_acquired_lock() => _acquired.Should().BeNull();
    }
}
