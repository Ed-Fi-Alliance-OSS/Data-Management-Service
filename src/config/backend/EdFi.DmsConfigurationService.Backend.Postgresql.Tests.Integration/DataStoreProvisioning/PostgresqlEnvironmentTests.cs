// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The PostgreSQL environmental validation against a live server (7.3 items 4, 5 and 7, Step 3.9): the lane
/// admin passes every server probe and, with a real <c>psql</c>, is <c>Ready</c>, leaving the probe lock
/// released; a role without <c>CREATEDB</c>, and a role without <c>pg_read_all_stats</c>, are each
/// <c>NotReady</c> naming the superuser requirement and exactly the missing privilege; a wrong <c>PsqlPath</c> and a <c>psql</c> below the
/// floor are <c>NotReady</c>; a probe lock held by another session is <c>LockTimeout</c> after the server's
/// own lock wait; and a server nothing listens on is <c>ServerUnavailable</c> with the lock probe skipped.
/// </summary>
[Category(Category)]
public class PostgresqlEnvironmentTests
{
    private const string SignalRole = "pg_signal_backend";
    private const string ReadStatsRole = "pg_read_all_stats";

    private static IReadOnlyList<DmsReadinessReason> ReasonsOf(DmsEngineEnvironment environment) =>
        environment is DmsEngineEnvironment.NotReady notReady ? notReady.Reasons : [];

    /// <summary>The probes other than <c>psql</c>, which depends on the lane host rather than the server.</summary>
    private static IEnumerable<DmsReadinessReason> ServerReasonsOf(DmsEngineEnvironment environment) =>
        ReasonsOf(environment).Where(reason => reason.Probe != PostgresqlEnvironment.PsqlProbe);

    private static PostgresqlDmsTargetEngine EngineWith(
        PostgresqlAdminConnection connection,
        string psqlPath,
        int minimumPsqlMajorVersion = PostgresqlDmsEngineSettings.DefaultMinimumPsqlMajorVersion,
        TimeSpan? lockAcquireTimeout = null
    ) =>
        new(
            connection,
            new PostgresqlDmsEngineSettings(
                lockAcquireTimeout ?? TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                psqlPath
            )
            {
                MinimumPsqlMajorVersion = minimumPsqlMajorVersion,
            },
            TimeProvider.System,
            NullLogger.Instance
        );

    private static string MissingPsql() => Path.Combine(AppContext.BaseDirectory, "no-such-dir", "psql");

    [TestFixture]
    public class Given_the_lane_admin
    {
        private DmsEngineEnvironment _environment = null!;
        private long _probeLocksAfter;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            await EnsureEnqueueOwnerRoleAsync();
            _environment = await Engine().ValidateEnvironmentAsync(default);
            long key = PostgresqlTargetLockSession.ProbeLockKey;
            _probeLocksAfter = await ScalarOnServerAsync<long>(
                $"SELECT count(*) FROM pg_catalog.pg_locks WHERE locktype = 'advisory' AND classid = {(uint)((ulong)key >> 32)} AND objid = {(uint)(ulong)key} AND objsubid = 1;"
            );
        }

        [Test]
        public void It_passes_every_server_probe() => ServerReasonsOf(_environment).Should().BeEmpty();

        [Test]
        public void It_is_ready_with_a_real_psql()
        {
            RequirePsqlOrSkip();
            _environment.Should().BeOfType<DmsEngineEnvironment.Ready>();
        }

        [Test]
        public void It_releases_the_probe_lock() => _probeLocksAfter.Should().Be(0);
    }

    [TestFixture]
    public class Given_roles_missing_one_privilege_each
    {
        private readonly List<string> _roles = [];
        private DmsReadinessReason[] _withoutCreateDatabase = [];
        private DmsReadinessReason[] _withoutReadAllStats = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            await EnsureEnqueueOwnerRoleAsync();

            string noCreateDatabase = await CreateLoginRoleAsync("nocreatedb", SignalRole, ReadStatsRole);
            _roles.Add(noCreateDatabase);
            _withoutCreateDatabase =
            [
                .. ServerReasonsOf(await EngineAs(noCreateDatabase).ValidateEnvironmentAsync(default)),
            ];

            string noReadAllStats = await CreateLoginRoleAsync("nostats", SignalRole);
            _roles.Add(noReadAllStats);
            await ExecuteOnServerAsync($"ALTER ROLE {PostgresqlIdentifiers.Quote(noReadAllStats)} CREATEDB;");
            _withoutReadAllStats =
            [
                .. ServerReasonsOf(await EngineAs(noReadAllStats).ValidateEnvironmentAsync(default)),
            ];
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            foreach (string role in _roles)
            {
                await DropRoleAsync(role);
            }
        }

        [Test]
        public void It_reports_the_superuser_requirement_and_the_missing_createdb() =>
            _withoutCreateDatabase
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.SuperuserProbe,
                        DmsProvisioningDiagnostic.ForOperation(
                            PostgresqlEnvironment.ArtifactOwnershipOperation
                        )
                    ),
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.CreateDatabasePrivilegeProbe,
                        DmsProvisioningDiagnostic.ForOperation(
                            PostgresqlEnvironment.CreateDatabasePrivilegeProbe
                        )
                    )
                );

        [Test]
        public void It_reports_the_superuser_requirement_and_the_missing_pg_read_all_stats() =>
            _withoutReadAllStats
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.SuperuserProbe,
                        DmsProvisioningDiagnostic.ForOperation(
                            PostgresqlEnvironment.ArtifactOwnershipOperation
                        )
                    ),
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.ReadAllStatsPrivilegeProbe,
                        DmsProvisioningDiagnostic.ForOperation(
                            PostgresqlEnvironment.ReadAllStatsPrivilegeProbe
                        )
                    )
                );
    }

    [TestFixture]
    public class Given_a_psql_that_cannot_serve
    {
        private DmsEngineEnvironment _wrongPath = null!;
        private DmsEngineEnvironment? _belowFloor;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _wrongPath = await EngineWith(AdminConnection(), MissingPsql()).ValidateEnvironmentAsync(default);
            if (HasPsql)
            {
                _belowFloor = await EngineWith(AdminConnection(), PsqlPath!, minimumPsqlMajorVersion: 99)
                    .ValidateEnvironmentAsync(default);
            }
        }

        [Test]
        public void It_reports_a_wrong_path_as_not_ready() =>
            ReasonsOf(_wrongPath)
                .Should()
                .ContainSingle(reason => reason.Probe == PostgresqlEnvironment.PsqlProbe)
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.StartupValidationFailed
                    && reason.Diagnostic!.Operation == "ProbePsqlVersion.StartFailed"
                    && reason.Diagnostic.ExceptionTypeChain.Length > 0
                );

        [Test]
        public void It_reports_a_psql_below_the_floor_as_engine_incompatible()
        {
            RequirePsqlOrSkip();
            ReasonsOf(_belowFloor!)
                .Should()
                .ContainSingle(reason => reason.Probe == PostgresqlEnvironment.PsqlProbe)
                .Which.Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.EngineIncompatible,
                        PostgresqlEnvironment.PsqlProbe,
                        DmsProvisioningDiagnostic.ForOperation(PostgresqlEnvironment.PsqlBelowFloorOperation)
                    )
                );
        }
    }

    [TestFixture]
    public class Given_another_session_holds_the_probe_lock
    {
        private DmsReadinessReason[] _lockReasons = [];
        private bool _otherStillHolds;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await using NpgsqlConnection other = await AdminConnection().OpenMaintenanceAsync(default);
            await using (NpgsqlCommand take = new("SELECT pg_catalog.pg_advisory_lock(@key);", other))
            {
                take.Parameters.AddWithValue("key", PostgresqlTargetLockSession.ProbeLockKey);
                await take.ExecuteNonQueryAsync();
            }

            DmsEngineEnvironment environment = await EngineWith(
                    AdminConnection(),
                    MissingPsql(),
                    lockAcquireTimeout: TimeSpan.FromSeconds(1)
                )
                .ValidateEnvironmentAsync(default);
            _lockReasons =
            [
                .. ReasonsOf(environment)
                    .Where(reason => reason.Probe == PostgresqlEnvironment.TargetLockProbe),
            ];
            await using NpgsqlCommand release = new("SELECT pg_catalog.pg_advisory_unlock(@key);", other);
            release.Parameters.AddWithValue("key", PostgresqlTargetLockSession.ProbeLockKey);
            _otherStillHolds = (bool)(await release.ExecuteScalarAsync())!;
        }

        [Test]
        public void It_reports_the_contention_as_lock_timeout() =>
            _lockReasons
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.LockTimeout,
                        PostgresqlEnvironment.TargetLockProbe,
                        DmsProvisioningDiagnostic.ForOperation(PostgresqlTargetLockSession.AcquireStep)
                    )
                );

        [Test]
        public void It_leaves_the_other_holder_untouched() => _otherStillHolds.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_server_nothing_listens_on
    {
        private IReadOnlyList<DmsReadinessReason> _reasons = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            PostgresqlAdminConnection unreachable = new(
                new NpgsqlConnectionStringBuilder(AdminConnectionString!)
                {
                    Host = "127.0.0.1",
                    Port = 1,
                    Timeout = 5,
                }.ConnectionString,
                TimeSpan.FromSeconds(5)
            );
            _reasons = ReasonsOf(
                await EngineWith(unreachable, MissingPsql()).ValidateEnvironmentAsync(default)
            );
        }

        [Test]
        public void It_reports_the_server_as_unavailable_at_connect() =>
            _reasons
                .Should()
                .ContainSingle(reason => reason.Probe == PostgresqlEnvironment.TargetServerProbe)
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.ServerUnavailable
                    && reason.Diagnostic!.Operation == PostgresqlEnvironment.ConnectOperation
                );

        [Test]
        public void It_still_runs_the_psql_probe_and_skips_the_lock_probe() =>
            _reasons
                .Select(reason => reason.Probe)
                .Should()
                .BeEquivalentTo(PostgresqlEnvironment.TargetServerProbe, PostgresqlEnvironment.PsqlProbe);
    }
}
