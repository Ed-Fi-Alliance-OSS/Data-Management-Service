// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// Step 4.3 against a live server: the planner reads a real backup's header and file list (D-11) and refuses a
/// device holding two backup sets; the visibility probe (D-21) reports a staging server path the server cannot
/// open as not visible and, when the lane provides a directory shared with the server, reports it visible. No test
/// here restores anything.
/// </summary>
[Category(Category)]
public class MssqlStagingTests
{
    private static Task<string> ServerPropertyAsync(string property) =>
        ScalarOnServerAsync<string>($"SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('{property}'));");

    private static MssqlStagingProbe Probe(
        string stagingPath,
        string stagingServerPath,
        Func<string, string>? headerStatement = null
    ) =>
        new(
            new MssqlBackupStager(
                new MssqlStagingSettings(
                    stagingPath,
                    stagingServerPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                    3,
                    TimeSpan.FromSeconds(30)
                ),
                TimeProvider.System,
                NullLogger.Instance
            ),
            AdminConnection(),
            NullLogger.Instance
        )
        {
            HeaderStatement = headerStatement ?? MssqlRestorePlanner.HeaderOnlyStatement,
        };

    [TestFixture]
    public class Given_a_real_backup_of_a_three_file_database
    {
        private TestDatabase _database = null!;
        private string _dataPath = string.Empty;
        private string _logPath = string.Empty;
        private MssqlBackupDescription _description = null!;
        private MssqlBackupDescription _twoSets = null!;
        private MssqlRestorePlanResult _plan = null!;
        private MssqlRestorePlanResult _twoSetsPlan = null!;
        private const string Stem = "edfi_dms_provision_stage_43a0000000aa__43a0000000aa";

        private static DmsOperationId Operation(string value) =>
            DmsOperationId.TryParse(value, out DmsOperationId operation)
                ? operation
                : throw new InvalidOperationException("The fixed operation id did not parse.");

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("planner");
            _dataPath = await ServerPropertyAsync("InstanceDefaultDataPath");
            _logPath = await ServerPropertyAsync("InstanceDefaultLogPath");
            string extraFile = MssqlServerPaths.Join(_dataPath, _database.Name + "_extra.ndf");
            await ExecuteOnServerAsync(
                $"ALTER DATABASE {MssqlIdentifiers.Quote(_database.Name)} ADD FILE (NAME = N'extra_data', FILENAME = {MssqlIdentifiers.Literal(extraFile)});"
            );

            string onePath = await BackupPathAsync("dms1438-planner-one-set.bak");
            string twoPath = await BackupPathAsync("dms1438-planner-two-sets.bak");
            string quoted = MssqlIdentifiers.Quote(_database.Name);
            await ExecuteOnServerAsync(
                $"""
                BACKUP DATABASE {quoted} TO DISK = {MssqlIdentifiers.Literal(onePath)} WITH INIT, FORMAT;
                BACKUP DATABASE {quoted} TO DISK = {MssqlIdentifiers.Literal(twoPath)} WITH INIT, FORMAT;
                BACKUP DATABASE {quoted} TO DISK = {MssqlIdentifiers.Literal(twoPath)} WITH NOINIT;
                """
            );

            await using SqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
            _description = await MssqlRestorePlanner.ReadAsync(connection, onePath, 60, default);
            _twoSets = await MssqlRestorePlanner.ReadAsync(connection, twoPath, 60, default);
            DmsDatabaseName target = DatabaseNamed("EdFi_Planner_Target");
            _plan = MssqlRestorePlanner.Plan(
                _description,
                Operation("43a0000000aa"),
                target,
                _dataPath,
                _logPath
            );
            _twoSetsPlan = MssqlRestorePlanner.Plan(
                _twoSets,
                Operation("43a0000000aa"),
                target,
                _dataPath,
                _logPath
            );
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_reads_one_full_backup_set_at_position_one_with_the_recorded_name() =>
            _description.Headers.Should().Equal(new MssqlBackupHeader(1, 1, _database.Name));

        [Test]
        public void It_reads_every_file_in_the_servers_order_data_files_first() =>
            _description
                .Files.Should()
                .Equal(
                    new MssqlBackupFile(_database.Name, "D", 1),
                    new MssqlBackupFile("extra_data", "D", 3),
                    new MssqlBackupFile(_database.Name + "_log", "L", 2)
                );

        [Test]
        public void It_places_every_file_under_the_tagged_names_in_the_servers_order() =>
            ((MssqlRestorePlanResult.Planned)_plan)
                .Plan.Moves.Should()
                .Equal(
                    new MssqlFileMove(_database.Name, MssqlServerPaths.Join(_dataPath, Stem + ".mdf")),
                    new MssqlFileMove(
                        "extra_data",
                        MssqlServerPaths.Join(_dataPath, Stem + "_extra_data.ndf")
                    ),
                    new MssqlFileMove(
                        _database.Name + "_log",
                        MssqlServerPaths.Join(_logPath, Stem + "_log.ldf")
                    )
                );

        [Test]
        public void It_reads_both_sets_of_an_appended_device() => _twoSets.Headers.Should().HaveCount(2);

        [Test]
        public void It_refuses_an_appended_device() =>
            ((MssqlRestorePlanResult.Refused)_twoSetsPlan)
                .Defect.Should()
                .Be(MssqlBackupDefect.BackupSetCount);
    }

    [TestFixture]
    public class Given_a_staging_server_path_the_server_cannot_open
    {
        private string _stagingPath = string.Empty;
        private MssqlStagingProbeResult _result = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _stagingPath = Path.Combine(
                Path.GetTempPath(),
                "dms1438-probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))
            );
            Directory.CreateDirectory(_stagingPath);
            string dataPath = await ServerPropertyAsync("InstanceDefaultDataPath");
            string missing = "dms1438-missing-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            string serverPath = dataPath.StartsWith('/') ? "/var/opt/" + missing : @"C:\" + missing;
            _result = await Probe(_stagingPath, serverPath).RunAsync(default);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_stagingPath))
            {
                Directory.Delete(_stagingPath, recursive: true);
            }
        }

        [Test]
        public void It_reports_the_staging_directory_not_visible() =>
            _result.Visibility.Should().Be(MssqlStagingVisibility.NotVisible);

        [Test]
        public void It_carries_the_servers_device_error_number() =>
            _result.Diagnostic!.ProviderErrorCode.Should().Be("3201");

        [Test]
        public void It_names_the_probe_step() =>
            _result.Diagnostic!.Operation.Should().Be(MssqlStagingProbe.HeaderStep);

        [Test]
        public void It_deletes_the_probe_file()
        {
            _result.ProbeDeleted.Should().BeTrue();
            Directory.GetFileSystemEntries(_stagingPath).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_staging_directory_shared_with_the_server
    {
        private string _stagingPath = string.Empty;
        private MssqlStagingProbeResult _result = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            string? stagingPath = Environment.GetEnvironmentVariable(SharedStagingPathVariable);
            string? serverPath = Environment.GetEnvironmentVariable(SharedStagingServerPathVariable);
            if (string.IsNullOrWhiteSpace(stagingPath) || string.IsNullOrWhiteSpace(serverPath))
            {
                DmsProvisioningLaneRequirement.SkipLocallyOrFailInCi(
                    $"{SharedStagingPathVariable} and {SharedStagingServerPathVariable} are not set; the shared-volume probe is skipped."
                );
            }

            _stagingPath = stagingPath;
            _result = await Probe(stagingPath, serverPath).RunAsync(default);
        }

        [Test]
        public void It_reports_the_staging_directory_visible() =>
            _result.Visibility.Should().Be(MssqlStagingVisibility.Visible);

        [Test]
        public void It_carries_no_diagnostic() => _result.Diagnostic.Should().BeNull();

        [Test]
        public void It_deletes_the_probe_file()
        {
            _result.ProbeDeleted.Should().BeTrue();
            Directory.GetFiles(_stagingPath, "dms-provision-*.bak").Should().BeEmpty();
        }
    }

    /// <summary>
    /// SqlClient reports a request it cancelled after sending as <see cref="SqlException"/> <c>0</c>; the probe must
    /// end with <see cref="OperationCanceledException"/> and still delete its file. The header statement is replaced
    /// by a long <c>WAITFOR</c> carrying a marker, so the test cancels only once the server is running the request.
    /// </summary>
    [TestFixture]
    public class Given_a_probe_cancelled_after_its_request_reached_the_server
    {
        private const string RunningRequestQuery =
            "SELECT COUNT(*) FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.session_id <> @@SPID AND t.text LIKE @pattern;";

        private string _stagingPath = string.Empty;
        private bool _dispatched;
        private Exception? _thrown;
        private MssqlStagingProbeResult? _result;
        private bool _requestGone;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _stagingPath = Path.Combine(
                Path.GetTempPath(),
                "dms1438-probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))
            );
            Directory.CreateDirectory(_stagingPath);
            string marker =
                "dms1438_probe_cancel_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            string pattern = "%" + marker + "%";
            MssqlStagingProbe probe = Probe(
                _stagingPath,
                "/var/opt/dms-staging",
                _ => $"WAITFOR DELAY '00:02:00'; -- {marker}"
            );

            using CancellationTokenSource cancellation = new();
            Task<MssqlStagingProbeResult> run = probe.RunAsync(cancellation.Token);
            _dispatched = await WaitUntilAsync(
                async () => await ScalarOnServerAsync<int>(RunningRequestQuery, ("@pattern", pattern)) == 1,
                TimeSpan.FromSeconds(30)
            );
            await cancellation.CancelAsync();
            try
            {
                _result = await run;
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }

            _requestGone = await WaitUntilAsync(
                async () => await ScalarOnServerAsync<int>(RunningRequestQuery, ("@pattern", pattern)) == 0,
                TimeSpan.FromSeconds(20)
            );
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_stagingPath))
            {
                Directory.Delete(_stagingPath, recursive: true);
            }
        }

        [Test]
        public void It_cancelled_while_the_server_ran_the_request() => _dispatched.Should().BeTrue();

        [Test]
        public void It_returns_no_verdict() => _result.Should().BeNull();

        [Test]
        public void It_ends_with_operation_canceled() =>
            _thrown.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_deletes_the_probe_file() =>
            Directory.GetFileSystemEntries(_stagingPath).Should().BeEmpty();

        [Test]
        public void It_leaves_no_request_on_the_server() => _requestGone.Should().BeTrue();
    }
}
