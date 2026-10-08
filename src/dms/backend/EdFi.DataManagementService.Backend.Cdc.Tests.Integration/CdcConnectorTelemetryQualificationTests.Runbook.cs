// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.SchemaTools.Tests.Unit;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category("PostgresqlIntegration")]
[Category("CdcConnectorTelemetryQualification")]
[Category("DatabaseIntegration")]
public sealed class Given_PostgresqlCdcRunbookRetention
{
    [Test]
    public async Task It_executes_marked_slot_disk_and_progress_inspections_with_unavailable_actions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await using var fixture = await CdcConnectorTemplatePinnedImageFixture.StartAsync(
            CdcProvider.Postgresql,
            timeout.Token
        );
        var request = await fixture.CreateRequestAsync(timeout.Token);
        await fixture.RegisterRenderedConnectorConfigDirectlyAsync(fixture.Render(request), timeout.Token);
        await fixture.QualifyPostgresqlRunbookRetentionAsync(request, timeout.Token);
    }
}

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    private async Task<string> InspectRunbookMetricsAsync(
        string connector,
        string phase,
        bool present,
        CancellationToken token
    )
    {
        var directory = CreateInspectionDirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "metrics.private");
            var endpoint = await ControllerMetricsEndpointAsync(token);
            var inputs = new Dictionary<string, string>
            {
                ["<private-metrics-file>"] = path,
                ["<worker-metrics-url>"] = endpoint.AbsoluteUri,
            };
            var result = await CdcRunbookLiveCommands.InvokeInspectionAsync(
                "cdc-telemetry-inspect",
                inputs,
                new Dictionary<string, string>(),
                token
            );
            result.ExitCode.Should().Be(0);
            string text = await File.ReadAllTextAsync(path, token);
            CdcTelemetryQualification.Scalar(text, "jmx_scrape_error").Should().Be(0);
            CdcTelemetryQualification.HasConnector(text, connector).Should().Be(present);
            if (present)
            {
                CdcTelemetryQualification.AssertStreamingMetrics(text, Provider, connector);
            }
            await WriteInspectionEvidenceAsync(
                "cdc-telemetry-inspect",
                phase,
                new
                {
                    result.ExitCode,
                    ScrapeError = CdcTelemetryQualification.Scalar(text, "jmx_scrape_error"),
                    CurrentLagPresent = CdcTelemetryQualification.HasCurrentLag(text, connector),
                    CurrentLagMilliseconds = MetricValue(text, connector, "current"),
                    OptionalStatistics = new[] { "min", "max", "average", "p50", "p95", "p99" }.ToDictionary(
                        name => name,
                        name => MetricValue(text, connector, name)
                    ),
                    WorkerStartTimeSeconds = CdcTelemetryQualification.Scalar(
                        text,
                        "edfi_cdc_worker_start_time_seconds"
                    ),
                    WorkerHeapMaxBytes = CdcTelemetryQualification.Scalar(
                        text,
                        "edfi_cdc_worker_heap_max_bytes"
                    ),
                    Action = present ? "FreshControllerPassRequired" : "CurrentLagUnavailableNeverZero",
                },
                token
            );
            if (phase == "initial")
            {
                inputs["<worker-metrics-url>"] = new Uri(
                    _httpClient.BaseAddress!,
                    "/fixture-missing-metrics"
                ).AbsoluteUri;
                var unavailable = await CdcRunbookLiveCommands.InvokeInspectionAsync(
                    "cdc-telemetry-inspect",
                    inputs,
                    new Dictionary<string, string>(),
                    token
                );
                unavailable.ExitCode.Should().NotBe(0);
                unavailable.Error.Should().Contain("Metrics unavailable; do not use a partial file.");
                await WriteInspectionEvidenceAsync(
                    "cdc-telemetry-inspect",
                    "unavailable-endpoint",
                    new { unavailable.ExitCode, Action = "DiscardPartialFileAndCorrectEndpoint" },
                    token
                );
            }
            return text;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static double? MetricValue(string text, string connector, string statistic)
    {
        string prefix = $"edfi_cdc_source_lag_{statistic}_milliseconds{{";
        var samples = text.Split('\n')
            .Where(line =>
                line.StartsWith(prefix, StringComparison.Ordinal)
                && line.Contains($"connector=\"{connector}\"", StringComparison.Ordinal)
            )
            .ToArray();
        return samples.Length == 0
            ? null
            : double.Parse(
                samples.Single()[(samples.Single().LastIndexOf('}') + 1)..],
                CultureInfo.InvariantCulture
            );
    }

    internal async Task QualifyPostgresqlRunbookRetentionAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken token
    )
    {
        var directory = CreateInspectionDirectory();
        try
        {
            int port = await ReadMappedProviderPortAsync(token);
            string passfile = Path.Combine(directory.FullName, "pgpass.private");
            string servicefile = Path.Combine(directory.FullName, "pgservice.private");
            await File.WriteAllTextAsync(
                passfile,
                $"127.0.0.1:{port}:{PostgresqlDatabaseName}:postgres:{ConnectorDatabasePassword}\n",
                token
            );
            await File.WriteAllTextAsync(
                servicefile,
                $"[runbook]\nhost=127.0.0.1\nport={port}\ndbname={PostgresqlDatabaseName}\nuser=postgres\nconnect_timeout=5\npassfile={passfile}\n",
                token
            );
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(passfile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(servicefile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var environment = new Dictionary<string, string>
            {
                ["PGSERVICEFILE"] = servicefile,
                ["PGPASSWORD"] = "",
                ["PGOPTIONS"] = "",
                ["LC_ALL"] = "C",
            };
            var inputs = new Dictionary<string, string>
            {
                ["<pg-monitor-service>"] = "runbook",
                ["<managed-slot>"] = PostgresqlReplicationSlotName,
            };
            for (int sample = 0; sample < 2; sample++)
            {
                // Existing real provider heartbeat + committed-offset assertions establish advancing
                // source progress separately from lag, disk headroom and consumer correctness.
                await AssertHeartbeatAndCommittedOffsetProgressAsync(request, token);
                var result = await CdcRunbookLiveCommands.InvokeInspectionAsync(
                    "cdc-pg-retention-inspect",
                    inputs,
                    environment,
                    token
                );
                result.ExitCode.Should().Be(0);
                var rows = result
                    .Output.Split('\n')
                    .Select(line => line.Split('|').Select(value => value.Trim()).ToArray())
                    .Where(row => row.Length == 7)
                    .ToArray();
                rows.Should().HaveCount(2);
                rows[0]
                    .Should()
                    .Equal(
                        "observed_at",
                        "active",
                        "wal_status",
                        "retained_wal_bytes",
                        "unconfirmed_wal_bytes",
                        "remaining_slot_budget_bytes",
                        "invalidation_reason"
                    );
                var row = rows[1];
                DateTimeOffset
                    .Parse(row[0], CultureInfo.InvariantCulture)
                    .Should()
                    .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
                row[1].Should().Be("t");
                row[2].Should().BeOneOf("reserved", "extended");
                long retained = long.Parse(row[3], CultureInfo.InvariantCulture);
                long unconfirmed = long.Parse(row[4], CultureInfo.InvariantCulture);
                retained.Should().BeGreaterThanOrEqualTo(0);
                unconfirmed.Should().BeGreaterThanOrEqualTo(0);
                row[6].Should().BeEmpty();
                var settings = result
                    .Output.Split('\n')
                    .Select(line => line.Split('|').Select(value => value.Trim()).ToArray())
                    .Where(row => row.Length == 3 && row[0] != "name")
                    .ToArray();
                settings
                    .Select(row => row[0])
                    .Should()
                    .Equal("max_slot_wal_keep_size", "max_wal_size", "wal_keep_size");
                settings[0][1].Should().Be("-1");
                row[5].Should().BeEmpty("unlimited slot retention has no numeric remaining budget");
                await WriteInspectionEvidenceAsync(
                    "cdc-pg-retention-inspect",
                    $"sample-{sample}",
                    new
                    {
                        result.ExitCode,
                        ObservedAt = row[0],
                        Active = true,
                        WalStatus = row[2],
                        RetainedWalBytes = retained,
                        UnconfirmedWalBytes = unconfirmed,
                        RemainingSlotBudgetAvailable = false,
                        InvalidationReasonAvailable = false,
                        Settings = settings.Select(row => new
                        {
                            Name = row[0],
                            Setting = row[1],
                            Unit = row[2],
                        }),
                        HeartbeatAndCommittedSourceProgress = "Advanced",
                        ConsumerProgress = "NotMeasured",
                        Action = "FreshControllerContinuityRequired",
                    },
                    token
                );
            }
            inputs["<managed-slot>"] = "fixture_missing_slot";
            var missing = await CdcRunbookLiveCommands.InvokeInspectionAsync(
                "cdc-pg-retention-inspect",
                inputs,
                environment,
                token
            );
            missing.ExitCode.Should().Be(0);
            missing.Output.Should().Contain("(0 rows)");
            missing.Output.Split('\n').Count(line => line.Split('|').Length == 7).Should().Be(1);
            await WriteInspectionEvidenceAsync(
                "cdc-pg-retention-inspect",
                "missing-slot",
                new
                {
                    missing.ExitCode,
                    SlotRows = 0,
                    Action = "UnavailableHistoryIncidentNeverHealthyEmpty",
                },
                token
            );
            inputs["<pg-monitor-service>"] = "fixture_missing_service";
            var denied = await CdcRunbookLiveCommands.InvokeInspectionAsync(
                "cdc-pg-retention-inspect",
                inputs,
                environment,
                token
            );
            denied.ExitCode.Should().NotBe(0);
            denied.Error.Should().Contain("PostgreSQL retention observation unavailable.");
            await WriteInspectionEvidenceAsync(
                "cdc-pg-retention-inspect",
                "unavailable-service",
                new { denied.ExitCode, Action = "CorrectMonitoringAccessAndResample" },
                token
            );

            await QualifyRunbookDiskAsync(
                "/var/lib/postgresql/data",
                "/var/lib/postgresql/data/pg_wal",
                environment,
                token
            );
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task QualifyRunbookDiskAsync(
        string dataPath,
        string logPath,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken token
    )
    {
        var diskInputs = new Dictionary<string, string>
        {
            ["<provider-container>"] = ProviderContainerName,
            ["<data-path>"] = dataPath,
            ["<wal-or-log-path>"] = logPath,
        };
        var disk = await CdcRunbookLiveCommands.InvokeInspectionAsync(
            "cdc-provider-disk-inspect",
            diskInputs,
            environment,
            token
        );
        disk.ExitCode.Should().Be(0);
        var capacity = disk
            .Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        capacity.Should().HaveCount(2);
        var observations = capacity
            .Select(row => new
            {
                Blocks1024 = long.Parse(row[1], CultureInfo.InvariantCulture),
                Used1024 = long.Parse(row[2], CultureInfo.InvariantCulture),
                Available1024 = long.Parse(row[3], CultureInfo.InvariantCulture),
                UsedPercent = int.Parse(row[4].TrimEnd('%'), CultureInfo.InvariantCulture),
            })
            .ToArray();
        foreach (var observation in observations)
        {
            observation.Blocks1024.Should().BeGreaterThan(0);
            observation.Used1024.Should().BeGreaterThanOrEqualTo(0);
            observation.Available1024.Should().BeGreaterThan(0);
            observation.UsedPercent.Should().BeInRange(0, 100);
        }
        await WriteInspectionEvidenceAsync(
            "cdc-provider-disk-inspect",
            "capacity",
            new
            {
                disk.ExitCode,
                PathsObserved = 2,
                Capacity = observations,
                Action = "DeploymentChoosesCapacityThreshold",
            },
            token
        );
        diskInputs["<wal-or-log-path>"] = "/fixture-missing-wal-path";
        var unavailableDisk = await CdcRunbookLiveCommands.InvokeInspectionAsync(
            "cdc-provider-disk-inspect",
            diskInputs,
            environment,
            token
        );
        unavailableDisk.ExitCode.Should().NotBe(0);
        unavailableDisk.Error.Should().Contain("Provider filesystem capacity unavailable.");
        await WriteInspectionEvidenceAsync(
            "cdc-provider-disk-inspect",
            "unavailable-path",
            new { unavailableDisk.ExitCode, Action = "CapacityUnavailableStorageOwnerMustInvestigate" },
            token
        );
    }

    private static DirectoryInfo CreateInspectionDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("cdc-inspections-");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        return directory;
    }

    private async Task WriteInspectionEvidenceAsync(
        string id,
        string phase,
        object observation,
        CancellationToken token
    )
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "cdc-controller-telemetry-" + Guid.NewGuid().ToString("N") + ".json"
        );
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    TestId = TestContext.CurrentContext.Test.MethodName,
                    SnippetId = id,
                    SnippetSha256 = Convert.ToHexStringLower(
                        SHA256.HashData(Encoding.UTF8.GetBytes(CdcRunbookExamples.Read(id)))
                    ),
                    Provider = Provider.ToString(),
                    ConnectImage = _settings.ConnectImage,
                    AuthorizationProfile = "AuthorizationDisabledLocal",
                    AclIsolationProven = false,
                    Phase = phase,
                    Observation = observation,
                }
            ),
            token
        );
        TestContext.AddTestAttachment(
            path,
            "Sanitized bounded runbook inspection; no raw output or source positions"
        );
    }
}
