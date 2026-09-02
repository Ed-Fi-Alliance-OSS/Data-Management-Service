// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal sealed class DocumentCacheThresholdGenerationSample : IDisposable
{
    private const string CapturedAtUtc = "2026-09-01T00:00:00Z";

    public string ResultDirectory { get; } =
        Path.Combine(
            Path.GetTempPath(),
            "document-cache-threshold-generator-tests",
            Guid.NewGuid().ToString("N")
        );

    public static DocumentCacheThresholdGenerationSample Create()
    {
        DocumentCacheThresholdGenerationSample sample = new();
        sample.WriteSharedArtifacts();
        sample.WriteProviderArtifacts(PerfProvider.Postgresql);
        sample.WriteProviderArtifacts(PerfProvider.Mssql);
        return sample;
    }

    public void RemoveArtifact(string relativePath)
    {
        string path = FullPath(relativePath);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void RewriteInterruptedRestart(
        PerfProvider provider,
        double replacementElapsedMs,
        string interruptionMode = "natural-command-cancellation"
    )
    {
        string providerName = PerfProviders.ArtifactName(provider);
        WritePhase(
            providerName,
            "interrupted-rebuild-restart-from-beginning",
            elapsedMs: replacementElapsedMs + 250,
            [
                Metric("interruptionMode", interruptionMode),
                Metric("replacementElapsedMs", replacementElapsedMs, "ms"),
                Metric("restartFromBeginningCompleted", true),
            ]
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(ResultDirectory))
        {
            Directory.Delete(ResultDirectory, recursive: true);
        }
    }

    private void WriteSharedArtifacts()
    {
        Directory.CreateDirectory(ResultDirectory);
        WriteText("qualification-summary.md", "# DocumentCache Qualification Run\n");
        WriteText("query-plan-guards/guard.trx", "<TestRun />");
        WriteText(
            DocumentCacheOperatorMetricsEvidence.RelativePath,
            PerfArtifactJson.Serialize(
                DocumentCacheOperatorMetricsEvidence.CreateSample(
                    PerfProviders.ArtifactName(PerfProvider.Postgresql),
                    PerfProviders.ArtifactName(PerfProvider.Mssql)
                )
            )
        );
        WriteText("provider-metrics/postgresql-wal-vacuum-bloat.md", "# PostgreSQL metrics\n");
        WriteText("provider-metrics/mssql-log-ghost-index.md", "# SQL Server metrics\n");
    }

    private void WriteProviderArtifacts(PerfProvider provider)
    {
        string providerName = PerfProviders.ArtifactName(provider);
        foreach (
            string path in DocumentCacheQualificationRunPipeline.CommandTranscriptRelativePaths(providerName)
        )
        {
            WriteText(path, $"# {path}\n");
        }

        WritePhase(providerName, "preflight-guards", 25, []);
        WritePhase(providerName, "disabled-canonical-write-samples", 50, []);
        WritePhase(providerName, "offline-activation-first-baseline", 600_000, []);
        WritePhase(
            providerName,
            "tracking-canonical-write-overhead",
            75,
            [Metric("trackingWriteOverheadRatio", 1.05m, "ratio")]
        );
        WriteStatusPhase(providerName, "status-empty-work-latency", p95Ms: 35);
        WritePhase(providerName, "online-rebuild-clear-reseed-drain", 900_000, []);
        RewriteInterruptedRestart(provider, replacementElapsedMs: 1_200_000);
        WritePhase(
            providerName,
            "outage-distinct-document-writes",
            80,
            [
                Metric("queueDmlInsertCount", 1_000, "attempts"),
                Metric("queueDmlUpdateCount", 100, "attempts"),
                Metric("queueDmlDeleteCount", 50, "attempts"),
                Metric("queueDmlAttemptCount", 1_150, "attempts"),
                Metric("queueDmlAmplificationRatio", 1.15m, "ratio"),
            ]
        );
        WritePhase(providerName, "outage-work-row-growth", 90, [Metric("workRowGrowthRatio", 1.0m, "ratio")]);
        CopyPhaseMetricToEvidence(providerName, "outage-work-row-growth");
        WriteStatusPhase(providerName, "status-large-work-inventory-latency", p95Ms: 210);
        WritePhase(providerName, "outage-drain", 120_000, [Metric("drainElapsedMs", 120_000, "ms")]);
        CopyPhaseMetricToEvidence(providerName, "outage-drain");
        WriteStatusPhase(providerName, "status-small-work-inventory-latency", p95Ms: 80);
        WritePhase(
            providerName,
            "same-document-enqueue-ack-contention",
            100,
            [Metric("p95LockWaitMs", 125, "ms")]
        );
        WriteText(
            $"writer-contention-evidence/{providerName}-same-document-enqueue-ack-contention.json",
            File.ReadAllText(
                FullPath($"phase-metrics/{providerName}-same-document-enqueue-ack-contention.json")
            )
        );
        WritePhase(providerName, "explicit-integrity-scrub", 100, []);
        WritePhase(providerName, "post-run-final-counts", 25, []);
        WriteProviderSummary(provider, providerName);
    }

    private void WriteStatusPhase(string providerName, string phase, double p95Ms)
    {
        DocumentCacheQualificationPhaseCounts counts = Counts(providerName);
        DocumentCacheQualificationPhaseMetrics phaseMetrics = DocumentCacheQualificationPhaseMetrics.Create(
            providerName,
            phase,
            CapturedAtUtc,
            TimeSpan.FromMilliseconds(p95Ms + 10),
            counts,
            counts,
            [],
            latency: new PerfLatencySummary(
                P50Ms: p95Ms / 2,
                P95Ms: p95Ms,
                MeanMs: p95Ms / 2,
                MinMs: p95Ms / 4,
                MaxMs: p95Ms,
                SamplesMs: [p95Ms / 2, p95Ms]
            )
        );
        WriteText($"phase-metrics/{providerName}-{phase}.json", PerfArtifactJson.Serialize(phaseMetrics));
    }

    private void WritePhase(
        string providerName,
        string phase,
        double elapsedMs,
        IReadOnlyList<DocumentCacheQualificationPhaseMetricValue> metrics
    )
    {
        DocumentCacheQualificationPhaseCounts counts = Counts(providerName);
        WriteText(
            $"phase-metrics/{providerName}-{phase}.json",
            PerfArtifactJson.Serialize(
                DocumentCacheQualificationPhaseMetrics.Create(
                    providerName,
                    phase,
                    CapturedAtUtc,
                    TimeSpan.FromMilliseconds(elapsedMs),
                    counts,
                    counts,
                    metrics
                )
            )
        );
    }

    private void WriteProviderSummary(PerfProvider provider, string providerName)
    {
        DocumentCacheProviderQueryMetric queryMetric =
            provider == PerfProvider.Postgresql
                ? new(
                    "projection",
                    "provider-metrics/postgresql-projection.sql",
                    "provider-metrics/postgresql-projection.explain.json",
                    StatisticsFilePath: null,
                    SharedReadBlocks: 8_000,
                    SharedHitBlocks: 90_000,
                    LogicalReads: null,
                    PhysicalReads: null,
                    SharedReadBlocksPerProjectedDocument: 8m,
                    LogicalReadsPerProjectedDocument: null,
                    DbExecutionMs: 20,
                    DbCpuMs: null,
                    DbElapsedMs: null
                )
                : new(
                    "projection",
                    "provider-metrics/mssql-projection.sql",
                    "provider-metrics/mssql-projection.plans.json",
                    "provider-metrics/mssql-projection.stats.txt",
                    SharedReadBlocks: null,
                    SharedHitBlocks: null,
                    LogicalReads: 12_000,
                    PhysicalReads: 15,
                    SharedReadBlocksPerProjectedDocument: null,
                    LogicalReadsPerProjectedDocument: 12m,
                    DbExecutionMs: null,
                    DbCpuMs: 30,
                    DbElapsedMs: 40
                );

        WriteText(
            DocumentCacheProviderMetricSummary.RelativePath(providerName),
            PerfArtifactJson.Serialize(
                new DocumentCacheProviderMetricSummary(
                    PerfArtifactSchema.Version,
                    providerName,
                    CapturedAtUtc,
                    [
                        new DocumentCacheProviderLogMetric(
                            "offline-activation-first-baseline",
                            ProjectedDocumentCount: 500_000,
                            Bytes: 10_000_000,
                            BytesPerProjectedDocument: 20m
                        ),
                        new DocumentCacheProviderLogMetric(
                            "online-rebuild-clear-reseed-drain",
                            ProjectedDocumentCount: 500_000,
                            Bytes: 11_000_000,
                            BytesPerProjectedDocument: 22m
                        ),
                        new DocumentCacheProviderLogMetric(
                            "interrupted-rebuild-restart-from-beginning",
                            ProjectedDocumentCount: 500_000,
                            Bytes: 12_000_000,
                            BytesPerProjectedDocument: 24m
                        ),
                    ],
                    [queryMetric],
                    ProviderMaintenanceRatioPercent: 0.25m
                )
            )
        );
    }

    private void CopyPhaseMetricToEvidence(string providerName, string phase)
    {
        WriteText(
            $"outage-drain-evidence/{providerName}-{phase}.json",
            File.ReadAllText(FullPath($"phase-metrics/{providerName}-{phase}.json"))
        );
    }

    private static DocumentCacheQualificationPhaseCounts Counts(string providerName) =>
        new(
            providerName,
            SourceDocumentRows: 500_000,
            DmsDocumentRows: 500_000,
            DocumentCacheRows: 500_000,
            DocumentProjectionWorkRows: 0,
            ProjectionLifecycleState: "Tracking",
            CacheAheadRecoveryRequired: false
        );

    private static DocumentCacheQualificationPhaseMetricValue Metric(
        string name,
        object value,
        string unit = "value"
    ) =>
        new(
            name,
            value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString() ?? string.Empty,
            unit
        );

    private void WriteText(string relativePath, string content)
    {
        string path = FullPath(relativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException($"Artifact path '{relativePath}' has no directory.")
        );
        File.WriteAllText(path, content);
    }

    private string FullPath(string relativePath) =>
        Path.Combine(
            ResultDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
        );
}

[TestFixture]
public class Given_DocumentCache_Qualification_Threshold_Result_Generation
{
    [Test]
    public void It_generates_catalog_backed_rows_in_deterministic_order()
    {
        using DocumentCacheThresholdGenerationSample sample = DocumentCacheThresholdGenerationSample.Create();

        IReadOnlyList<DocumentCacheQualificationResult> rows =
            DocumentCacheQualificationThresholdResultGenerator.GenerateFromDirectory(sample.ResultDirectory);

        rows.Select(row => row.ThresholdId)
            .Should()
            .Equal(DocumentCacheQualification.OrderedThresholds().Select(threshold => threshold.Id));
        rows.Should()
            .OnlyContain(row =>
                row.Maximum
                    == DocumentCacheQualification
                        .Thresholds.Single(threshold => threshold.Id == row.ThresholdId)
                        .Maximum
                && row.Unit
                    == DocumentCacheQualification
                        .Thresholds.Single(threshold => threshold.Id == row.ThresholdId)
                        .Unit
                && row.Area
                    == DocumentCacheQualification
                        .Thresholds.Single(threshold => threshold.Id == row.ThresholdId)
                        .Area
                && row.Measurement
                    == DocumentCacheQualification
                        .Thresholds.Single(threshold => threshold.Id == row.ThresholdId)
                        .Measurement
            );
        rows.Single(row => row.ThresholdId == "postgresql-baseline-completion-minutes")
            .MeasuredValue.Should()
            .Be(10m);
        rows.Single(row => row.ThresholdId == "postgresql-shared-read-blocks-per-document")
            .MeasuredValue.Should()
            .Be(8m);
        rows.Single(row => row.ThresholdId == "postgresql-queue-dml-amplification-ratio")
            .MeasuredValue.Should()
            .Be(1.15m);
        rows.Single(row => row.ThresholdId == "postgresql-queue-dml-amplification-ratio")
            .EvidencePath.Should()
            .Be("phase-metrics/postgresql-outage-distinct-document-writes.json");
        rows.Single(row => row.ThresholdId == "mssql-logical-reads-per-document")
            .MeasuredValue.Should()
            .Be(12m);
        rows.Single(row => row.ThresholdId == "mssql-queue-dml-amplification-ratio")
            .MeasuredValue.Should()
            .Be(1.15m);
        rows.Should().OnlyContain(row => row.Passed == true);
    }

    [Test]
    public void It_writes_reloads_and_validates_threshold_results_from_disk()
    {
        using DocumentCacheThresholdGenerationSample sample = DocumentCacheThresholdGenerationSample.Create();

        DocumentCacheQualificationThresholdResultGenerator.WriteThresholdResults(sample.ResultDirectory);

        string thresholdResultsPath = Path.Combine(sample.ResultDirectory, "threshold-results.json");
        File.Exists(thresholdResultsPath).Should().BeTrue();
        File.ReadAllText(thresholdResultsPath).Should().Contain("\"measuredValue\"");
        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_rejects_generated_rows_that_point_to_missing_evidence()
    {
        using DocumentCacheThresholdGenerationSample sample = DocumentCacheThresholdGenerationSample.Create();
        sample.RemoveArtifact("outage-drain-evidence/postgresql-outage-work-row-growth.json");

        FluentActions
            .Invoking(() =>
                DocumentCacheQualificationThresholdResultGenerator.WriteThresholdResults(
                    sample.ResultDirectory
                )
            )
            .Should()
            .Throw<PerfArtifactValidationException>()
            .WithMessage("*thresholdRow.evidencePathMissing*");
    }

    [Test]
    public void It_records_durable_baseline_cursor_ticket_for_ticketed_failures()
    {
        using DocumentCacheThresholdGenerationSample sample = DocumentCacheThresholdGenerationSample.Create();
        sample.RewriteInterruptedRestart(
            PerfProvider.Postgresql,
            replacementElapsedMs: TimeSpan.FromMinutes(45).TotalMilliseconds
        );

        IReadOnlyList<DocumentCacheQualificationResult> rows =
            DocumentCacheQualificationThresholdResultGenerator.GenerateFromDirectory(
                sample.ResultDirectory,
                durableBaselineCursorTicket: "DMS-9999"
            );

        DocumentCacheQualificationResult restartRow = rows.Single(row =>
            row.ThresholdId == "postgresql-restart-from-beginning-completion-minutes"
        );
        restartRow.Passed.Should().BeFalse();
        restartRow.DurableBaselineCursorTicket.Should().Be("DMS-9999");
    }

    [Test]
    public void It_rejects_synthetic_interrupted_rebuild_evidence_for_representative_thresholds()
    {
        using DocumentCacheThresholdGenerationSample sample = DocumentCacheThresholdGenerationSample.Create();
        sample.RewriteInterruptedRestart(
            PerfProvider.Postgresql,
            replacementElapsedMs: TimeSpan.FromMinutes(20).TotalMilliseconds,
            interruptionMode: "deterministic-rebuilding-partial-progress"
        );

        FluentActions
            .Invoking(() =>
                DocumentCacheQualificationThresholdResultGenerator.GenerateFromDirectory(
                    sample.ResultDirectory
                )
            )
            .Should()
            .Throw<PerfArtifactValidationException>()
            .WithMessage(
                "*interruptionMode*deterministic-rebuilding-partial-progress*natural-command-cancellation*"
            );
    }
}
