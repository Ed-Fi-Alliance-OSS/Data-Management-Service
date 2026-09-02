// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Builds threshold-results.json from the measured DocumentCache qualification artifacts.
/// </summary>
public static class DocumentCacheQualificationThresholdResultGenerator
{
    private const string OfflineActivationPhase = "offline-activation-first-baseline";
    private const string OnlineRebuildPhase = "online-rebuild-clear-reseed-drain";
    private const string InterruptedRebuildPhase = "interrupted-rebuild-restart-from-beginning";
    private const string NaturalCommandCancellationInterruptionMode = "natural-command-cancellation";
    private const string TrackingWriteOverheadPhase = "tracking-canonical-write-overhead";
    private const string StatusEmptyWorkPhase = "status-empty-work-latency";
    private const string StatusLargeWorkPhase = "status-large-work-inventory-latency";
    private const string StatusSmallWorkPhase = "status-small-work-inventory-latency";
    private const string OutageDistinctDocumentWritesPhase = "outage-distinct-document-writes";
    private const string OutageWorkRowGrowthPhase = "outage-work-row-growth";
    private const string OutageDrainPhase = "outage-drain";
    private const string SameDocumentContentionPhase = "same-document-enqueue-ack-contention";

    private static readonly IReadOnlySet<string> _ticketRequiredOnFailureAreas = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "restartFromBeginning",
        "databaseCpu",
        "databaseLog",
        "queueDmlAmplification",
    };

    private static readonly IReadOnlySet<string> _baselineLogPhases = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        OfflineActivationPhase,
        OnlineRebuildPhase,
        InterruptedRebuildPhase,
    };

    public static IReadOnlyList<DocumentCacheQualificationResult> GenerateFromDirectory(
        string resultsDirectory,
        string? durableBaselineCursorTicket = null
    )
    {
        string root = RequireExistingDirectory(resultsDirectory);
        Dictionary<PerfProvider, ProviderThresholdInputs> inputsByProvider = DocumentCacheQualification
            .OrderedThresholds()
            .Select(threshold => threshold.Provider)
            .Distinct()
            .ToDictionary(provider => provider, provider => ProviderThresholdInputs.Load(root, provider));

        return
        [
            .. DocumentCacheQualification
                .OrderedThresholds()
                .Select(threshold =>
                    CreateRow(threshold, inputsByProvider[threshold.Provider], durableBaselineCursorTicket)
                ),
        ];
    }

    public static void WriteThresholdResults(
        string resultsDirectory,
        string? durableBaselineCursorTicket = null
    )
    {
        IReadOnlyList<DocumentCacheQualificationResult> rows = GenerateFromDirectory(
            resultsDirectory,
            durableBaselineCursorTicket
        );
        DocumentCacheQualificationArtifactWriter.WriteThresholdResults(resultsDirectory, rows);
        DocumentCacheQualificationArtifactValidator.EnsureValidDirectory(resultsDirectory);
    }

    public static void AssembleProviderEvidenceAndWriteThresholdResults(
        string resultsDirectory,
        string? durableBaselineCursorTicket = null
    )
    {
        string root = RequireExistingDirectory(resultsDirectory);

        foreach (
            PerfProvider provider in DocumentCacheQualification
                .OrderedThresholds()
                .Select(threshold => threshold.Provider)
                .Distinct()
        )
        {
            string providerRunDirectory = LatestProviderRunDirectory(root, provider);
            MergeDirectory(providerRunDirectory, root, "phase-metrics");
            MergeDirectory(providerRunDirectory, root, "command-transcripts");
            MergeDirectory(providerRunDirectory, root, "provider-metrics");
            MergeDirectory(providerRunDirectory, root, "outage-drain-evidence");
            MergeDirectory(providerRunDirectory, root, "writer-contention-evidence");
        }

        WriteThresholdResults(root, durableBaselineCursorTicket);
    }

    private static DocumentCacheQualificationResult CreateRow(
        DocumentCacheQualificationThreshold threshold,
        ProviderThresholdInputs inputs,
        string? durableBaselineCursorTicket
    )
    {
        ThresholdMeasurement measurement = Measure(threshold, inputs);
        bool passed = measurement.PassedOverride ?? measurement.MeasuredValue <= threshold.Maximum;
        string? ticket =
            !passed
            && _ticketRequiredOnFailureAreas.Contains(threshold.Area)
            && !string.IsNullOrWhiteSpace(durableBaselineCursorTicket)
                ? durableBaselineCursorTicket.Trim()
                : null;

        return new DocumentCacheQualificationResult(
            PerfProviders.ArtifactName(threshold.Provider),
            threshold.Id,
            threshold.Area,
            threshold.Measurement,
            measurement.MeasuredValue,
            threshold.Maximum,
            threshold.Unit,
            passed,
            measurement.EvidencePath,
            measurement.ReviewerNote,
            ticket
        );
    }

    private static ThresholdMeasurement Measure(
        DocumentCacheQualificationThreshold threshold,
        ProviderThresholdInputs inputs
    ) =>
        threshold.Area switch
        {
            "baselineCompletion" => PhaseElapsedMinutes(inputs, OfflineActivationPhase),
            "rebuildCompletion" => PhaseElapsedMinutes(inputs, OnlineRebuildPhase),
            "restartFromBeginning" => RestartFromBeginningMinutes(inputs),
            "databaseCpu" => OperatorCpuPercent(inputs),
            "databaseIo" => ProviderReadCost(inputs, threshold.Provider),
            "databaseLog" => ProviderLogBytesPerDocument(inputs),
            "queueDmlAmplification" => QueueDmlAmplification(inputs),
            "statusOldestWorkLatency" => StatusP95Latency(inputs),
            "canonicalWriteOverhead" => PhaseMetric(
                inputs,
                TrackingWriteOverheadPhase,
                "trackingWriteOverheadRatio",
                "Tracking canonical write p95 divided by disabled canonical write p95."
            ),
            "outageQueueGrowth" => PhaseMetric(
                inputs,
                OutageWorkRowGrowthPhase,
                "workRowGrowthRatio",
                "Observed DocumentProjectionWork rows divided by distinct outage-touched documents."
            ),
            "outageDrain" => PhaseMetricMinutes(
                inputs,
                OutageDrainPhase,
                "drainElapsedMs",
                "Administrative drain elapsed time for the outage backlog."
            ),
            "sameDocumentLockWait" => PhaseMetric(
                inputs,
                SameDocumentContentionPhase,
                "p95LockWaitMs",
                "HTTP write p95 while same-document enqueue contenders raced work acknowledgement."
            ),
            "providerMaintenance" => ProviderMaintenanceRatio(inputs, threshold.Provider),
            _ => throw new PerfArtifactValidationException([
                $"No DocumentCache threshold generator mapping exists for area '{threshold.Area}'.",
            ]),
        };

    private static ThresholdMeasurement PhaseElapsedMinutes(ProviderThresholdInputs inputs, string phase)
    {
        ThresholdPhaseMetrics metrics = inputs.Phase(phase);
        decimal elapsedMs = DecimalFromDouble(
            metrics.ElapsedMilliseconds,
            PhasePath(inputs.ProviderName, phase)
        );
        return new ThresholdMeasurement(
            elapsedMs / 60_000m,
            PhasePath(inputs.ProviderName, phase),
            $"Measured phase elapsed time from `{PhasePath(inputs.ProviderName, phase)}`."
        );
    }

    private static ThresholdMeasurement RestartFromBeginningMinutes(ProviderThresholdInputs inputs)
    {
        ThresholdPhaseMetrics metrics = inputs.Phase(InterruptedRebuildPhase);
        string interruptionMode = MetricText(metrics, "interruptionMode");
        if (
            !string.Equals(
                interruptionMode,
                NaturalCommandCancellationInterruptionMode,
                StringComparison.Ordinal
            )
        )
        {
            throw new PerfArtifactValidationException([
                $"DocumentCache qualification artifact '{PhasePath(inputs.ProviderName, InterruptedRebuildPhase)}' "
                    + $"has interruptionMode '{interruptionMode}'. Representative restart-from-beginning "
                    + $"threshold evidence requires '{NaturalCommandCancellationInterruptionMode}'.",
            ]);
        }

        decimal replacementElapsedMs = MetricDecimal(metrics, "replacementElapsedMs");
        bool completed = MetricBool(metrics, "restartFromBeginningCompleted");
        return new ThresholdMeasurement(
            replacementElapsedMs / 60_000m,
            PhasePath(inputs.ProviderName, InterruptedRebuildPhase),
            "Measured replacement rebuild elapsed time after the interrupted Rebuilding state was observed.",
            completed ? null : false
        );
    }

    private static ThresholdMeasurement OperatorCpuPercent(ProviderThresholdInputs inputs)
    {
        DocumentCacheOperatorProviderMetrics providerMetrics = inputs.OperatorMetrics.MetricsFor(
            inputs.ProviderName
        );
        return new ThresholdMeasurement(
            providerMetrics.AverageDatabaseCpuPercent
                ?? throw MissingMetric(
                    DocumentCacheOperatorMetricsEvidence.RelativePath,
                    "averageDatabaseCpuPercent"
                ),
            DocumentCacheOperatorMetricsEvidence.RelativePath,
            "Measured average database CPU from strict operator-supplied CPU/IO evidence."
        );
    }

    private static ThresholdMeasurement ProviderReadCost(
        ProviderThresholdInputs inputs,
        PerfProvider provider
    )
    {
        DocumentCacheProviderQueryMetric projectionSample =
            inputs.ProviderSummary.QuerySamples.SingleOrDefault(sample => sample.Name == "projection")
            ?? throw MissingMetric(inputs.ProviderSummaryPath, "projection query sample");

        decimal measuredValue =
            provider == PerfProvider.Postgresql
                ? projectionSample.SharedReadBlocksPerProjectedDocument
                    ?? throw MissingMetric(inputs.ProviderSummaryPath, "sharedReadBlocksPerProjectedDocument")
                : projectionSample.LogicalReadsPerProjectedDocument
                    ?? throw MissingMetric(inputs.ProviderSummaryPath, "logicalReadsPerProjectedDocument");

        return new ThresholdMeasurement(
            measuredValue,
            inputs.ProviderSummaryPath,
            "Measured projection query read cost from provider metric capture."
        );
    }

    private static ThresholdMeasurement ProviderLogBytesPerDocument(ProviderThresholdInputs inputs)
    {
        DocumentCacheProviderLogMetric metric =
            inputs
                .ProviderSummary.LogPhases.Where(phase =>
                    _baselineLogPhases.Contains(phase.Phase) && phase.BytesPerProjectedDocument is not null
                )
                .OrderByDescending(phase => phase.BytesPerProjectedDocument)
                .FirstOrDefault()
            ?? throw MissingMetric(inputs.ProviderSummaryPath, "baseline/rebuild bytesPerProjectedDocument");

        return new ThresholdMeasurement(
            metric.BytesPerProjectedDocument!.Value,
            inputs.ProviderSummaryPath,
            $"Measured maximum provider log growth per projected document from `{metric.Phase}`."
        );
    }

    private static ThresholdMeasurement QueueDmlAmplification(ProviderThresholdInputs inputs) =>
        PhaseMetric(
            inputs,
            OutageDistinctDocumentWritesPhase,
            "queueDmlAmplificationRatio",
            "Measured DocumentProjectionWork insert/update/delete counter deltas per distinct outage-touched document."
        );

    private static ThresholdMeasurement StatusP95Latency(ProviderThresholdInputs inputs)
    {
        ThresholdMeasurement[] measurements =
        [
            PhaseLatency(inputs, StatusEmptyWorkPhase),
            PhaseLatency(inputs, StatusLargeWorkPhase),
            PhaseLatency(inputs, StatusSmallWorkPhase),
        ];
        ThresholdMeasurement slowest = measurements
            .OrderByDescending(measurement => measurement.MeasuredValue)
            .First();

        return slowest with
        {
            ReviewerNote = "Measured maximum p95 across empty, large, and small status/oldest-work samples.",
        };
    }

    private static ThresholdMeasurement ProviderMaintenanceRatio(
        ProviderThresholdInputs inputs,
        PerfProvider provider
    )
    {
        string metricName =
            provider == PerfProvider.Postgresql
                ? "dead tuple ratio after VACUUM"
                : "ghost row ratio after index maintenance";
        return new ThresholdMeasurement(
            inputs.ProviderSummary.ProviderMaintenanceRatioPercent,
            inputs.ProviderSummaryPath,
            $"Measured {metricName} from provider maintenance capture."
        );
    }

    private static ThresholdMeasurement PhaseMetric(
        ProviderThresholdInputs inputs,
        string phase,
        string metricName,
        string reviewerNote
    ) =>
        new(
            MetricDecimal(inputs.Phase(phase), metricName),
            PhasePath(inputs.ProviderName, phase),
            reviewerNote
        );

    private static ThresholdMeasurement PhaseMetricMinutes(
        ProviderThresholdInputs inputs,
        string phase,
        string metricName,
        string reviewerNote
    ) =>
        PhaseMetric(inputs, phase, metricName, reviewerNote) with
        {
            MeasuredValue = MetricDecimal(inputs.Phase(phase), metricName) / 60_000m,
        };

    private static ThresholdMeasurement PhaseLatency(ProviderThresholdInputs inputs, string phase)
    {
        ThresholdPhaseMetrics metrics = inputs.Phase(phase);
        double p95 =
            metrics.Latency?.P95Ms ?? throw MissingMetric(PhasePath(inputs.ProviderName, phase), "p95");
        return new ThresholdMeasurement(
            DecimalFromDouble(p95, PhasePath(inputs.ProviderName, phase)),
            PhasePath(inputs.ProviderName, phase),
            $"Measured status p95 latency from `{phase}`."
        );
    }

    private static decimal MetricDecimal(ThresholdPhaseMetrics metrics, string metricName)
    {
        DocumentCacheQualificationPhaseMetricValue metric =
            metrics.Metrics.SingleOrDefault(metric => metric.Name == metricName)
            ?? throw MissingMetric(PhasePath(metrics.Provider, metrics.Phase), metricName);

        if (
            !decimal.TryParse(
                metric.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal parsed
            )
        )
        {
            throw new PerfArtifactValidationException([
                $"DocumentCache phase metric '{metricName}' in '{PhasePath(metrics.Provider, metrics.Phase)}' "
                    + $"is not a decimal value: '{metric.Value}'.",
            ]);
        }

        return parsed;
    }

    private static string MetricText(ThresholdPhaseMetrics metrics, string metricName)
    {
        DocumentCacheQualificationPhaseMetricValue metric =
            metrics.Metrics.SingleOrDefault(metric => metric.Name == metricName)
            ?? throw MissingMetric(PhasePath(metrics.Provider, metrics.Phase), metricName);

        return metric.Value;
    }

    private static bool MetricBool(ThresholdPhaseMetrics metrics, string metricName)
    {
        DocumentCacheQualificationPhaseMetricValue metric =
            metrics.Metrics.SingleOrDefault(metric => metric.Name == metricName)
            ?? throw MissingMetric(PhasePath(metrics.Provider, metrics.Phase), metricName);

        if (bool.TryParse(metric.Value, out bool parsed))
        {
            return parsed;
        }

        throw new PerfArtifactValidationException([
            $"DocumentCache phase metric '{metricName}' in '{PhasePath(metrics.Provider, metrics.Phase)}' "
                + $"is not a boolean value: '{metric.Value}'.",
        ]);
    }

    private static decimal DecimalFromDouble(double value, string artifactPath)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new PerfArtifactValidationException([
                $"DocumentCache artifact '{artifactPath}' contains a non-finite measured value.",
            ]);
        }

        return decimal.Parse(
            value.ToString("G17", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture
        );
    }

    private static string LatestProviderRunDirectory(string root, PerfProvider provider)
    {
        string providerName = PerfProviders.ArtifactName(provider);
        IReadOnlyList<string> candidates =
        [
            .. Directory
                .EnumerateDirectories(
                    root,
                    $"{providerName}-*-document-cache-*",
                    SearchOption.TopDirectoryOnly
                )
                .OrderBy(path => path, StringComparer.Ordinal),
        ];

        if (candidates.Count == 0)
        {
            throw new PerfArtifactValidationException([
                $"DocumentCache qualification result directory '{root}' does not contain a nested {providerName} provider run.",
            ]);
        }

        return candidates[^1];
    }

    private static void MergeDirectory(string sourceRoot, string targetRoot, string relativeDirectory)
    {
        string source = Path.Combine(sourceRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceRoot, sourceFile)
                .Replace(Path.DirectorySeparatorChar, '/');
            string targetFile = Path.Combine(
                targetRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            );
            Directory.CreateDirectory(
                Path.GetDirectoryName(targetFile)
                    ?? throw new PerfArtifactValidationException([
                        $"DocumentCache qualification artifact path '{relativePath}' has no directory.",
                    ])
            );
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private static T ReadJsonArtifact<T>(string root, string relativePath)
    {
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            throw new PerfArtifactValidationException([
                $"DocumentCache qualification artifact '{relativePath}' is required but was not found.",
            ]);
        }

        try
        {
            return PerfArtifactJson.Deserialize<T>(File.ReadAllText(fullPath));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new PerfArtifactValidationException([
                $"DocumentCache qualification artifact '{relativePath}' could not be deserialized: {ex.Message}",
            ]);
        }
    }

    private static PerfArtifactValidationException MissingMetric(string artifactPath, string metricName) =>
        new([$"DocumentCache qualification artifact '{artifactPath}' is missing metric '{metricName}'."]);

    private static string RequireExistingDirectory(string resultsDirectory)
    {
        if (string.IsNullOrWhiteSpace(resultsDirectory))
        {
            throw new PerfArtifactValidationException([
                "DocumentCache qualification result directory is required.",
            ]);
        }

        string root = Path.GetFullPath(resultsDirectory);
        if (!Directory.Exists(root))
        {
            throw new PerfArtifactValidationException([
                $"DocumentCache qualification result directory '{root}' does not exist.",
            ]);
        }

        return root;
    }

    private static string PhasePath(string providerName, string phase) =>
        $"phase-metrics/{providerName}-{phase}.json";

    private sealed record ProviderThresholdInputs(
        string Root,
        PerfProvider Provider,
        string ProviderName,
        DocumentCacheOperatorMetricsEvidence OperatorMetrics,
        DocumentCacheProviderMetricSummary ProviderSummary,
        string ProviderSummaryPath
    )
    {
        public static ProviderThresholdInputs Load(string root, PerfProvider provider)
        {
            string providerName = PerfProviders.ArtifactName(provider);
            string providerSummaryPath = DocumentCacheProviderMetricSummary.RelativePath(providerName);
            DocumentCacheProviderMetricSummary providerSummary =
                ReadJsonArtifact<DocumentCacheProviderMetricSummary>(root, providerSummaryPath);
            if (providerSummary.SchemaVersion != PerfArtifactSchema.Version)
            {
                throw new PerfArtifactValidationException([
                    $"DocumentCache provider metric summary '{providerSummaryPath}' has schemaVersion "
                        + $"'{providerSummary.SchemaVersion}', expected '{PerfArtifactSchema.Version}'.",
                ]);
            }

            if (providerSummary.Provider != providerName)
            {
                throw new PerfArtifactValidationException([
                    $"DocumentCache provider metric summary '{providerSummaryPath}' is for provider "
                        + $"'{providerSummary.Provider}', expected '{providerName}'.",
                ]);
            }

            return new ProviderThresholdInputs(
                root,
                provider,
                providerName,
                DocumentCacheOperatorMetricsEvidence.LoadFromFile(
                    Path.Combine(root, DocumentCacheOperatorMetricsEvidence.RelativePath),
                    providerName
                ),
                providerSummary,
                providerSummaryPath
            );
        }

        public ThresholdPhaseMetrics Phase(string phase) =>
            ReadJsonArtifact<ThresholdPhaseMetrics>(Root, PhasePath(ProviderName, phase));
    }

    private sealed record ThresholdMeasurement(
        decimal MeasuredValue,
        string EvidencePath,
        string ReviewerNote,
        bool? PassedOverride = null
    );

    private sealed record ThresholdPhaseMetrics(
        string Provider,
        string Phase,
        double ElapsedMilliseconds,
        IReadOnlyList<DocumentCacheQualificationPhaseMetricValue> Metrics,
        PerfLatencySummary? Latency
    );
}
