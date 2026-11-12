// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;
using ProcessResult = EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql.MssqlBackupStagerSeparateIdentityTests.ProcessResult;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The 7.4 container topology with real separate identities (Step 5.3, recipe in <c>docs/RUNNING-LOCALLY.md</c>): the
/// tests run as a non-root CMS identity that owns the shared staging directory (group = the staging group, mode
/// <c>2750</c>), and the SQL Server container reaches it only through <c>--group-add</c> of the staging group. Each
/// fixture works in its own subdirectory of the shared directory, mode <c>2750</c>, which differs between them only in
/// its group: with the staging group the visibility probe passes, a create restores a server-made backup through the
/// staged file, and the server, which can read a staged file, cannot overwrite it or add an entry beside it (OS error
/// 5); with <see cref="UnsharedGroupVariable"/> (a group of the CMS identity the server is not in) the probe reports
/// <c>StagingNotVisible</c> and a create fails with <c>StagingFailed</c>, both on the server's device error
/// <c>3201</c>, leaving nothing behind. Its own category, because it needs that deployment: the CI lane does not
/// provide it, and it is skipped without it.
/// </summary>
[Category(SeparateIdentityCategory)]
public class MssqlSeparateIdentityStagingTests
{
    public const string SeparateIdentityCategory = "DmsProvisioningSeparateIdentity";

    /// <summary>A group the CMS identity belongs to and the SQL Server identity does not (name or gid).</summary>
    public const string UnsharedGroupVariable = "DMS_PROVISIONING_UNSHARED_GROUP";

    private const UnixFileMode Mode2750 =
        UnixFileMode.SetGroup
        | UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute;

    private static string StagingGroup =>
        Environment.GetEnvironmentVariable(MssqlBackupStagerSeparateIdentityTests.StagingGroupVariable)!;

    private static string UnsharedGroup => Environment.GetEnvironmentVariable(UnsharedGroupVariable)!;

    private static Task<ProcessResult> ShellAsync(string script, params string[] arguments) =>
        MssqlBackupStagerSeparateIdentityTests.RunShellAsync([], script, arguments);

    /// <summary>
    /// Skips without the deployment; fails when the deployment is not the one the fixture proves: the process is
    /// root, or the shared directory is not owned by it with the staging group and mode <c>2750</c>.
    /// </summary>
    private static async Task RequireSeparateIdentitiesAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The separate-identity staging test needs Unix modes and a Linux SQL Server.");
        }

        if (
            !IsConfigured
            || string.IsNullOrWhiteSpace(SharedStagingPath)
            || string.IsNullOrWhiteSpace(SharedStagingServerPath)
            || string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    MssqlBackupStagerSeparateIdentityTests.StagingGroupVariable
                )
            )
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UnsharedGroupVariable))
        )
        {
            Assert.Ignore(
                $"{AdminEnvironmentVariableName}, {SharedStagingPathVariable}, {SharedStagingServerPathVariable}, "
                    + $"{MssqlBackupStagerSeparateIdentityTests.StagingGroupVariable} and {UnsharedGroupVariable} "
                    + "are not all set; the separate-identity staging test is skipped (docs/RUNNING-LOCALLY.md)."
            );
        }

        Environment.IsPrivilegedProcess.Should().BeFalse("the CMS identity must not be root");
        ProcessResult shared = await ShellAsync("stat -c '%u %g %G %a' \"$1\" && id -u", SharedStagingPath!);
        shared.ExitCode.Should().Be(0, shared.Error);
        string[] facts = shared.Output.Split(
            [' ', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        facts[0].Should().Be(facts[4], "the CMS identity owns the shared staging directory");
        StagingGroup.Should().BeOneOf([facts[1], facts[2]], "the shared directory has the staging group");
        facts[3].Should().Be("2750");
        new[] { facts[1], facts[2] }.Should().NotContain(UnsharedGroup);
    }

    /// <summary>
    /// A subdirectory of the shared staging directory, mode <c>2750</c>, with <paramref name="group"/> (the inherited
    /// staging group when null), and its path as the server sees it; deleted with <see cref="DisposeAsync"/>.
    /// </summary>
    private sealed class StagingSubdirectory : IAsyncDisposable
    {
        private StagingSubdirectory(string name)
        {
            Path = System.IO.Path.Combine(SharedStagingPath!, name);
            ServerPath = SharedStagingServerPath!.TrimEnd('/') + "/" + name;
        }

        public string Path { get; }

        public string ServerPath { get; }

        public string Group { get; private set; } = "";

        public static async Task<StagingSubdirectory> CreateAsync(string? group)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The staging subdirectory needs Unix modes.");
            }

            StagingSubdirectory directory = new(
                "dms1438-identity-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))
            );
            Directory.CreateDirectory(directory.Path);
            if (group is not null)
            {
                ProcessResult changed = await ShellAsync("chgrp \"$1\" \"$2\"", group, directory.Path);
                changed.ExitCode.Should().Be(0, changed.Error);
            }

            // After chgrp: the mode is set by the owner, so setgid holds for the group the directory now has.
            File.SetUnixFileMode(directory.Path, Mode2750);
            directory.Group = (await ShellAsync("stat -c '%G %g %a' \"$1\"", directory.Path)).Output.Trim();
            return directory;
        }

        public MssqlStagingSettings Settings =>
            new(
                Path,
                ServerPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                3,
                TimeSpan.FromSeconds(30)
            );

        public MssqlDmsTargetEngine Engine() =>
            new(
                AdminConnection(),
                new MssqlDmsEngineSettings(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(5),
                    Settings
                ),
                TimeProvider.System,
                NullLogger.Instance
            );

        public string[] Entries() =>
            [.. Directory.GetFileSystemEntries(Path).Select(entry => System.IO.Path.GetFileName(entry))];

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    [TestFixture]
    public class Given_the_server_reaches_the_staging_directory_through_the_staging_group
    {
        private StagingSubdirectory? _staging;
        private Lane? _lane;
        private string _scratch = "";
        private DmsEngineEnvironment _environment = null!;
        private DmsDataStoreCreateResult _result = null!;
        private string[] _entriesAfterCreate = [];
        private MssqlStagedBackup? _staged;
        private (int[] Numbers, string Messages) _read;
        private (int[] Numbers, string Messages) _overwrite;
        private (int[] Numbers, string Messages) _newEntry;
        private MssqlStagedFileCheck _afterAttempts = null!;
        private string[] _entriesAfterAttempts = [];

        /// <summary>Runs <paramref name="sql"/> on the server and returns the error numbers and messages it refused it with (none when it succeeded).</summary>
        private static async Task<(int[] Numbers, string Messages)> RefusalOfAsync(string sql)
        {
            try
            {
                await ExecuteOnServerAsync(sql);
                return ([], "");
            }
            catch (SqlException exception)
            {
                SqlError[] errors = [.. exception.Errors.Cast<SqlError>()];
                return (
                    [.. errors.Select(error => error.Number)],
                    string.Join(" | ", errors.Select(error => error.Message))
                );
            }
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            await RequireSeparateIdentitiesAsync();
            _staging = await StagingSubdirectory.CreateAsync(group: null);
            _environment = await _staging.Engine().ValidateEnvironmentAsync(default);

            byte[] backup = await ShapedBackup.CreateAsync();
            _lane = await Lane.BuildAsync(backup, new LaneHooks(), _staging.Engine());
            _result = await _lane.CreateAsync();
            _entriesAfterCreate = _staging.Entries();

            // The server as the reader: a staged file it restores from, then attempts to replace it and to add beside it.
            _scratch = Path.Combine(Path.GetTempPath(), "dms1438-identity-" + DmsOperationId.New().Value);
            Directory.CreateDirectory(_scratch);
            MssqlBackupStager stager = new(_staging.Settings, TimeProvider.System, NullLogger.Instance);
            _staged = (
                (MssqlStagingResult.Staged)
                    await stager.StageAsync(
                        ShapedBackup.Extracted(_scratch, backup),
                        DmsOperationId.New(),
                        default
                    )
            ).Backup;
            _read = await RefusalOfAsync(
                $"RESTORE HEADERONLY FROM DISK = {MssqlIdentifiers.Literal(_staged.ServerPath)};"
            );
            _overwrite = await RefusalOfAsync(
                $"BACKUP DATABASE [model] TO DISK = {MssqlIdentifiers.Literal(_staged.ServerPath)} WITH FORMAT, INIT, COPY_ONLY;"
            );
            _newEntry = await RefusalOfAsync(
                $"BACKUP DATABASE [model] TO DISK = {MssqlIdentifiers.Literal(_staging.ServerPath + "/dms-provision-ffffffffffff.bak")} WITH FORMAT, INIT, COPY_ONLY;"
            );
            _afterAttempts = await MssqlBackupStager.VerifyAsync(_staged, default);
            _entriesAfterAttempts = _staging.Entries();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }

            if (_staging is not null)
            {
                await _staging.DisposeAsync();
            }

            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }

        [Test]
        public void It_works_in_a_2750_directory_with_the_staging_group() =>
            _staging!.Group.Split(' ').Should().Contain(StagingGroup).And.EndWith("2750");

        [Test]
        public void It_is_ready() => _environment.Should().BeOfType<DmsEngineEnvironment.Ready>();

        [Test]
        public void It_creates_the_target_by_a_restore_from_the_staged_file()
        {
            _result.Should().BeOfType<DmsDataStoreCreateResult.Created>();
            _lane!
                .Engine.RestoreOutcomes.Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>();
        }

        [Test]
        public void It_leaves_no_staged_file_after_the_create() => _entriesAfterCreate.Should().BeEmpty();

        [Test]
        public void It_lets_the_server_read_a_staged_file() => _read.Numbers.Should().BeEmpty(_read.Messages);

        [Test]
        public void It_refuses_the_server_an_overwrite_of_the_staged_file() => AssertAccessDenied(_overwrite);

        [Test]
        public void It_refuses_the_server_a_new_entry_beside_it() => AssertAccessDenied(_newEntry);

        [Test]
        public void It_keeps_the_staged_file_intact_and_alone()
        {
            _afterAttempts.Should().BeOfType<MssqlStagedFileCheck.Intact>();
            _entriesAfterAttempts.Should().Equal(_staged!.FileName);
        }

        private static void AssertAccessDenied((int[] Numbers, string Messages) refusal)
        {
            refusal.Numbers.Should().Contain(MssqlErrorClassifier.BackupDeviceCannotOpen);
            refusal.Messages.Should().Contain("Operating system error 5(");
        }
    }

    [TestFixture]
    public class Given_the_staging_group_is_removed_from_the_directory
    {
        private StagingSubdirectory? _staging;
        private Lane? _lane;
        private DmsEngineEnvironment _environment = null!;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _stagingAfter = null!;
        private DmsLiveTargetState _targetAfter = null!;
        private string[] _entriesAfter = [];

        private static DmsProvisioningFailure FailureOf(DmsDataStoreCreateResult result) =>
            result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

        [OneTimeSetUp]
        public async Task Setup()
        {
            await RequireSeparateIdentitiesAsync();
            _staging = await StagingSubdirectory.CreateAsync(UnsharedGroup);
            _environment = await _staging.Engine().ValidateEnvironmentAsync(default);
            _lane = await Lane.BuildAsync(
                await ShapedBackup.CreateAsync(),
                new LaneHooks(),
                _staging.Engine()
            );
            _result = await _lane.CreateAsync();
            MssqlDmsTargetEngine side = MssqlProvisioningTestServer.Engine();
            _stagingAfter = await side.InspectAsync(_lane.Operation.StagingDatabaseName, default);
            _targetAfter = await side.InspectAsync(_lane.Target, default);
            _entriesAfter = _staging.Entries();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }

            if (_staging is not null)
            {
                await _staging.DisposeAsync();
            }
        }

        [Test]
        public void It_works_in_a_2750_directory_with_a_group_the_server_is_not_in() =>
            _staging!.Group.Split(' ').Should().Contain(UnsharedGroup).And.EndWith("2750");

        [Test]
        public void It_reports_only_the_staging_directory_as_not_visible_with_the_device_error() =>
            _environment
                .Should()
                .BeOfType<DmsEngineEnvironment.NotReady>()
                .Subject.Reasons.Should()
                .ContainSingle()
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.StagingNotVisible
                    && reason.Probe == MssqlEnvironment.StagingVisibilityProbe
                    && reason.Diagnostic!.ProviderErrorCode == "3201"
                    && reason.Diagnostic.Operation == MssqlStagingProbe.HeaderStep
                );

        [Test]
        public void It_fails_the_create_as_a_staging_failure_with_the_device_error() =>
            FailureOf(_result)
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Code = DmsProvisioningFailureCode.StagingFailed,
                        Diagnostic = new
                        {
                            Operation = MssqlBackupStager.NotVisibleStep,
                            ProviderErrorCode = "3201",
                        },
                    }
                );

        [Test]
        public void It_leaves_no_staging_database_target_or_staged_file()
        {
            (_stagingAfter, _targetAfter)
                .Should()
                .Be((new DmsLiveTargetState.Absent(), new DmsLiveTargetState.Absent()));
            _entriesAfter.Should().BeEmpty();
            _lane!.TargetMutations.Should().BeEmpty();
        }
    }
}
