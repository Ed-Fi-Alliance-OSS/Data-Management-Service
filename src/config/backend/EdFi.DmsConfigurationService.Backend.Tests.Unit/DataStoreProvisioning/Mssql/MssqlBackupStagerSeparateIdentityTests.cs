// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// D-18 with separate processes on Unix. The staging directory is the documented recipe: owned by the test's user,
/// group = the staging group, mode <c>2750</c>. A process running as the reader identity (a staging-group member that
/// is not the owner, as the SQL Server service identity is) can read the staged file but cannot unlink, rename,
/// overwrite, or add an entry beside it: each attempt fails with <c>EACCES</c> and the file keeps its hash. A process
/// with write permission (a simulated administrator) can replace it, and the independent re-hash reports the
/// mismatch that 6.4 step 7 turns into <c>StagingIntegrityViolation</c> (Step 4.5). The reader cases need
/// <see cref="ReaderCommandVariable"/> (a command prefix that runs its arguments as the reader, such as
/// <c>setpriv --reuid=dmsreader --regid=dmsstaging --clear-groups</c> under root, or <c>sudo -n -u dmsreader</c>) and
/// <see cref="StagingGroupVariable"/>; without them they are skipped. <c>docs/RUNNING-LOCALLY.md</c> runs them with
/// real separate identities (Step 5.3): a non-root owner and a reader with the SQL Server container's uid.
/// </summary>
public class MssqlBackupStagerSeparateIdentityTests
{
    public const string ReaderCommandVariable = "DMS_PROVISIONING_STAGING_READER_COMMAND";

    public const string StagingGroupVariable = "DMS_PROVISIONING_STAGING_GROUP";

    private const UnixFileMode Mode2750 =
        UnixFileMode.SetGroup
        | UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute;

    /// <summary>The exit code, standard output, and standard error of a finished process.</summary>
    public sealed record ProcessResult(int ExitCode, string Output, string Error);

    /// <summary>Runs <c>/bin/sh -c <paramref name="script"/> sh <paramref name="arguments"/></c>, behind <paramref name="prefix"/> when given.</summary>
    public static async Task<ProcessResult> RunShellAsync(
        string[] prefix,
        string script,
        params string[] arguments
    )
    {
        string[] command = [.. prefix, "/bin/sh", "-c", script, "sh", .. arguments];
        ProcessStartInfo start = new(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["LC_ALL"] = "C";
        foreach (string argument in command.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        using Process process =
            Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource bound = new(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(bound.Token);
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    [TestFixture]
    public class Given_a_staged_file_in_a_2750_directory_and_a_reader_process
    {
        private MssqlBackupStagerTests.StagingDirectory? _directory;
        private MssqlStagedBackup _backup = null!;
        private string[] _reader = [];
        private ProcessResult _read = null!;
        private ProcessResult _unlink = null!;
        private ProcessResult _rename = null!;
        private ProcessResult _overwrite = null!;
        private ProcessResult _create = null!;
        private MssqlStagedFileCheck _afterAttempts = null!;
        private string _fileGroup = "";

        [SetUp]
        public async Task Setup()
        {
            string? readerCommand = Environment.GetEnvironmentVariable(ReaderCommandVariable);
            string? group = Environment.GetEnvironmentVariable(StagingGroupVariable);
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("The separate-identity staging test needs Unix modes and processes.");
                return;
            }

            if (string.IsNullOrWhiteSpace(readerCommand) || string.IsNullOrWhiteSpace(group))
            {
                Assert.Ignore(
                    $"{ReaderCommandVariable} and {StagingGroupVariable} are not set; the separate-identity staging test is skipped."
                );
                return;
            }

            _reader = readerCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _directory = new MssqlBackupStagerTests.StagingDirectory();

            // The documented recipe (7.4): chgrp to the staging group, then 2750. The root and its parent must be traversable by the reader.
            File.SetUnixFileMode(_directory.Root, Mode2750 & ~UnixFileMode.SetGroup);
            (
                await RunShellAsync(
                    [],
                    "chgrp \"$1\" \"$2\" && chgrp \"$1\" \"$3\"",
                    group,
                    _directory.StagingPath,
                    _directory.Root
                )
            )
                .ExitCode.Should()
                .Be(0);
            File.SetUnixFileMode(_directory.StagingPath, Mode2750);

            _backup = (
                (MssqlStagingResult.Staged)
                    await new MssqlBackupStager(
                        _directory.Settings,
                        TimeProvider.System,
                        NullLogger.Instance
                    ).StageAsync(
                        _directory.Artifact,
                        MssqlBackupStagerTests.Operation(),
                        CancellationToken.None
                    )
            ).Backup;
            _fileGroup = (await RunShellAsync([], "stat -c %G \"$1\"", _backup.LocalPath)).Output.Trim();

            _read = await RunShellAsync(_reader, "sha256sum \"$1\"", _backup.LocalPath);
            _unlink = await RunShellAsync(_reader, "rm -f \"$1\"", _backup.LocalPath);
            _rename = await RunShellAsync(_reader, "mv \"$1\" \"$1.moved\"", _backup.LocalPath);
            _overwrite = await RunShellAsync(_reader, "printf tampered > \"$1\"", _backup.LocalPath);
            _create = await RunShellAsync(
                _reader,
                "printf tampered > \"$1/dms-provision-ffffffffffff.bak\"",
                _directory.StagingPath
            );
            _afterAttempts = await MssqlBackupStager.VerifyAsync(_backup, CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _directory?.Dispose();

        [Test]
        public void It_gives_the_file_the_staging_group_through_setgid() =>
            _fileGroup.Should().Be(Environment.GetEnvironmentVariable(StagingGroupVariable));

        [Test]
        public void It_lets_the_reader_read_the_staged_bytes()
        {
            _read.ExitCode.Should().Be(0);
            _read.Output.Should().StartWith(_backup.Sha256);
        }

        [Test]
        public void It_refuses_the_reader_an_unlink() => AssertPermissionDenied(_unlink);

        [Test]
        public void It_refuses_the_reader_a_rename() => AssertPermissionDenied(_rename);

        [Test]
        public void It_refuses_the_reader_an_overwrite() => AssertPermissionDenied(_overwrite);

        [Test]
        public void It_refuses_the_reader_a_new_entry() => AssertPermissionDenied(_create);

        [Test]
        public void It_keeps_the_staged_file_intact() =>
            _afterAttempts.Should().BeOfType<MssqlStagedFileCheck.Intact>();

        [Test]
        public void It_leaves_only_the_staged_file() =>
            _directory!.Entries().Should().Equal("dms-provision-0123456789ab.bak");

        private static void AssertPermissionDenied(ProcessResult result)
        {
            result.ExitCode.Should().NotBe(0);
            result.Error.Should().Contain("Permission denied");
        }
    }

    [TestFixture]
    public class Given_a_process_with_write_permission_replacing_the_staged_file
    {
        private MssqlBackupStagerTests.StagingDirectory? _directory;
        private ProcessResult _replace = null!;
        private MssqlStagedFileCheck _check = null!;

        [SetUp]
        public async Task Setup()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("The separate-process replacement runs a Unix shell.");
            }

            _directory = new MssqlBackupStagerTests.StagingDirectory();
            MssqlStagedBackup backup = (
                (MssqlStagingResult.Staged)
                    await _directory
                        .Stager()
                        .StageAsync(
                            _directory.Artifact,
                            MssqlBackupStagerTests.Operation(),
                            CancellationToken.None
                        )
            ).Backup;

            // The administrator's replacement: a new file renamed over the staged name, as an editor or a copy tool does.
            _replace = await RunShellAsync(
                [],
                "printf tampered > \"$1.new\" && mv -f \"$1.new\" \"$1\"",
                backup.LocalPath
            );
            _check = await MssqlBackupStager.VerifyAsync(backup, CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _directory?.Dispose();

        [Test]
        public void It_replaced_the_file() => _replace.ExitCode.Should().Be(0);

        [Test]
        public void It_finds_the_replacement_by_the_independent_re_hash() =>
            _check.Should().BeOfType<MssqlStagedFileCheck.Mismatch>();
    }
}
