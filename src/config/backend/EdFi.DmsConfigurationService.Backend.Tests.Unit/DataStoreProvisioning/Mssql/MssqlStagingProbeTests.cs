// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>D-21: the verdict by error number, the server path joins, and a probe the CMS cannot write never reaching the server.</summary>
public class MssqlStagingProbeTests
{
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

            // Nothing listens on port 1: reaching the server would throw instead of returning a verdict.
            MssqlAdminConnection unreachable = new(
                "Server=127.0.0.1,1;User Id=sa;Password=unused;Connect Timeout=1;TrustServerCertificate=true",
                TimeSpan.FromSeconds(5)
            );
            _result = await new MssqlStagingProbe(stager, unreachable).RunAsync(CancellationToken.None);
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
}
