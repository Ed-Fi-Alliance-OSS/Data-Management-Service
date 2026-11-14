// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using JobsLogEntry = EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs.LogEntry;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// D-21: the verdict by error number, the server path joins, a probe the CMS cannot write never reaching the server,
/// a probe the routine rejected and could not delete reported and logged by name (Step 4.9), and a run that ends
/// with an exception keeping that exception while it deletes the probe file or logs it by name.
/// </summary>
public class MssqlStagingProbeTests
{
    /// <summary>Nothing listens on port 1: opening the connection fails at once.</summary>
    private static MssqlAdminConnection Unreachable() =>
        new(
            "Server=127.0.0.1,1;User Id=sa;Password=unused;Connect Timeout=1;TrustServerCertificate=true",
            TimeSpan.FromSeconds(5)
        );

    [TestFixture]
    public class Given_error_number_vectors
    {
        [TestCase(new[] { 3201, 3013 }, MssqlStagingVisibility.NotVisible)]
        [TestCase(new[] { 3201 }, MssqlStagingVisibility.NotVisible)]
        [TestCase(new[] { 3254, 3013 }, MssqlStagingVisibility.Visible)]
        [TestCase(new[] { 3241, 3013 }, MssqlStagingVisibility.Visible)]
        [TestCase(new[] { 3242, 3013 }, MssqlStagingVisibility.Visible)]
        [TestCase(new[] { 3241, 3201 }, MssqlStagingVisibility.NotVisible)]
        [TestCase(new[] { 3013 }, MssqlStagingVisibility.Inconclusive)]
        [TestCase(new[] { 3110, 3013 }, MssqlStagingVisibility.Inconclusive)]
        [TestCase(new[] { 262 }, MssqlStagingVisibility.Inconclusive)]
        [TestCase(new int[0], MssqlStagingVisibility.Inconclusive)]
        public void It_classifies_the_server_answer(int[] numbers, MssqlStagingVisibility expected) =>
            MssqlStagingProbe.Classify(numbers).Should().Be(expected);
    }

    [TestFixture]
    public class Given_server_path_vectors
    {
        [TestCase("/var/opt/dms-staging", "/var/opt/dms-staging/p.bak")]
        [TestCase("/var/opt/dms-staging//", "/var/opt/dms-staging/p.bak")]
        [TestCase("/", "/p.bak")]
        [TestCase(@"D:\Staging\", @"D:\Staging\p.bak")]
        [TestCase(@"C:\", @"C:\p.bak")]
        [TestCase(@"\\files\staging", @"\\files\staging\p.bak")]
        [TestCase("C:/Staging", @"C:/Staging\p.bak")]
        public void It_joins_in_the_servers_convention(string directory, string expected) =>
            MssqlServerPaths.Join(directory, "p.bak").Should().Be(expected);
    }

    [TestFixture]
    public class Given_a_staging_directory_the_cms_cannot_write
    {
        private MssqlStagingProbeResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            string missing = Path.Combine(
                Path.GetTempPath(),
                "dms1438-probe-missing-" + Guid.NewGuid().ToString("N")
            );
            MssqlBackupStager stager = new(
                new MssqlStagingSettings(
                    missing,
                    "/srv/staging",
                    MssqlBackupStagerTests.Mode0640,
                    1,
                    TimeSpan.FromSeconds(5)
                ),
                TimeProvider.System,
                NullLogger.Instance
            );

            // Reaching the server would throw instead of returning a verdict.
            _result = await new MssqlStagingProbe(stager, Unreachable(), NullLogger.Instance).RunAsync(
                CancellationToken.None
            );
        }

        [Test]
        public void It_reports_the_probe_not_writable() =>
            _result.Visibility.Should().Be(MssqlStagingVisibility.NotWritable);

        [Test]
        public void It_names_the_staging_step() =>
            _result.Diagnostic!.Operation.Should().Be(MssqlBackupStager.CreateStep);

        [Test]
        public void It_reports_nothing_left_behind() => _result.ProbeDeleted.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_probe_the_routine_rejects_and_cannot_delete
    {
        private MssqlBackupStagerTests.StagingDirectory? _directory;
        private FileStream? _heldOpen;
        private readonly CapturingLogger<MssqlStagingProbe> _logger = new();
        private MssqlStagingProbeResult _result = null!;
        private string[] _left = [];

        [SetUp]
        public async Task Setup()
        {
            MssqlBackupStagerTests.IgnoreIfPrivilegedOnUnix();
            MssqlBackupStagerTests.StagingDirectory directory = new(cleanupAttempts: 2);
            _directory = directory;
            _logger.Entries.Clear();

            // A byte written after the copy fails the re-hash of the zero-byte probe; the deletion is then blocked.
            MssqlBackupStager stager = directory.Stager(afterCopy: path =>
            {
                File.WriteAllBytes(path, [7]);
                if (OperatingSystem.IsWindows())
                {
                    _heldOpen = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                else
                {
                    directory.MakeUndeletable(path);
                }
            });

            // Reaching the server would throw instead of returning a verdict.
            _result = await new MssqlStagingProbe(stager, Unreachable(), _logger).RunAsync(
                CancellationToken.None
            );
            _left = directory.Entries();
        }

        [TearDown]
        public void TearDown()
        {
            _heldOpen?.Dispose();
            _directory?.Dispose();
        }

        [Test]
        public void It_reports_the_probe_not_writable() =>
            _result.Visibility.Should().Be(MssqlStagingVisibility.NotWritable);

        [Test]
        public void It_reports_the_probe_left_behind() => _result.ProbeDeleted.Should().BeFalse();

        [Test]
        public void It_logs_the_probe_file_by_its_generated_name()
        {
            JobsLogEntry entry = _logger.Entries.Should().ContainSingle().Subject;
            entry.EventId.Should().Be(MssqlStagingProbe.ProbeFileResidue);
            entry.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Error);
            entry.Field("ResourceId").Should().Be(_left.Single());
        }
    }

    [TestFixture]
    public class Given_a_staged_probe_and_a_server_that_cannot_be_reached
    {
        private MssqlBackupStagerTests.StagingDirectory _directory = null!;
        private readonly CapturingLogger<MssqlStagingProbe> _logger = new();
        private Exception? _thrown;

        [SetUp]
        public async Task Setup()
        {
            _directory = new MssqlBackupStagerTests.StagingDirectory();
            _logger.Entries.Clear();
            _thrown = null;
            try
            {
                await new MssqlStagingProbe(_directory.Stager(), Unreachable(), _logger).RunAsync(
                    CancellationToken.None
                );
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }
        }

        [TearDown]
        public void TearDown() => _directory.Dispose();

        [Test]
        public void It_propagates_the_connection_failure_unchanged() =>
            _thrown.Should().BeOfType<SqlException>();

        [Test]
        public void It_deletes_the_probe_file() => _directory.Entries().Should().BeEmpty();

        [Test]
        public void It_logs_nothing() => _logger.Entries.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_staged_probe_the_cleanup_cannot_delete_and_a_server_that_cannot_be_reached
    {
        private MssqlBackupStagerTests.StagingDirectory? _directory;
        private readonly CapturingLogger<MssqlStagingProbe> _logger = new();
        private Exception? _thrown;
        private string[] _left = [];

        [SetUp]
        public async Task Setup()
        {
            MssqlBackupStagerTests.IgnoreIfPrivilegedOnUnix();
            MssqlBackupStagerTests.StagingDirectory directory = new(cleanupAttempts: 2);
            _directory = directory;
            _logger.Entries.Clear();
            _thrown = null;

            // After the probe is written and before it is re-read: the re-hash still reads it, nothing can delete it.
            MssqlBackupStager stager = directory.Stager(afterCopy: directory.MakeUndeletable);
            try
            {
                await new MssqlStagingProbe(stager, Unreachable(), _logger).RunAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }

            _left = directory.Entries();
        }

        [TearDown]
        public void TearDown() => _directory?.Dispose();

        [Test]
        public void It_propagates_the_connection_failure_unchanged() =>
            _thrown.Should().BeOfType<SqlException>();

        [Test]
        public void It_leaves_one_probe_file() =>
            _left.Should().ContainSingle().Which.Should().MatchRegex("^dms-provision-[0-9a-f]{12}\\.bak$");

        [Test]
        public void It_logs_the_probe_file_by_its_generated_name()
        {
            JobsLogEntry entry = _logger.Entries.Should().ContainSingle().Subject;
            entry.EventId.Should().Be(MssqlStagingProbe.ProbeFileResidue);
            entry.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Error);
            entry.Field("ResourceId").Should().Be(_left.Single());
        }
    }
}
