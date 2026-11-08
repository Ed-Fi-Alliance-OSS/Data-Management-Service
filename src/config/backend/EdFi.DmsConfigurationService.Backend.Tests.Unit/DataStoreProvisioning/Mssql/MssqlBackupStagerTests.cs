// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// D-18 staging: generated name, <c>CreateNew</c>, copy hash and independent re-hash, the bounded deletion of a file
/// the routine created, the configured Unix mode under a restrictive umask, and the settings contract.
/// </summary>
public class MssqlBackupStagerTests
{
    public const UnixFileMode Mode0640 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    /// <summary>A private staging directory, a backup-shaped artifact in a sibling directory, and the stager over them.</summary>
    public sealed class StagingDirectory : IDisposable
    {
        public StagingDirectory(byte[]? artifactBytes = null, int cleanupAttempts = 3)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "dms1438-stager-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))
            );
            StagingPath = Path.Combine(Root, "staging");
            Directory.CreateDirectory(StagingPath);
            Directory.CreateDirectory(Path.Combine(Root, "artifact"));
            ArtifactBytes = artifactBytes ?? RandomNumberGenerator.GetBytes(200_000);
            string artifactPath = Path.Combine(Root, "artifact", "artifact.bak");
            File.WriteAllBytes(artifactPath, ArtifactBytes);
            Artifact = new StagedArtifact(artifactPath, Sha256Of(ArtifactBytes), DmsArtifactKind.MssqlBackup);
            Settings = new MssqlStagingSettings(
                StagingPath,
                "/var/opt/dms-staging/",
                Mode0640,
                cleanupAttempts,
                TimeSpan.FromSeconds(10)
            );
        }

        public string Root { get; }

        public string StagingPath { get; }

        public byte[] ArtifactBytes { get; }

        public StagedArtifact Artifact { get; }

        public MssqlStagingSettings Settings { get; }

        public MssqlBackupStager Stager(Action<string>? afterCopy = null) =>
            new(Settings, TimeProvider.System, NullLogger.Instance) { AfterCopy = afterCopy };

        public string[] Entries() =>
            Directory.GetFileSystemEntries(StagingPath).Select(Path.GetFileName).ToArray()!;

        public void Dispose()
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (string file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
            }
            else
            {
                File.SetUnixFileMode(
                    StagingPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }

            Directory.Delete(Root, recursive: true);
        }

        /// <summary>
        /// Makes <paramref name="path"/> impossible to delete without stopping it being read: the read-only attribute
        /// on Windows, a staging directory without write permission on Unix. <see cref="Dispose"/> undoes both.
        /// </summary>
        public void MakeUndeletable(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(StagingPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>A privileged Unix process may unlink in a directory without write permission.</summary>
    public static void IgnoreIfPrivilegedOnUnix()
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("A privileged process may unlink in a read-only directory.");
        }
    }

    public static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static DmsOperationId Operation(string value = "0123456789ab") =>
        DmsOperationId.TryParse(value, out DmsOperationId operation)
            ? operation
            : throw new InvalidOperationException("The fixed operation id did not parse.");

    [TestFixture]
    public class Given_an_artifact_staged_successfully
    {
        private StagingDirectory _directory = null!;
        private MssqlStagedBackup _backup = null!;

        [SetUp]
        public async Task Setup()
        {
            _directory = new StagingDirectory();
            MssqlStagingResult result = await _directory
                .Stager()
                .StageAsync(_directory.Artifact, Operation(), CancellationToken.None);
            _backup = ((MssqlStagingResult.Staged)result).Backup;
        }

        [TearDown]
        public void TearDown() => _directory.Dispose();

        [Test]
        public void It_names_the_file_from_the_operation_id() =>
            _backup.FileName.Should().Be("dms-provision-0123456789ab.bak");

        [Test]
        public void It_writes_into_the_staging_directory() =>
            _backup
                .LocalPath.Should()
                .Be(Path.Combine(_directory.StagingPath, "dms-provision-0123456789ab.bak"));

        [Test]
        public void It_writes_exactly_the_artifact_bytes() =>
            File.ReadAllBytes(_backup.LocalPath).Should().Equal(_directory.ArtifactBytes);

        [Test]
        public void It_joins_the_server_path_in_the_servers_convention() =>
            _backup.ServerPath.Should().Be("/var/opt/dms-staging/dms-provision-0123456789ab.bak");

        [Test]
        public void It_carries_the_manifest_hash() => _backup.Sha256.Should().Be(_directory.Artifact.Sha256);

        [Test]
        public void It_leaves_only_the_staged_file() =>
            _directory.Entries().Should().Equal("dms-provision-0123456789ab.bak");

        [Test]
        public async Task It_verifies_the_untouched_file_as_intact() =>
            (await MssqlBackupStager.VerifyAsync(_backup, CancellationToken.None))
                .Should()
                .BeOfType<MssqlStagedFileCheck.Intact>();

        [Test]
        public async Task It_deletes_the_file_on_request()
        {
            bool deleted = await _directory.Stager().DeleteAsync(_backup);
            deleted.Should().BeTrue();
            File.Exists(_backup.LocalPath).Should().BeFalse();
        }

        [Test]
        public async Task It_reports_an_already_absent_file_as_deleted()
        {
            File.Delete(_backup.LocalPath);
            (await _directory.Stager().DeleteAsync(_backup)).Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_an_existing_entry_under_the_generated_name
    {
        private StagingDirectory _directory = null!;
        private MssqlStagingResult _result = null!;
        private readonly byte[] _foreign = [1, 2, 3, 4];

        [SetUp]
        public async Task Setup()
        {
            _directory = new StagingDirectory();
            await File.WriteAllBytesAsync(
                Path.Combine(_directory.StagingPath, "dms-provision-0123456789ab.bak"),
                _foreign
            );
            _result = await _directory
                .Stager()
                .StageAsync(_directory.Artifact, Operation(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _directory.Dispose();

        [Test]
        public void It_fails_at_the_create_step() =>
            ((MssqlStagingResult.Failed)_result)
                .Diagnostic.Operation.Should()
                .Be(MssqlBackupStager.CreateStep);

        [Test]
        public void It_reports_no_residue_for_a_file_it_did_not_create() =>
            ((MssqlStagingResult.Failed)_result).Residue.Should().BeNull();

        [Test]
        public void It_leaves_the_existing_file_untouched() =>
            File.ReadAllBytes(Path.Combine(_directory.StagingPath, "dms-provision-0123456789ab.bak"))
                .Should()
                .Equal(_foreign);
    }

    [TestFixture]
    public class Given_an_artifact_that_does_not_match_its_manifest_hash
    {
        private StagingDirectory _directory = null!;
        private MssqlStagingResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _directory = new StagingDirectory();
            StagedArtifact wrongHash = _directory.Artifact with { Sha256 = Sha256Of([0]) };
            _result = await _directory.Stager().StageAsync(wrongHash, Operation(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _directory.Dispose();

        [Test]
        public void It_fails_at_the_copy_hash() =>
            ((MssqlStagingResult.Failed)_result)
                .Diagnostic.Operation.Should()
                .Be(MssqlBackupStager.CopyHashMismatchStep);

        [Test]
        public void It_deletes_the_copy() => _directory.Entries().Should().BeEmpty();

        [Test]
        public void It_reports_no_residue() => ((MssqlStagingResult.Failed)_result).Residue.Should().BeNull();
    }

    [TestFixture]
    public class Given_a_byte_flipped_between_the_copy_and_the_re_hash
    {
        private StagingDirectory _directory = null!;
        private MssqlStagingResult _result = null!;
        private bool _flipped;

        [SetUp]
        public async Task Setup()
        {
            _directory = new StagingDirectory();
            _flipped = false;
            _result = await _directory
                .Stager(afterCopy: path =>
                {
                    using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
                    stream.Position = 100_000;
                    int value = stream.ReadByte();
                    stream.Position = 100_000;
                    stream.WriteByte((byte)(value ^ 0x01));
                    _flipped = true;
                })
                .StageAsync(_directory.Artifact, Operation(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _directory.Dispose();

        [Test]
        public void It_ran_the_flip() => _flipped.Should().BeTrue();

        [Test]
        public void It_fails_at_the_re_hash() =>
            ((MssqlStagingResult.Failed)_result)
                .Diagnostic.Operation.Should()
                .Be(MssqlBackupStager.RehashMismatchStep);

        [Test]
        public void It_deletes_the_altered_file() => _directory.Entries().Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_staged_file_replaced_or_removed_afterwards
    {
        private StagingDirectory _directory = null!;
        private MssqlStagedFileCheck _replaced = null!;
        private MssqlStagedFileCheck _rewritten = null!;
        private MssqlStagedFileCheck _removed = null!;

        [SetUp]
        public async Task Setup()
        {
            _directory = new StagingDirectory();
            MssqlBackupStager stager = _directory.Stager();

            MssqlStagedBackup replaced = await StageAsync(stager, "0123456789aa");
            string sibling = replaced.LocalPath + ".new";
            await File.WriteAllBytesAsync(sibling, [9, 9, 9]);
            File.Move(sibling, replaced.LocalPath, overwrite: true);
            _replaced = await MssqlBackupStager.VerifyAsync(replaced, CancellationToken.None);

            MssqlStagedBackup rewritten = await StageAsync(stager, "0123456789bb");
            byte[] same = await File.ReadAllBytesAsync(rewritten.LocalPath);
            same[^1] ^= 0x80;
            await File.WriteAllBytesAsync(rewritten.LocalPath, same);
            _rewritten = await MssqlBackupStager.VerifyAsync(rewritten, CancellationToken.None);

            MssqlStagedBackup removed = await StageAsync(stager, "0123456789cc");
            File.Delete(removed.LocalPath);
            _removed = await MssqlBackupStager.VerifyAsync(removed, CancellationToken.None);
        }

        private async Task<MssqlStagedBackup> StageAsync(MssqlBackupStager stager, string operation) =>
            (
                (MssqlStagingResult.Staged)
                    await stager.StageAsync(_directory.Artifact, Operation(operation), CancellationToken.None)
            ).Backup;

        [TearDown]
        public void TearDown() => _directory.Dispose();

        [Test]
        public void It_finds_a_replaced_file_mismatched() =>
            _replaced.Should().BeOfType<MssqlStagedFileCheck.Mismatch>();

        [Test]
        public void It_finds_a_rewritten_file_mismatched() =>
            _rewritten.Should().BeOfType<MssqlStagedFileCheck.Mismatch>();

        [Test]
        public void It_finds_a_removed_file_unreadable() =>
            ((MssqlStagedFileCheck.Unreadable)_removed)
                .Diagnostic.Operation.Should()
                .Be(MssqlBackupStager.RehashStep);
    }

    [TestFixture]
    public class Given_a_staged_file_the_deletion_cannot_remove
    {
        private StagingDirectory? _directory;
        private MssqlStagingResult _result = null!;
        private FileStream? _heldOpen;

        [SetUp]
        public async Task Setup()
        {
            if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
            {
                Assert.Ignore("A privileged process may unlink in a read-only directory.");
            }

            _directory = new StagingDirectory(cleanupAttempts: 2);
            _result = await _directory
                .Stager(afterCopy: path =>
                {
                    File.WriteAllBytes(path, [7]);
                    if (OperatingSystem.IsWindows())
                    {
                        // An open handle without delete sharing blocks the deletion on Windows.
                        _heldOpen = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    else
                    {
                        // A directory without write permission blocks the unlink on Unix.
                        File.SetUnixFileMode(
                            _directory!.StagingPath,
                            UnixFileMode.UserRead | UnixFileMode.UserExecute
                        );
                    }
                })
                .StageAsync(_directory.Artifact, Operation(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown()
        {
            _heldOpen?.Dispose();
            _directory?.Dispose();
        }

        [Test]
        public void It_fails_at_the_re_hash() =>
            ((MssqlStagingResult.Failed)_result)
                .Diagnostic.Operation.Should()
                .Be(MssqlBackupStager.RehashMismatchStep);

        [Test]
        public void It_reports_the_staged_file_by_its_generated_name() =>
            ((MssqlStagingResult.Failed)_result)
                .Residue.Should()
                .Be(new DmsCleanupResidue(DmsResidueKind.StagedBackup, "dms-provision-0123456789ab.bak"));
    }

    [TestFixture]
    public class Given_the_mode_cannot_be_applied_to_the_new_file
    {
        private StagingDirectory _directory = null!;
        private MssqlStagingResult _result = null!;
        private bool _modeAttempted;

        [SetUp]
        public async Task Setup()
        {
            _directory = new StagingDirectory();
            _modeAttempted = false;
            MssqlBackupStager stager = new(_directory.Settings, TimeProvider.System, NullLogger.Instance)
            {
                ApplyModeOverride = _ =>
                {
                    _modeAttempted = true;
                    throw new UnauthorizedAccessException("The mode change was refused.");
                },
            };
            _result = await stager.StageAsync(_directory.Artifact, Operation(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _directory.Dispose();

        [Test]
        public void It_attempted_the_mode() => _modeAttempted.Should().BeTrue();

        [Test]
        public void It_fails_at_the_mode_step() =>
            ((MssqlStagingResult.Failed)_result)
                .Diagnostic.Operation.Should()
                .Be(MssqlBackupStager.ApplyModeStep);

        [Test]
        public void It_deletes_the_file_it_created() => _directory.Entries().Should().BeEmpty();

        [Test]
        public void It_reports_no_residue() => ((MssqlStagingResult.Failed)_result).Residue.Should().BeNull();
    }

    [TestFixture]
    public class Given_the_mode_cannot_be_applied_and_the_new_file_cannot_be_deleted
    {
        private StagingDirectory? _directory;
        private MssqlStagingResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            IgnoreIfPrivilegedOnUnix();
            StagingDirectory directory = new(cleanupAttempts: 2);
            _directory = directory;
            MssqlBackupStager stager = new(directory.Settings, TimeProvider.System, NullLogger.Instance)
            {
                ApplyModeOverride = stream =>
                {
                    // Both failures at once: the file cannot be deleted, then the mode change is refused.
                    directory.MakeUndeletable(stream.Name);
                    throw new UnauthorizedAccessException("The mode change was refused.");
                },
            };
            _result = await stager.StageAsync(directory.Artifact, Operation(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _directory?.Dispose();

        [Test]
        public void It_fails_at_the_mode_step() =>
            ((MssqlStagingResult.Failed)_result)
                .Diagnostic.Operation.Should()
                .Be(MssqlBackupStager.ApplyModeStep);

        [Test]
        public void It_reports_the_file_it_owns_by_its_generated_name() =>
            ((MssqlStagingResult.Failed)_result)
                .Residue.Should()
                .Be(new DmsCleanupResidue(DmsResidueKind.StagedBackup, "dms-provision-0123456789ab.bak"));

        [Test]
        public void It_leaves_the_reported_file_in_place() =>
            _directory!.Entries().Should().Equal("dms-provision-0123456789ab.bak");
    }

    [TestFixture]
    public class Given_a_cancelled_staging
    {
        private StagingDirectory _directory = null!;
        private Func<Task> _stage = null!;

        [SetUp]
        public void Setup()
        {
            _directory = new StagingDirectory();
            CancellationToken cancelled = new(canceled: true);
            _stage = () => _directory.Stager().StageAsync(_directory.Artifact, Operation(), cancelled);
        }

        [TearDown]
        public void TearDown() => _directory.Dispose();

        [Test]
        public async Task It_propagates_the_cancellation_and_deletes_the_file_it_created()
        {
            await _stage.Should().ThrowAsync<OperationCanceledException>();
            _directory.Entries().Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_missing_artifact_or_staging_directory
    {
        private StagingDirectory _directory = null!;
        private MssqlStagingResult _missingArtifact = null!;
        private MssqlStagingResult _missingDirectory = null!;

        [SetUp]
        public async Task Setup()
        {
            _directory = new StagingDirectory();
            _missingArtifact = await _directory
                .Stager()
                .StageAsync(
                    _directory.Artifact with
                    {
                        FilePath = _directory.Artifact.FilePath + ".absent",
                    },
                    Operation(),
                    CancellationToken.None
                );
            Directory.Delete(_directory.StagingPath);
            _missingDirectory = await _directory
                .Stager()
                .StageAsync(_directory.Artifact, Operation(), CancellationToken.None);
            Directory.CreateDirectory(_directory.StagingPath);
        }

        [TearDown]
        public void TearDown() => _directory.Dispose();

        [Test]
        public void It_fails_at_opening_the_source() =>
            ((MssqlStagingResult.Failed)_missingArtifact)
                .Diagnostic.Operation.Should()
                .Be(MssqlBackupStager.OpenSourceStep);

        [Test]
        public void It_fails_at_the_create_step_without_a_directory() =>
            ((MssqlStagingResult.Failed)_missingDirectory)
                .Diagnostic.Operation.Should()
                .Be(MssqlBackupStager.CreateStep);

        [Test]
        public void It_names_only_the_exception_type() =>
            ((MssqlStagingResult.Failed)_missingDirectory)
                .Diagnostic.ExceptionTypeChain.Should()
                .Be(typeof(DirectoryNotFoundException).FullName);
    }

    [TestFixture]
    public class Given_a_restrictive_umask
    {
        private UnixFileMode _staged0640;
        private UnixFileMode _staged0600;

        [SetUp]
        public async Task Setup()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix modes apply on Unix only.");
                return;
            }

            uint previous = SetUmask(Convert.ToUInt32("077", 8));
            try
            {
                _staged0640 = await StageWithModeAsync(Mode0640);
                _staged0600 = await StageWithModeAsync(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            finally
            {
                SetUmask(previous);
            }
        }

        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private static async Task<UnixFileMode> StageWithModeAsync(UnixFileMode mode)
        {
            using StagingDirectory directory = new();
            MssqlStagingSettings settings = new(
                directory.StagingPath,
                "/srv/staging",
                mode,
                3,
                TimeSpan.FromSeconds(10)
            );
            MssqlStagedBackup backup = (
                (MssqlStagingResult.Staged)
                    await new MssqlBackupStager(
                        settings,
                        TimeProvider.System,
                        NullLogger.Instance
                    ).StageAsync(directory.Artifact, Operation(), CancellationToken.None)
            ).Backup;
            return File.GetUnixFileMode(backup.LocalPath);
        }

        [Test]
        public void It_applies_the_configured_mode_despite_the_umask() => _staged0640.Should().Be(Mode0640);

        [Test]
        public void It_applies_a_narrower_configured_mode_exactly() =>
            _staged0600.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        [DllImport("libc", EntryPoint = "umask")]
        private static extern uint SetUmask(uint mask);
    }

    [TestFixture]
    public class Given_settings_vectors
    {
        private static readonly string _local = Path.GetTempPath();

        private static Action Settings(
            string stagingPath = "",
            string serverPath = "/srv/staging",
            UnixFileMode mode = Mode0640,
            int attempts = 3,
            double timeoutSeconds = 10
        ) =>
            () =>
                _ = new MssqlStagingSettings(
                    stagingPath.Length == 0 ? _local : stagingPath,
                    serverPath,
                    mode,
                    attempts,
                    TimeSpan.FromSeconds(timeoutSeconds)
                );

        [Test]
        public void It_accepts_a_unix_server_path() => Settings().Should().NotThrow();

        [Test]
        public void It_accepts_a_windows_drive_server_path() =>
            Settings(serverPath: @"D:\Staging").Should().NotThrow();

        [Test]
        public void It_accepts_a_unc_server_path() =>
            Settings(serverPath: @"\\files\staging").Should().NotThrow();

        [Test]
        public void It_refuses_a_relative_server_path() =>
            Settings(serverPath: "staging/dir").Should().Throw<ArgumentException>();

        [Test]
        public void It_refuses_a_relative_staging_path() =>
            Settings(stagingPath: "staging").Should().Throw<ArgumentException>();

        [Test]
        public void It_refuses_group_write() =>
            Settings(mode: Mode0640 | UnixFileMode.GroupWrite).Should().Throw<ArgumentException>();

        [Test]
        public void It_refuses_other_write() =>
            Settings(mode: Mode0640 | UnixFileMode.OtherWrite).Should().Throw<ArgumentException>();

        [Test]
        public void It_refuses_zero_cleanup_attempts() =>
            Settings(attempts: 0).Should().Throw<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_eleven_cleanup_attempts() =>
            Settings(attempts: 11).Should().Throw<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_a_zero_cleanup_bound() =>
            Settings(timeoutSeconds: 0).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestFixture]
    public class Given_file_mode_text
    {
        [Test]
        public void It_parses_four_octal_digits() =>
            MssqlStagingSettings.ParseFileMode("0640").Should().Be(Mode0640);

        [Test]
        public void It_parses_three_octal_digits() =>
            MssqlStagingSettings
                .ParseFileMode("600")
                .Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        [Test]
        public void It_parses_a_setgid_digit() =>
            MssqlStagingSettings.ParseFileMode("2640").Should().Be(Mode0640 | UnixFileMode.SetGroup);

        [TestCase("0648")]
        [TestCase("06400")]
        [TestCase("64")]
        [TestCase("")]
        [TestCase(" 640")]
        [TestCase("0x1a0")]
        public void It_refuses_anything_but_three_or_four_octal_digits(string text) =>
            FluentActions
                .Invoking(() => MssqlStagingSettings.ParseFileMode(text))
                .Should()
                .Throw<FormatException>();
    }
}
