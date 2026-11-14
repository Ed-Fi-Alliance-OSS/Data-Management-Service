// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The SQL Server environmental validation against a live server (7.3 items 4, 6 and 7, Step 4.9): the lane admin
/// passes every server probe and, with a staging server path the server cannot open, is <c>NotReady</c> only as
/// <c>StagingNotVisible</c> (error 3201), with the probe file deleted and the probe lock released; a login that is
/// not <c>sysadmin</c> is <c>NotReady</c> naming exactly that privilege, even with every other privilege, a login
/// with no permission names every one, and a <c>dbcreator</c>-only login also names the permissions the role does
/// not carry; a server below the floor is
/// <c>EngineIncompatible</c>; a probe lock held by another session is <c>LockTimeout</c> with that holder untouched;
/// the one-shot heartbeat answers held on a fresh holder and declares a holder lost once its lock is released;
/// and, when the lane shares a staging directory with the server, the lane admin is <c>Ready</c>.
/// </summary>
[Category(Category)]
public class MssqlEnvironmentTests
{
    /// <summary>The probes that read the server, without the staging probes, which depend on the lane's volumes.</summary>
    private static readonly HashSet<string> _stagingProbes =
    [
        MssqlEnvironment.StagingDirectoryProbe,
        MssqlEnvironment.StagingVisibilityProbe,
    ];

    private static IReadOnlyList<DmsReadinessReason> ReasonsOf(DmsEngineEnvironment environment) =>
        environment is DmsEngineEnvironment.NotReady notReady ? notReady.Reasons : [];

    private static DmsReadinessReason[] ServerReasonsOf(DmsEngineEnvironment environment) =>
        [.. ReasonsOf(environment).Where(reason => !_stagingProbes.Contains(reason.Probe))];

    private static DmsReadinessReason Missing(string probe, string? operation = null) =>
        new(
            DmsProvisioningFailureCode.AdminPrivilegeMissing,
            probe,
            DmsProvisioningDiagnostic.ForOperation(operation ?? probe)
        );

    private static readonly DmsReadinessReason _createDatabase = Missing(
        MssqlEnvironment.CreateDatabasePrivilegeProbe,
        MssqlEnvironment.CreateDatabaseRoleOperation
    );

    /// <summary>A private staging directory on the CMS host, mode 0750 on Unix; deleted with <see cref="IDisposable.Dispose"/>.</summary>
    private sealed class LocalStagingDirectory : IDisposable
    {
        public LocalStagingDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dms1438-readiness-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))
            );
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    Path,
                    UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute
                );
            }
        }

        public string Path { get; }

        public string[] Entries() => Directory.GetFileSystemEntries(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    /// <summary>A server-side directory that does not exist, in the server's own path convention.</summary>
    private static async Task<string> MissingServerPathAsync()
    {
        string dataPath = await ScalarOnServerAsync<string>(
            "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath'));"
        );
        string missing = "dms1438-missing-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        return dataPath.StartsWith('/') ? "/var/opt/" + missing : @"C:\" + missing;
    }

    private static MssqlDmsTargetEngine EngineWith(
        MssqlAdminConnection connection,
        string stagingPath,
        string stagingServerPath,
        TimeSpan? lockAcquireTimeout = null,
        int minimumProductMajorVersion = MssqlEnvironment.MinimumProductMajorVersion
    ) =>
        new(
            connection,
            new MssqlDmsEngineSettings(
                lockAcquireTimeout ?? TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                new MssqlStagingSettings(
                    stagingPath,
                    stagingServerPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                    3,
                    TimeSpan.FromSeconds(30)
                )
            ),
            TimeProvider.System,
            NullLogger.Instance
        )
        {
            MinimumProductMajorVersion = minimumProductMajorVersion,
        };

    [TestFixture]
    public class Given_the_lane_admin_and_a_staging_server_path_the_server_cannot_open
    {
        private LocalStagingDirectory _staging = null!;
        private DmsEngineEnvironment _environment = null!;
        private int _probeLockGrantableAfter;
        private int _serverMajorVersion;

        private static Task<int> ProbeLockGrantableAsync() =>
            ScalarOnServerAsync<int>(
                "SELECT APPLOCK_TEST(N'public', @resource, N'Exclusive', N'Session');",
                ("@resource", MssqlTargetLockSession.ProbeResource)
            );

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staging = new LocalStagingDirectory();
            _environment = await EngineWith(AdminConnection(), _staging.Path, await MissingServerPathAsync())
                .ValidateEnvironmentAsync(default);
            _probeLockGrantableAfter = await ProbeLockGrantableAsync();
            _serverMajorVersion = await ScalarOnServerAsync<int>(
                "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));"
            );
        }

        [OneTimeTearDown]
        public void TearDown() => _staging?.Dispose();

        [Test]
        public void It_passes_every_server_probe_and_the_lock_probe() =>
            ServerReasonsOf(_environment).Should().BeEmpty();

        [Test]
        public void It_reports_only_the_staging_directory_as_not_visible_with_the_device_error() =>
            ReasonsOf(_environment)
                .Should()
                .ContainSingle()
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.StagingNotVisible
                    && reason.Probe == MssqlEnvironment.StagingVisibilityProbe
                    && reason.Diagnostic!.ProviderErrorCode == "3201"
                    && reason.Diagnostic.Operation == MssqlStagingProbe.HeaderStep
                );

        [Test]
        public void It_deletes_the_probe_file() => _staging.Entries().Should().BeEmpty();

        [Test]
        public void It_releases_the_probe_lock() => _probeLockGrantableAfter.Should().Be(1);

        [Test]
        public void It_reports_the_servers_major_version_for_the_template_comparison() =>
            _environment.ServerMajorVersion.Should().Be(_serverMajorVersion);
    }

    [TestFixture]
    public class Given_logins_missing_privileges
    {
        private readonly List<string> _logins = [];
        private LocalStagingDirectory _staging = null!;
        private DmsReadinessReason[] _withoutDbcreator = [];
        private DmsReadinessReason[] _withNothing = [];
        private DmsReadinessReason[] _dbcreatorOnly = [];
        private DmsReadinessReason[] _everyPrivilegeButSysadmin = [];
        private int _everyPrivilegeButSysadminIsSysadmin = -1;

        private async Task<DmsReadinessReason[]> ServerReasonsAsAsync(string login, string serverPath) =>
            ServerReasonsOf(
                await EngineWith(AdminConnectionAs(login), _staging.Path, serverPath)
                    .ValidateEnvironmentAsync(default)
            );

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staging = new LocalStagingDirectory();
            string serverPath = await MissingServerPathAsync();

            string withoutDbcreator = await CreateLoginAsync("nodbcreator");
            _logins.Add(withoutDbcreator);
            string quoted = MssqlIdentifiers.Quote(withoutDbcreator);
            await ExecuteOnServerAsync(
                $"GRANT ALTER ANY DATABASE TO {quoted}; GRANT ALTER ANY CONNECTION TO {quoted}; GRANT VIEW SERVER STATE TO {quoted};"
            );
            _withoutDbcreator = await ServerReasonsAsAsync(withoutDbcreator, serverPath);

            string withNothing = await CreateLoginAsync("bare");
            _logins.Add(withNothing);
            _withNothing = await ServerReasonsAsAsync(withNothing, serverPath);

            string dbcreatorOnly = await CreateLoginAsync("dbcreator");
            _logins.Add(dbcreatorOnly);
            await ExecuteOnServerAsync(
                $"ALTER SERVER ROLE [dbcreator] ADD MEMBER {MssqlIdentifiers.Quote(dbcreatorOnly)};"
            );
            _dbcreatorOnly = await ServerReasonsAsAsync(dbcreatorOnly, serverPath);

            // Every privilege this provisioner documented before it required sysadmin.
            string everyPrivilege = await CreateLoginAsync("nosysadmin");
            _logins.Add(everyPrivilege);
            string everyQuoted = MssqlIdentifiers.Quote(everyPrivilege);
            await ExecuteOnServerAsync(
                $"ALTER SERVER ROLE [dbcreator] ADD MEMBER {everyQuoted}; GRANT ALTER ANY DATABASE TO {everyQuoted}; GRANT ALTER ANY CONNECTION TO {everyQuoted}; GRANT VIEW SERVER STATE TO {everyQuoted};"
            );
            _everyPrivilegeButSysadminIsSysadmin = await ScalarOnServerAsync<int>(
                "SELECT CONVERT(int, IS_SRVROLEMEMBER(N'sysadmin', @login));",
                ("@login", everyPrivilege)
            );
            _everyPrivilegeButSysadmin = await ServerReasonsAsAsync(everyPrivilege, serverPath);
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            SqlConnection.ClearAllPools();
            foreach (string login in _logins)
            {
                await DropLoginAsync(login);
            }

            _staging?.Dispose();
        }

        [Test]
        public void It_reports_exactly_the_missing_sysadmin_without_dbcreator() =>
            _withoutDbcreator.Should().Equal(_createDatabase);

        [Test]
        public void It_reports_exactly_the_missing_sysadmin_for_every_other_documented_privilege()
        {
            _everyPrivilegeButSysadminIsSysadmin.Should().Be(0);
            _everyPrivilegeButSysadmin.Should().Equal(_createDatabase);
        }

        [Test]
        public void It_reports_every_missing_privilege_and_the_hidden_file_catalog() =>
            _withNothing
                .Should()
                .Equal(
                    _createDatabase,
                    Missing(MssqlEnvironment.AlterAnyDatabasePrivilegeProbe),
                    Missing(MssqlEnvironment.AlterAnyConnectionPrivilegeProbe),
                    Missing(MssqlEnvironment.ViewServerStatePrivilegeProbe),
                    Missing(MssqlEnvironment.FileCatalogVisibilityProbe)
                );

        [Test]
        public void It_reports_sysadmin_and_the_permissions_dbcreator_does_not_carry() =>
            _dbcreatorOnly
                .Should()
                .Equal(
                    _createDatabase,
                    Missing(MssqlEnvironment.AlterAnyDatabasePrivilegeProbe),
                    Missing(MssqlEnvironment.AlterAnyConnectionPrivilegeProbe),
                    Missing(MssqlEnvironment.ViewServerStatePrivilegeProbe)
                );
    }

    [TestFixture]
    public class Given_a_floor_above_the_server
    {
        private LocalStagingDirectory _staging = null!;
        private DmsReadinessReason[] _reasons = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staging = new LocalStagingDirectory();
            _reasons = ServerReasonsOf(
                await EngineWith(
                        AdminConnection(),
                        _staging.Path,
                        await MissingServerPathAsync(),
                        minimumProductMajorVersion: 99
                    )
                    .ValidateEnvironmentAsync(default)
            );
        }

        [OneTimeTearDown]
        public void TearDown() => _staging?.Dispose();

        [Test]
        public void It_reports_the_server_as_engine_incompatible() =>
            _reasons
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.EngineIncompatible,
                        MssqlEnvironment.ServerVersionProbe,
                        DmsProvisioningDiagnostic.ForOperation(MssqlEnvironment.ServerVersionProbe)
                    )
                );
    }

    [TestFixture]
    public class Given_another_session_holds_the_probe_lock
    {
        private LocalStagingDirectory _staging = null!;
        private DmsReadinessReason[] _lockReasons = [];
        private string _otherModeAfter = string.Empty;

        private static SqlCommand LockProcedure(string procedure, SqlConnection connection)
        {
            SqlCommand command = new(procedure, connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.Add(
                new SqlParameter("@Resource", SqlDbType.NVarChar, 255)
                {
                    Value = MssqlTargetLockSession.ProbeResource,
                }
            );
            command.Parameters.Add(
                new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Session" }
            );
            command.Parameters.Add(
                new SqlParameter("@DbPrincipal", SqlDbType.NVarChar, 128) { Value = "public" }
            );
            return command;
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staging = new LocalStagingDirectory();
            await using SqlConnection other = await AdminConnection().OpenMaintenanceAsync(default);
            await using (SqlCommand take = LockProcedure("master.sys.sp_getapplock", other))
            {
                take.Parameters.Add(
                    new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" }
                );
                await take.ExecuteNonQueryAsync();
            }

            DmsEngineEnvironment environment = await EngineWith(
                    AdminConnection(),
                    _staging.Path,
                    await MissingServerPathAsync(),
                    lockAcquireTimeout: TimeSpan.FromSeconds(1)
                )
                .ValidateEnvironmentAsync(default);
            _lockReasons =
            [
                .. ReasonsOf(environment).Where(reason => reason.Probe == MssqlEnvironment.TargetLockProbe),
            ];

            await using (
                SqlCommand mode = new("SELECT APPLOCK_MODE(N'public', @resource, N'Session');", other)
            )
            {
                mode.Parameters.Add(
                    new SqlParameter("@resource", SqlDbType.NVarChar, 255)
                    {
                        Value = MssqlTargetLockSession.ProbeResource,
                    }
                );
                _otherModeAfter = (string)(await mode.ExecuteScalarAsync())!;
            }

            await using SqlCommand release = LockProcedure("master.sys.sp_releaseapplock", other);
            await release.ExecuteNonQueryAsync();
        }

        [OneTimeTearDown]
        public void TearDown() => _staging?.Dispose();

        [Test]
        public void It_reports_the_contention_as_lock_timeout() =>
            _lockReasons
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.LockTimeout,
                        MssqlEnvironment.TargetLockProbe,
                        DmsProvisioningDiagnostic.ForOperation(MssqlTargetLockSession.AcquireStep)
                    )
                );

        [Test]
        public void It_leaves_the_other_holder_untouched() => _otherModeAfter.Should().Be("Exclusive");
    }

    /// <summary>
    /// The one-shot heartbeat the lock probe runs: on a fresh holder it answers held and leaves the holder usable;
    /// once the lock is released on the holder's own live session it answers not held and declares the holder lost,
    /// as the heartbeat's own loss rule does.
    /// </summary>
    [TestFixture]
    public class Given_a_probe_holder
    {
        private bool _heldWhileHolding;
        private bool _lostWhileHolding;
        private bool _heldAfterRelease;
        private bool _lostAfterRelease;

        private static async Task<MssqlTargetLockSession> AcquireProbeAsync() =>
            await MssqlTargetLockSession.AcquireResourceAsync(
                AdminConnection(),
                MssqlTargetLockSession.ProbeResource,
                DmsOperationId.New(),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromHours(1),
                TimeProvider.System,
                NullLogger.Instance,
                connectionOpened: null,
                default
            )
                is DmsTargetLockResult.Acquired acquired
                ? (MssqlTargetLockSession)acquired.Holder
                : throw new InvalidOperationException("The probe lock was not acquired.");

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await using (MssqlTargetLockSession holder = await AcquireProbeAsync())
            {
                _heldWhileHolding = await holder.ProbeOnceAsync(default);
                _lostWhileHolding = holder.IsLost;
            }

            await using MssqlTargetLockSession released = await AcquireProbeAsync();
            await released.RunAsync(
                async cancellationToken =>
                {
                    await using SqlCommand command = new("master.sys.sp_releaseapplock", released.Connection);
                    command.CommandType = CommandType.StoredProcedure;
                    command.Parameters.AddWithValue("@Resource", MssqlTargetLockSession.ProbeResource);
                    command.Parameters.AddWithValue("@LockOwner", "Session");
                    return await command.ExecuteNonQueryAsync(cancellationToken);
                },
                TimeSpan.FromSeconds(30),
                default
            );
            _heldAfterRelease = await released.ProbeOnceAsync(default);
            _lostAfterRelease = released.IsLost;
        }

        [Test]
        public void It_answers_held_on_a_fresh_holder() => _heldWhileHolding.Should().BeTrue();

        [Test]
        public void It_leaves_a_fresh_holder_usable() => _lostWhileHolding.Should().BeFalse();

        [Test]
        public void It_answers_not_held_once_the_lock_is_released_under_the_holder() =>
            _heldAfterRelease.Should().BeFalse();

        [Test]
        public void It_declares_that_holder_lost() => _lostAfterRelease.Should().BeTrue();
    }

    [TestFixture]
    public class Given_the_lane_admin_and_a_staging_directory_shared_with_the_server
    {
        private DmsEngineEnvironment _environment = null!;
        private string _stagingPath = string.Empty;
        private int _sysadmin = -1;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _stagingPath = SharedStagingPath!;
            _sysadmin = await ScalarOnServerAsync<int>("SELECT CONVERT(int, IS_SRVROLEMEMBER(N'sysadmin'));");
            _environment = await EngineWith(AdminConnection(), _stagingPath, SharedStagingServerPath!)
                .ValidateEnvironmentAsync(default);
        }

        [Test]
        public void It_is_ready_as_sysadmin()
        {
            _sysadmin.Should().Be(1);
            _environment.Should().BeOfType<DmsEngineEnvironment.Ready>();
        }

        [Test]
        public void It_leaves_no_probe_file() =>
            Directory.GetFiles(_stagingPath, "dms-provision-*.bak").Should().BeEmpty();
    }
}
