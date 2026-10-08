// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category("MssqlIntegration")]
[Category("CdcConnectorTelemetryQualification")]
[Category("DatabaseIntegration")]
public sealed class Given_SqlServerCdcRunbookRetention
{
    [Test]
    public async Task It_executes_marked_capture_retention_version_store_and_disk_inspections_with_unavailable_actions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await using var fixture = await CdcConnectorTemplatePinnedImageFixture.StartAsync(
            CdcProvider.SqlServer,
            timeout.Token
        );
        var request = await fixture.CreateRequestAsync(timeout.Token);
        await fixture.RegisterRenderedConnectorConfigDirectlyAsync(fixture.Render(request), timeout.Token);
        await fixture.QualifySqlServerRunbookRetentionAsync(request, timeout.Token);
    }
}

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    internal async Task QualifySqlServerRunbookRetentionAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken token
    )
    {
        var environment = new Dictionary<string, string>
        {
            ["SQLCMDPASSWORD"] = ConnectorDatabasePassword,
            ["SQLCMDINI"] = "",
        };
        var inputs = new Dictionary<string, string>
        {
            ["<sqlserver-host,port>"] = $"127.0.0.1,{await ReadMappedProviderPortAsync(token)}",
            ["<target-database>"] = SqlServerDatabaseName,
            ["<monitor-login>"] = "sa",
        };
        for (int sample = 0; sample < 2; sample++)
        {
            await AssertHeartbeatAndCommittedOffsetProgressAsync(request, token);
            var result = await CdcRunbookLiveCommands.InvokeInspectionAsync(
                "cdc-sqlserver-retention-inspect",
                inputs,
                environment,
                token
            );
            result
                .ExitCode.Should()
                .Be(0, "the complete bounded batch must succeed; raw output stays private");
            var database = InspectionRows(result.Output, "observed_at").Should().ContainSingle().Subject;
            DateTime
                .Parse(database[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                .ToUniversalTime()
                .Should()
                .BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(30));
            database[1].Should().Be("1");
            database[2].Should().BeOneOf("ON", "OFF");
            database[3].Should().BeOneOf("0", "1");
            database[4].Should().BeOneOf("0", "1");
            InspectionRows(result.Output, "nested_triggers").Single()[0].Should().Be("1");
            var agent = InspectionRows(result.Output, "agent_status").Should().ContainSingle().Subject;
            agent[0].Should().Be("Running");
            var jobs = InspectionRows(result.Output, "job_type|enabled");
            jobs.Select(row => row[0]).Should().Equal("capture", "cleanup");
            jobs.Should().OnlyContain(row => row[1] == "1");
            jobs[0][2].Should().Be("1");
            var history = InspectionRows(result.Output, "job_type|run_status");
            history.Length.Should().BeInRange(1, 10);
            history.Should().OnlyContain(row => row[1] == "1", "completed fixture jobs must have succeeded");
            int.Parse(jobs[1][4], CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
            int.Parse(jobs[1][5], CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
            var scans = InspectionRows(result.Output, "session_id|start_time");
            scans.Length.Should().BeInRange(1, 5);
            scans.Should().OnlyContain(row => row[4] == "0");
            var captures = InspectionRows(result.Output, "source_table");
            captures
                .Select(row => row[0])
                .Should()
                .BeEquivalentTo("Document", "DocumentCache", "CdcHeartbeat");
            foreach (var capture in captures)
            {
                capture[1].Should().MatchRegex("^0x[0-9A-Fa-f]{20}$").And.NotBe("0x00000000000000000000");
                capture[2].Should().MatchRegex("^0x[0-9A-Fa-f]{20}$").And.NotBe("0x00000000000000000000");
                string.Compare(capture[1], capture[2], StringComparison.OrdinalIgnoreCase)
                    .Should()
                    .BeLessThanOrEqualTo(0);
            }
            var tempdbStore = InspectionRows(result.Output, "tempdb_version_store_kb");
            var persistentStore = InspectionRows(result.Output, "adr_off_row_version_store_kb");
            foreach (var row in tempdbStore.Concat(persistentStore))
            {
                long.Parse(row[0], CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(0);
            }
            var transactions = InspectionRows(result.Output, "session_id|elapsed_time_seconds");
            transactions.Length.Should().BeLessThanOrEqualTo(10);
            var tempdb = InspectionRows(result.Output, "tempdb_free_kb").Should().ContainSingle().Subject;
            long.Parse(tempdb[0], CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
            long.Parse(tempdb[1], CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(0);
            await WriteInspectionEvidenceAsync(
                "cdc-sqlserver-retention-inspect",
                $"sample-{sample}",
                new
                {
                    result.ExitCode,
                    ObservedAt = database[0],
                    CdcEnabled = true,
                    SnapshotIsolationState = database[2],
                    ReadCommittedSnapshot = database[3] == "1",
                    AcceleratedDatabaseRecovery = database[4] == "1",
                    LogReuseWait = database[5],
                    NestedTriggers = true,
                    AgentStatus = agent[0],
                    Jobs = jobs.Select(row => new
                    {
                        Type = row[0],
                        Enabled = row[1] == "1",
                        Continuous = row[2],
                        PollingIntervalSeconds = row[3],
                        RetentionMinutes = row[4],
                        Threshold = row[5],
                        Started = row[6] != "NULL",
                        Stopped = row[7] != "NULL",
                    }),
                    CompletedJobHistoryRows = history.Length,
                    CompletedJobs = history.Select(row => new
                    {
                        Type = row[0],
                        RunStatus = int.Parse(row[1], CultureInfo.InvariantCulture),
                        RunDate = row[2],
                        RunTime = row[3],
                        RunDurationHhmmss = row[4],
                    }),
                    ScanSessions = scans.Select(row => new
                    {
                        Phase = row[3],
                        Errors = int.Parse(row[4], CultureInfo.InvariantCulture),
                        LatencySeconds = row[5],
                    }),
                    CaptureCount = captures.Length,
                    RetainedRangesNonzeroAndOrdered = true,
                    TempdbVersionStoreRows = tempdbStore.Length,
                    TempdbVersionStoreKiB = tempdbStore.Select(row =>
                        long.Parse(row[0], CultureInfo.InvariantCulture)
                    ),
                    PersistentVersionStoreRows = persistentStore.Length,
                    PersistentVersionStoreKiB = persistentStore.Select(row =>
                        long.Parse(row[0], CultureInfo.InvariantCulture)
                    ),
                    ActiveSnapshotTransactions = transactions.Length,
                    TempdbFreeKiB = long.Parse(tempdb[0], CultureInfo.InvariantCulture),
                    TempdbTotalVersionStoreKiB = long.Parse(tempdb[1], CultureInfo.InvariantCulture),
                    HeartbeatAndCommittedSourceProgress = "Advanced",
                    ConsumerProgress = "NotMeasured",
                    Action = database[2] == "ON" && database[3] == "1"
                        ? "FreshControllerContinuityRequired"
                        : "E18PrerequisiteCorrectionOnlyWhileDisabled",
                    AbsentStoreAction = "AbsentRowsAreNotMeasuredZero;CorrelateAdrAndSnapshotState",
                    SchemaHistoryAction = "RetainedLsnRangeDoesNotProveSchemaHistory;UseControllerHistoryEvidence",
                },
                token
            );
        }
        inputs["<monitor-login>"] = "fixture_missing_monitor";
        var denied = await CdcRunbookLiveCommands.InvokeInspectionAsync(
            "cdc-sqlserver-retention-inspect",
            inputs,
            environment,
            token
        );
        denied.ExitCode.Should().NotBe(0);
        denied.Error.Should().Contain("SQL Server retention observation incomplete or unavailable.");
        await WriteInspectionEvidenceAsync(
            "cdc-sqlserver-retention-inspect",
            "unavailable-monitor",
            new { denied.ExitCode, Action = "CorrectMonitoringAccessAndResample;DiscardPartialBatch" },
            token
        );
        string dataPath = await ReadSqlServerScalarAsync(
            "SELECT physical_name FROM sys.database_files WHERE file_id = 1;",
            token
        );
        string logPath = await ReadSqlServerScalarAsync(
            "SELECT physical_name FROM sys.database_files WHERE type = 1;",
            token
        );
        await QualifyRunbookDiskAsync(dataPath.Trim(), logPath.Trim(), environment, token);
    }

    private static string[][] InspectionRows(string output, string firstHeader)
    {
        string[] lines = output.Split('\n').Select(line => line.Trim()).ToArray();
        int header = Array.FindIndex(
            lines,
            line => line == firstHeader || line.StartsWith(firstHeader + "|", StringComparison.Ordinal)
        );
        header.Should().BeGreaterThanOrEqualTo(0, "every documented result set must be returned");
        return lines
            .Skip(header + 2)
            .TakeWhile(line => line.Length > 0)
            .Select(line => line.Split('|').Select(value => value.Trim()).ToArray())
            .ToArray();
    }
}
