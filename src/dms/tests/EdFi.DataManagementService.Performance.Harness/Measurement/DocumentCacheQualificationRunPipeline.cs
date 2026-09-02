// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Shared orchestration boundary for the long-running DMS-1317 DocumentCache representative
/// qualification. Provider-specific NUnit fixtures own database leasing and hand this pipeline
/// the API harness plus an out-of-band replay/assertion connection factory.
/// </summary>
public static class DocumentCacheQualificationRunPipeline
{
    private const int ExpectedHarnessDataStoreId = 1;
    private const string ExpectedHarnessTenantKey = "";
    private const string StandardJsonContentType = "application/json";
    private const long DisabledWriteFirstOrdinal = 1;
    private const long TrackingWriteFirstOrdinal = 1_001;
    private const int SmallInventoryWorkRows = 1;
    private const int NaturalInterruptionPollLimit = 300;
    private const string NaturalCommandCancellationInterruptionMode = "natural-command-cancellation";
    private const string DeterministicPartialProgressInterruptionMode =
        "deterministic-rebuilding-partial-progress";

    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly IReadOnlyList<DocumentCacheQualificationPhaseDefinition> _phaseDefinitions =
    [
        new("preflight-guards", "Preflight guards"),
        new("disabled-canonical-write-samples", "Disabled canonical write samples"),
        new("offline-activation-first-baseline", "Offline activation first baseline"),
        new("tracking-canonical-write-overhead", "Tracking canonical write overhead"),
        new("status-empty-work-latency", "Status empty work latency"),
        new("online-rebuild-clear-reseed-drain", "Online rebuild clear reseed drain"),
        new("interrupted-rebuild-restart-from-beginning", "Interrupted rebuild restart-from-beginning"),
        new("outage-distinct-document-writes", "Outage distinct-document writes"),
        new("outage-work-row-growth", "Outage work-row growth"),
        new("status-large-work-inventory-latency", "Status large work inventory latency"),
        new("outage-drain", "Outage drain"),
        new("status-small-work-inventory-latency", "Status small work inventory latency"),
        new("same-document-enqueue-ack-contention", "Same-document enqueue/ack contention"),
        new("explicit-integrity-scrub", "Explicit integrity scrub"),
        new("post-run-final-counts", "Post-run final counts"),
    ];

    public static IReadOnlyList<string> PhaseMetricRelativePaths(string providerName) =>
        PhaseArtifactRelativePaths("phase-metrics", providerName, "json");

    public static IReadOnlyList<string> CommandTranscriptRelativePaths(string providerName) =>
        PhaseArtifactRelativePaths("command-transcripts", providerName, "md");

    public static async Task<string> RunAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        DocumentCacheRepresentativeRunConfiguration configuration
    ) =>
        await RunAsync(
            harness,
            provider,
            openReplayConnectionAsync,
            leasedConnectionString,
            configuration,
            representativeEvidence: true
        );

    internal static async Task<string> RunSmokeAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        DocumentCacheRepresentativeRunConfiguration configuration
    ) =>
        await RunAsync(
            harness,
            provider,
            openReplayConnectionAsync,
            leasedConnectionString,
            configuration,
            representativeEvidence: false
        );

    private static async Task<string> RunAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        DocumentCacheRepresentativeRunConfiguration configuration,
        bool representativeEvidence
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(openReplayConnectionAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(leasedConnectionString);
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.Provider != provider)
        {
            throw new PerfObservationException(
                $"DocumentCache representative pipeline received provider {PerfProviders.ArtifactName(provider)} "
                    + $"with configuration for {PerfProviders.ArtifactName(configuration.Provider)}."
            );
        }

        if (configuration.HighWaterMark > int.MaxValue)
        {
            throw new PerfObservationException(
                $"DocumentCache representative pipeline requires {PerfEnvironmentVariables.DocumentCacheHighWaterMark} "
                    + $"to fit in a .NET int for DocumentCacheOptions; got {configuration.HighWaterMark}."
            );
        }

        DocumentCacheQualificationFixtureSetupResult setup = representativeEvidence
            ? await DocumentCacheQualificationFixtureSetup.PrepareAsync(
                harness,
                provider,
                openReplayConnectionAsync,
                leasedConnectionString,
                configuration
            )
            : await DocumentCacheQualificationFixtureSetup.PrepareSmokeAsync(
                harness,
                provider,
                openReplayConnectionAsync,
                leasedConnectionString,
                configuration
            );

        DocumentCacheQualificationRunner runner = new(
            harness,
            provider,
            openReplayConnectionAsync,
            leasedConnectionString,
            configuration,
            setup,
            representativeEvidence
        );
        await runner.ExecuteAsync();

        return setup.RunDirectory;
    }

    private static IReadOnlyList<string> PhaseArtifactRelativePaths(
        string directory,
        string providerName,
        string extension
    )
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new ArgumentException("Provider artifact name must not be blank.", nameof(providerName));
        }

        return
        [
            .. _phaseDefinitions.Select(phase =>
                $"{directory}/{providerName}-{phase.ArtifactStem}.{extension}"
            ),
        ];
    }

    private sealed class DocumentCacheQualificationRunner(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        DocumentCacheRepresentativeRunConfiguration configuration,
        DocumentCacheQualificationFixtureSetupResult setup,
        bool representativeEvidence
    )
    {
        private readonly ApiIntegrationHarness _harness = harness;
        private readonly PerfProvider _provider = provider;
        private readonly string _providerName = PerfProviders.ArtifactName(provider);
        private readonly string _leasedConnectionString = leasedConnectionString;
        private readonly DocumentCacheRepresentativeRunConfiguration _configuration = configuration;
        private readonly DocumentCacheQualificationFixtureSetupResult _setup = setup;
        private readonly bool _representativeEvidence = representativeEvidence;
        private readonly Func<Task<DbConnection>> _openReplayConnectionAsync = openReplayConnectionAsync;
        private readonly List<string> _completedPhaseStems = [];

        private IDocumentCacheProjectionSupervisor Supervisor =>
            _harness.Services.GetRequiredService<IDocumentCacheProjectionSupervisor>();

        private IDocumentCacheProjectionScheduler Scheduler =>
            _harness.Services.GetRequiredService<IDocumentCacheProjectionScheduler>();

        private IDocumentCacheAdministrativeCommandRunner AdministrativeCommandRunner =>
            _harness.Services.GetRequiredService<IDocumentCacheAdministrativeCommandRunner>();

        private IDocumentCacheStatusService StatusService =>
            _harness.Services.GetRequiredService<IDocumentCacheStatusService>();

        private IDocumentCacheOfflineActivationCommand OfflineActivationCommand =>
            _harness.Services.GetRequiredService<IDocumentCacheOfflineActivationCommand>();

        private IDocumentCacheOnlineCacheRebuildCommand OnlineCacheRebuildCommand =>
            _harness.Services.GetRequiredService<IDocumentCacheOnlineCacheRebuildCommand>();

        private IDocumentCacheExplicitIntegrityScrubCommand ExplicitIntegrityScrubCommand =>
            _harness.Services.GetRequiredService<IDocumentCacheExplicitIntegrityScrubCommand>();

        public async Task ExecuteAsync()
        {
            await EnsureConnectionOpenAsync();
            await using DbConnection metricsConnection = await _openReplayConnectionAsync();
            IDocumentCacheProviderMetricCapture providerMetricCapture =
                DocumentCacheProviderMetricCapture.Create(
                    metricsConnection,
                    _provider,
                    _setup.RunDirectory,
                    _configuration
                );
            await providerMetricCapture.InitializeAsync();

            await RunPreflightGuardsAsync();
            DocumentCacheQualificationWriteBatchMetrics disabledWrites =
                await MeasureDisabledCanonicalWritesAsync();
            await RunOfflineActivationAsync(providerMetricCapture);
            await MeasureTrackingCanonicalWriteOverheadAsync(disabledWrites);
            await SampleStatusEmptyWorkLatencyAsync();
            await RunOnlineRebuildAsync(providerMetricCapture);
            await RunInterruptedRebuildRestartFromBeginningAsync(providerMetricCapture);
            DocumentCacheQualificationWriteBatchMetrics outageWrites = await RunOutageWritesAsync();
            await MeasureOutageWorkRowGrowthAsync(outageWrites);
            await SampleStatusLargeWorkInventoryLatencyAsync();
            await providerMetricCapture.CaptureQuerySamplesAsync();
            await DrainOutageBacklogAsync(providerMetricCapture);
            await SampleStatusSmallWorkInventoryLatencyAsync();
            await RunSameDocumentEnqueueAckContentionAsync();
            await RunExplicitIntegrityScrubAsync();
            await WritePostRunFinalCountsAsync();
            await providerMetricCapture.CompleteAsync();
            WriteQualificationSummary();
        }

        private async Task RunPreflightGuardsAsync()
        {
            const string phase = "preflight-guards";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();

            DocumentCacheProjectionTargetRuntimeContext targetContext =
                await RefreshSingleTargetContextAsync();
            DocumentCacheTargetExecutionContext executionContext = targetContext.TargetExecutionContext;
            GuardTargetIdentity(executionContext);
            GuardEffectiveSettings(executionContext.EffectiveSettings);
            GuardProviderPrerequisites(executionContext);

            DocumentCacheStatusResponse status = await StatusService.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );
            GuardStatusTargetPreflight(status);

            await SetLifecycleAsync(DocumentCacheLifecycleState.Disabled, cacheAheadRecoveryRequired: false);
            await ClearDocumentCacheAsync();
            await ClearProjectionWorkAsync();
            await RefreshSingleTargetContextAsync();

            stopwatch.Stop();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("targetTenantKey", executionContext.TargetKey.TenantKey),
                    Metric("targetDataStoreId", executionContext.TargetKey.DataStoreId),
                    Metric("targetGeneration", executionContext.Generation.Value),
                    Metric("targetProviderToken", executionContext.ProviderToken.Value),
                    Metric("connectionIdentity", "matched-leased-database"),
                    Metric("physicalSourceFingerprint", executionContext.PhysicalSourceFingerprint.Value),
                    Metric("inventoryStatus", executionContext.Inventory.Status),
                    Metric("enqueueTriggerStatus", executionContext.EnqueueTrigger.Status),
                    Metric("projectorPageSize", executionContext.EffectiveSettings.ProjectorPageSize, "rows"),
                    Metric(
                        "projectorMaxConcurrentTargets",
                        executionContext.EffectiveSettings.ProjectorMaxConcurrentTargets,
                        "targets"
                    ),
                    Metric(
                        "projectorBaselineHighWaterMark",
                        executionContext.EffectiveSettings.ProjectorBaselineHighWaterMark,
                        "rows"
                    ),
                    Metric("statusTargetCount", status.Targets.Length, "targets"),
                ],
                statusSnapshot: status
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Validated the single DocumentCache target resolved by the integration harness.",
                    "Verified provider prerequisites, inventory, enqueue trigger, connection identity, and requested projector settings.",
                    "Reset the benchmark target to `Disabled` and cleared `dms.DocumentCache` plus `dms.DocumentProjectionWork` before canonical write samples.",
                ]
            );
        }

        private async Task<DocumentCacheQualificationWriteBatchMetrics> MeasureDisabledCanonicalWritesAsync()
        {
            const string phase = "disabled-canonical-write-samples";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();

            DocumentCacheQualificationWriteBatchMetrics batch = await WriteStudentBatchAsync(
                DisabledWriteFirstOrdinal,
                warmupCount: _configuration.WarmupStatusSamples,
                measuredCount: _configuration.MeasuredStatusSamples,
                phase
            );

            stopwatch.Stop();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            long workRowsCreated =
                countsAfter.DocumentProjectionWorkRows - countsBefore.DocumentProjectionWorkRows;

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("canonicalWriteMode", "disabled"),
                    Metric("measuredWriteCount", batch.MeasuredCount, "writes"),
                    Metric("warmupWriteCount", batch.WarmupCount, "writes"),
                    Metric("workRowsCreated", workRowsCreated, "rows"),
                    Metric("disabledWriteP95Ms", batch.Latency?.P95Ms, "ms"),
                ],
                latency: batch.Latency,
                writeBatch: batch
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Measured canonical `PUT /data/ed-fi/students/{id}` samples while DocumentCache lifecycle was `Disabled`.",
                    "The projector target was not draining during these writes.",
                    $"Observed work-row delta: `{workRowsCreated.ToString(CultureInfo.InvariantCulture)}`.",
                ]
            );

            ThrowIfWriteBatchFailed(phase, batch);
            if (workRowsCreated != 0)
            {
                throw new PerfObservationException(
                    "Disabled canonical writes created DocumentProjectionWork rows; DocumentCache write overhead baseline is invalid."
                );
            }

            return batch;
        }

        private async Task RunOfflineActivationAsync(
            IDocumentCacheProviderMetricCapture providerMetricCapture
        )
        {
            const string phase = "offline-activation-first-baseline";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            DocumentCacheProjectionTargetRuntimeContext targetContext =
                await RefreshSingleTargetContextAsync();
            DocumentCacheProviderMetricPhaseScope metricScope = await providerMetricCapture.BeginPhaseAsync(
                phase
            );
            Stopwatch stopwatch = Stopwatch.StartNew();

            DocumentCacheAdministrativeCommandResult result = await OfflineActivationCommand.ExecuteAsync(
                new DocumentCacheOfflineActivationRequest(
                    DocumentCacheAdministrativeTargetKey.FromTargetKey(
                        targetContext.TargetExecutionContext.TargetKey
                    ),
                    new DocumentCacheOfflineWriterAdmission(
                        confirmed: true,
                        DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
                    ),
                    targetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.OfflineActivation
                )
            );

            stopwatch.Stop();
            await RefreshSingleTargetContextAsync();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            await providerMetricCapture.EndPhaseAsync(metricScope, countsAfter.DocumentCacheRows);
            DocumentCacheStatusResponse status = await StatusService.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("administrativeCommand", "offline activation"),
                    Metric("commandStatus", result.Status),
                    Metric("commandClassification", result.Classification),
                    Metric("commandLifecycle", result.Lifecycle?.ToString() ?? "null"),
                    Metric("cacheRowsAfter", countsAfter.DocumentCacheRows, "rows"),
                    Metric("workRowsAfter", countsAfter.DocumentProjectionWorkRows, "rows"),
                ],
                commandResult: result,
                statusSnapshot: status
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Ran the production offline activation command with confirmed closed-and-drained writer admission.",
                    "The command cleared projected state, seeded the first baseline, drained work, and returned the target to `Tracking`.",
                    $"Result status: `{result.Status}`; classification: `{result.Classification}`.",
                ]
            );

            AssertAdministrativeCommandCompleted(phase, result, DocumentCacheLifecycleState.Tracking);
            AssertTrackingCaughtUp(phase, RequireSingleStatusTarget(status));
        }

        private async Task MeasureTrackingCanonicalWriteOverheadAsync(
            DocumentCacheQualificationWriteBatchMetrics disabledWrites
        )
        {
            const string phase = "tracking-canonical-write-overhead";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();

            DocumentCacheQualificationWriteBatchMetrics trackingWrites = await WriteStudentBatchAsync(
                TrackingWriteFirstOrdinal,
                warmupCount: _configuration.WarmupStatusSamples,
                measuredCount: _configuration.MeasuredStatusSamples,
                phase,
                afterEachWriteAsync: async () => await DrainUntilEmptyAsync()
            );

            stopwatch.Stop();
            await RefreshSingleTargetContextAsync();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            DocumentCacheStatusResponse status = await StatusService.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );
            double? overheadRatio =
                disabledWrites.Latency is null || disabledWrites.Latency.P95Ms <= double.Epsilon
                    ? null
                    : trackingWrites.Latency!.P95Ms / disabledWrites.Latency.P95Ms;

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("canonicalWriteMode", "tracking-caught-up"),
                    Metric("disabledWriteP95Ms", disabledWrites.Latency?.P95Ms, "ms"),
                    Metric("trackingWriteP95Ms", trackingWrites.Latency?.P95Ms, "ms"),
                    Metric("trackingWriteOverheadRatio", overheadRatio, "ratio"),
                    Metric("measuredWriteCount", trackingWrites.MeasuredCount, "writes"),
                    Metric("workRowsAfterDrain", countsAfter.DocumentProjectionWorkRows, "rows"),
                ],
                latency: trackingWrites.Latency,
                writeBatch: trackingWrites,
                statusSnapshot: status
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Measured canonical writes while the target was already `Tracking` and caught up before every sample.",
                    "Each write was followed by an out-of-band administrative drain outside the measured HTTP write window.",
                    $"Calculated tracking write overhead ratio: `{FormatMetricValue(overheadRatio)}`.",
                ]
            );

            ThrowIfWriteBatchFailed(phase, trackingWrites);
            AssertTrackingCaughtUp(phase, RequireSingleStatusTarget(status));
        }

        private async Task SampleStatusEmptyWorkLatencyAsync()
        {
            const string phase = "status-empty-work-latency";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();
            DocumentCacheStatusSamplingResult sample = await SampleStatusLatencyAsync();
            stopwatch.Stop();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            DocumentCacheStatusTarget target = RequireSingleStatusTarget(sample.StatusSnapshot);

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                StatusMetrics(target, "empty"),
                latency: sample.Latency,
                statusSnapshot: sample.StatusSnapshot
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Sampled the DocumentCache status service while `dms.DocumentProjectionWork` was empty.",
                    "The oldest-work component was evaluated as part of each status observation.",
                    $"Status p95: `{sample.Latency.P95Ms.ToString("F3", CultureInfo.InvariantCulture)} ms`.",
                ]
            );

            AssertTrackingCaughtUp(phase, target);
        }

        private async Task RunOnlineRebuildAsync(IDocumentCacheProviderMetricCapture providerMetricCapture)
        {
            const string phase = "online-rebuild-clear-reseed-drain";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            DocumentCacheProjectionTargetRuntimeContext targetContext =
                await RefreshSingleTargetContextAsync();
            DocumentCacheProviderMetricPhaseScope metricScope = await providerMetricCapture.BeginPhaseAsync(
                phase
            );
            Stopwatch stopwatch = Stopwatch.StartNew();

            DocumentCacheAdministrativeCommandResult result = await OnlineCacheRebuildCommand.ExecuteAsync(
                new DocumentCacheOnlineCacheRebuildRequest(
                    DocumentCacheAdministrativeTargetKey.FromTargetKey(
                        targetContext.TargetExecutionContext.TargetKey
                    ),
                    targetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
                )
            );

            stopwatch.Stop();
            await RefreshSingleTargetContextAsync();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            await providerMetricCapture.EndPhaseAsync(metricScope, countsAfter.DocumentCacheRows);
            DocumentCacheStatusResponse status = await StatusService.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("administrativeCommand", "online cache rebuild"),
                    Metric("commandStatus", result.Status),
                    Metric("commandClassification", result.Classification),
                    Metric("clearPhaseObserved", HasPhaseDiagnosticOrResult(result, "ClearCache")),
                    Metric("reseedPhaseObserved", HasPhaseDiagnosticOrResult(result, "SeedBaseline")),
                    Metric("drainPhaseObserved", HasPhaseDiagnosticOrResult(result, "DrainWork")),
                    Metric("cacheRowsAfter", countsAfter.DocumentCacheRows, "rows"),
                    Metric("workRowsAfter", countsAfter.DocumentProjectionWorkRows, "rows"),
                ],
                commandResult: result,
                statusSnapshot: status
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Ran the production online rebuild command from `Tracking`.",
                    "The command path covers clear, reseed, drain, and return to `Tracking`.",
                    $"Result status: `{result.Status}`; classification: `{result.Classification}`.",
                ]
            );

            AssertAdministrativeCommandCompleted(phase, result, DocumentCacheLifecycleState.Tracking);
            AssertTrackingCaughtUp(phase, RequireSingleStatusTarget(status));
        }

        private async Task RunInterruptedRebuildRestartFromBeginningAsync(
            IDocumentCacheProviderMetricCapture providerMetricCapture
        )
        {
            const string phase = "interrupted-rebuild-restart-from-beginning";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            DocumentCacheProjectionTargetRuntimeContext targetContext =
                await RefreshSingleTargetContextAsync();
            DocumentCacheProviderMetricPhaseScope metricScope = await providerMetricCapture.BeginPhaseAsync(
                phase
            );

            Stopwatch stopwatch = Stopwatch.StartNew();
            DocumentCacheAdministrativeCommandResult? interruptedResult =
                await TryInterruptOnlineRebuildAsync(targetContext);
            DocumentCacheQualificationPhaseCounts interruptedCounts = await CaptureCountsAsync();
            string interruptionMode = NaturalCommandCancellationInterruptionMode;
            IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics> deterministicSlices = [];

            if (!IsInterruptedRebuildingState(interruptedResult, interruptedCounts))
            {
                if (_representativeEvidence)
                {
                    string interruptedCommandStatus =
                        interruptedResult?.Status.ToString() ?? "cancelled-or-not-observed";
                    throw new PerfObservationException(
                        "Representative interrupted-rebuild qualification requires observing the production "
                            + "online rebuild command stop in Rebuilding with partial cache/work progress. "
                            + "Synthetic interrupted state setup is allowed only for smoke-scale pipeline validation. "
                            + $"Observed lifecycle '{interruptedCounts.ProjectionLifecycleState}', "
                            + $"cache rows {interruptedCounts.DocumentCacheRows}, "
                            + $"work rows {interruptedCounts.DocumentProjectionWorkRows}, "
                            + $"command status '{interruptedCommandStatus}'."
                    );
                }

                (interruptedCounts, deterministicSlices) =
                    await BuildDeterministicInterruptedRebuildStateAsync();
                interruptionMode = DeterministicPartialProgressInterruptionMode;
            }

            DocumentCacheProjectionTargetRuntimeContext replacementTargetContext =
                await RefreshSingleTargetContextAsync();
            Stopwatch replacementStopwatch = Stopwatch.StartNew();
            DocumentCacheAdministrativeCommandResult replacementResult =
                await OnlineCacheRebuildCommand.ExecuteAsync(
                    new DocumentCacheOnlineCacheRebuildRequest(
                        DocumentCacheAdministrativeTargetKey.FromTargetKey(
                            replacementTargetContext.TargetExecutionContext.TargetKey
                        ),
                        replacementTargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                        DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
                    )
                );
            replacementStopwatch.Stop();
            stopwatch.Stop();

            await RefreshSingleTargetContextAsync();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            await providerMetricCapture.EndPhaseAsync(metricScope, countsAfter.DocumentCacheRows);
            DocumentCacheStatusResponse status = await StatusService.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("interruptionMode", interruptionMode),
                    Metric(
                        "interruptedCommandStatus",
                        interruptedResult?.Status.ToString() ?? "not-observed"
                    ),
                    Metric(
                        "interruptedCommandClassification",
                        interruptedResult?.Classification.ToString() ?? "not-observed"
                    ),
                    Metric("interruptedLifecycleState", interruptedCounts.ProjectionLifecycleState),
                    Metric("interruptedCacheRows", interruptedCounts.DocumentCacheRows, "rows"),
                    Metric("interruptedWorkRows", interruptedCounts.DocumentProjectionWorkRows, "rows"),
                    Metric("replacementCommandStatus", replacementResult.Status),
                    Metric("replacementCommandClassification", replacementResult.Classification),
                    Metric("replacementElapsedMs", replacementStopwatch.Elapsed.TotalMilliseconds, "ms"),
                    Metric("restartFromBeginningCompleted", true),
                ],
                commandResult: replacementResult,
                statusSnapshot: status,
                drainSlices: deterministicSlices.Count == 0 ? null : deterministicSlices
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Started online rebuild and attempted to interrupt it after `Rebuilding` with partial progress.",
                    $"Interrupted state mode: `{interruptionMode}`.",
                    "Ran the replacement online rebuild command from `Rebuilding` to prove restart-from-beginning completion.",
                    $"Replacement result status: `{replacementResult.Status}`; classification: `{replacementResult.Classification}`.",
                ]
            );

            AssertAdministrativeCommandCompleted(
                phase,
                replacementResult,
                DocumentCacheLifecycleState.Tracking
            );
            AssertTrackingCaughtUp(phase, RequireSingleStatusTarget(status));
        }

        private async Task<DocumentCacheQualificationWriteBatchMetrics> RunOutageWritesAsync()
        {
            const string phase = "outage-distinct-document-writes";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            if (countsBefore.DocumentProjectionWorkRows != 0)
            {
                throw new PerfObservationException(
                    "Outage write phase requires an empty DocumentProjectionWork table before the projector is held idle."
                );
            }

            DocumentProjectionWorkDmlSnapshot queueDmlBefore =
                await CaptureDocumentProjectionWorkDmlSnapshotAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();
            DocumentCacheQualificationWriteBatchMetrics batch = await WriteStudentBatchAsync(
                FirstOutageOrdinal(),
                warmupCount: 0,
                measuredCount: _configuration.OutageDistinctDocumentWrites,
                phase
            );
            stopwatch.Stop();
            DocumentProjectionWorkDmlSnapshot queueDmlAfter =
                await CaptureDocumentProjectionWorkDmlSnapshotAsync();
            DocumentProjectionWorkDmlSnapshot queueDmlDelta = queueDmlAfter.DeltaFrom(queueDmlBefore, phase);
            if (queueDmlDelta.Total < batch.SuccessfulMeasuredCount)
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' observed {queueDmlDelta.Total} DocumentProjectionWork DML attempts "
                        + $"for {batch.SuccessfulMeasuredCount} successful outage writes; queue DML evidence is stale "
                        + "or the enqueue path did not run for every successful write."
                );
            }

            decimal queueDmlAmplificationRatio =
                batch.MeasuredCount == 0 ? 0m : (decimal)queueDmlDelta.Total / batch.MeasuredCount;
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("projectorDrainMode", "not-draining"),
                    Metric("distinctTouchedDocuments", batch.MeasuredCount, "documents"),
                    Metric("firstTouchedOrdinal", batch.FirstOrdinal),
                    Metric("lastTouchedOrdinal", batch.LastOrdinal),
                    Metric("queueDmlInsertCount", queueDmlDelta.Inserted, "attempts"),
                    Metric("queueDmlUpdateCount", queueDmlDelta.Updated, "attempts"),
                    Metric("queueDmlDeleteCount", queueDmlDelta.Deleted, "attempts"),
                    Metric("queueDmlAttemptCount", queueDmlDelta.Total, "attempts"),
                    Metric("queueDmlAmplificationRatio", queueDmlAmplificationRatio, "ratio"),
                    Metric("writeP95Ms", batch.Latency?.P95Ms, "ms"),
                ],
                latency: batch.Latency,
                writeBatch: batch
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Created distinct-document outage writes while the background projector target was held idle.",
                    "No manual drain ran during this phase.",
                    $"Touched `{batch.MeasuredCount.ToString(CultureInfo.InvariantCulture)}` distinct documents.",
                    "Captured DocumentProjectionWork insert/update/delete counter deltas for queue DML amplification.",
                ]
            );

            ThrowIfWriteBatchFailed(phase, batch);
            return batch;
        }

        private async Task MeasureOutageWorkRowGrowthAsync(
            DocumentCacheQualificationWriteBatchMetrics outageWrites
        )
        {
            const string phase = "outage-work-row-growth";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();
            long observedWorkRows = countsBefore.DocumentProjectionWorkRows;
            double growthRatio =
                outageWrites.MeasuredCount == 0 ? 0 : (double)observedWorkRows / outageWrites.MeasuredCount;
            stopwatch.Stop();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("distinctTouchedDocuments", outageWrites.MeasuredCount, "documents"),
                    Metric("observedDocumentProjectionWorkRows", observedWorkRows, "rows"),
                    Metric("workRowGrowthRatio", growthRatio, "ratio"),
                ],
                writeBatch: outageWrites
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Measured DocumentProjectionWork growth after outage writes and before any drain.",
                    $"Work-row growth ratio: `{growthRatio.ToString("F6", CultureInfo.InvariantCulture)}`.",
                ]
            );
            WriteText(
                $"outage-drain-evidence/{_providerName}-outage-work-row-growth.json",
                PerfArtifactJson.Serialize(metrics)
            );

            if (observedWorkRows != outageWrites.MeasuredCount)
            {
                throw new PerfObservationException(
                    "Outage writes did not produce one durable work row per distinct touched document; "
                        + $"expected {outageWrites.MeasuredCount}, observed {observedWorkRows}."
                );
            }
        }

        private async Task SampleStatusLargeWorkInventoryLatencyAsync()
        {
            const string phase = "status-large-work-inventory-latency";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();
            DocumentCacheStatusSamplingResult sample = await SampleStatusLatencyAsync();
            stopwatch.Stop();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            DocumentCacheStatusTarget target = RequireSingleStatusTarget(sample.StatusSnapshot);

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                StatusMetrics(target, "large"),
                latency: sample.Latency,
                statusSnapshot: sample.StatusSnapshot
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Sampled status and oldest-work latency with the large outage work inventory still present.",
                    $"Work rows at sampling start: `{countsBefore.DocumentProjectionWorkRows.ToString(CultureInfo.InvariantCulture)}`.",
                ]
            );

            AssertTrackingNotCaughtUp(phase, target);
        }

        private async Task DrainOutageBacklogAsync(IDocumentCacheProviderMetricCapture providerMetricCapture)
        {
            const string phase = "outage-drain";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            DocumentCacheProviderMetricPhaseScope metricScope = await providerMetricCapture.BeginPhaseAsync(
                phase
            );
            Stopwatch stopwatch = Stopwatch.StartNew();
            IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics> slices = await DrainUntilEmptyAsync();
            stopwatch.Stop();
            await RefreshSingleTargetContextAsync();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            await providerMetricCapture.EndPhaseAsync(metricScope, countsBefore.DocumentProjectionWorkRows);
            DocumentCacheStatusResponse status = await StatusService.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("startingWorkRows", countsBefore.DocumentProjectionWorkRows, "rows"),
                    Metric("endingWorkRows", countsAfter.DocumentProjectionWorkRows, "rows"),
                    Metric("drainSliceCount", slices.Count, "slices"),
                    Metric("drainElapsedMs", stopwatch.Elapsed.TotalMilliseconds, "ms"),
                ],
                statusSnapshot: status,
                drainSlices: slices
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Drained the outage backlog with administrative drain slices.",
                    $"Started with `{countsBefore.DocumentProjectionWorkRows.ToString(CultureInfo.InvariantCulture)}` work rows.",
                    $"Finished with `{countsAfter.DocumentProjectionWorkRows.ToString(CultureInfo.InvariantCulture)}` work rows.",
                ]
            );
            WriteText(
                $"outage-drain-evidence/{_providerName}-outage-drain.json",
                PerfArtifactJson.Serialize(metrics)
            );

            AssertTrackingCaughtUp(phase, RequireSingleStatusTarget(status));
        }

        private async Task SampleStatusSmallWorkInventoryLatencyAsync()
        {
            const string phase = "status-small-work-inventory-latency";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            if (countsBefore.DocumentProjectionWorkRows != 0)
            {
                throw new PerfObservationException(
                    "Small work inventory status phase requires an empty DocumentProjectionWork table."
                );
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            DocumentCacheQualificationWriteBatchMetrics writeBatch = await WriteStudentBatchAsync(
                SameDocumentOrdinal(),
                warmupCount: 0,
                measuredCount: SmallInventoryWorkRows,
                phase
            );
            DocumentCacheStatusSamplingResult sample = await SampleStatusLatencyAsync();
            stopwatch.Stop();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            DocumentCacheStatusTarget target = RequireSingleStatusTarget(sample.StatusSnapshot);

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    .. StatusMetrics(target, "small"),
                    Metric("smallInventoryWrites", SmallInventoryWorkRows, "writes"),
                ],
                latency: sample.Latency,
                writeBatch: writeBatch,
                statusSnapshot: sample.StatusSnapshot
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Created a small work inventory and sampled status plus oldest-work latency before draining it.",
                    "The same document remains enqueued for the following same-document contention phase.",
                ]
            );

            ThrowIfWriteBatchFailed(phase, writeBatch);
            AssertTrackingNotCaughtUp(phase, target);
        }

        private async Task RunSameDocumentEnqueueAckContentionAsync()
        {
            const string phase = "same-document-enqueue-ack-contention";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            if (countsBefore.DocumentProjectionWorkRows < 1)
            {
                throw new PerfObservationException(
                    "Same-document contention phase requires one pre-existing work row to race enqueue with acknowledgement."
                );
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            Task<DocumentCacheQualificationWriteBatchMetrics> writesTask = WriteSameDocumentConcurrentlyAsync(
                phase
            );
            Task<IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics>> drainTask =
                DrainWhileWritesRunAsync(writesTask);

            DocumentCacheQualificationWriteBatchMetrics writeBatch = await writesTask;
            IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics> slices = await drainTask;
            stopwatch.Stop();

            await RefreshSingleTargetContextAsync();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            DocumentCacheStatusResponse status = await StatusService.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );

            int retryLikeFailureCount = writeBatch.Failures.Count(failure =>
                failure.Message.Contains("retry", StringComparison.OrdinalIgnoreCase)
                || failure.Message.Contains("serialization", StringComparison.OrdinalIgnoreCase)
            );
            int deadlockLikeFailureCount = writeBatch.Failures.Count(failure =>
                failure.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
            );

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("sameDocumentContenders", _configuration.SameDocumentContention, "writers"),
                    Metric("successfulWriters", writeBatch.SuccessfulMeasuredCount, "writers"),
                    Metric("failedWriters", writeBatch.Failures.Count, "writers"),
                    Metric("retryLikeFailureCount", retryLikeFailureCount, "failures"),
                    Metric("deadlockLikeFailureCount", deadlockLikeFailureCount, "failures"),
                    Metric("p95LockWaitMs", writeBatch.Latency?.P95Ms, "ms"),
                    Metric("drainSliceCount", slices.Count, "slices"),
                    Metric("workRowsAfterDrain", countsAfter.DocumentProjectionWorkRows, "rows"),
                ],
                latency: writeBatch.Latency,
                writeBatch: writeBatch,
                statusSnapshot: status,
                drainSlices: slices
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Ran same-document canonical `PUT` contenders while administrative drain slices acknowledged the same work row.",
                    $"Contenders: `{_configuration.SameDocumentContention.ToString(CultureInfo.InvariantCulture)}`.",
                    $"Failures: `{writeBatch.Failures.Count.ToString(CultureInfo.InvariantCulture)}`; deadlock-like failures: `{deadlockLikeFailureCount.ToString(CultureInfo.InvariantCulture)}`.",
                ]
            );
            WriteText(
                $"writer-contention-evidence/{_providerName}-same-document-enqueue-ack-contention.json",
                PerfArtifactJson.Serialize(metrics)
            );

            ThrowIfWriteBatchFailed(phase, writeBatch);
            AssertTrackingCaughtUp(phase, RequireSingleStatusTarget(status));
        }

        private async Task RunExplicitIntegrityScrubAsync()
        {
            const string phase = "explicit-integrity-scrub";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            DocumentCacheProjectionTargetRuntimeContext targetContext =
                await RefreshSingleTargetContextAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();

            DocumentCacheAdministrativeCommandResult result =
                await ExplicitIntegrityScrubCommand.ExecuteAsync(
                    new DocumentCacheExplicitIntegrityScrubRequest(
                        DocumentCacheAdministrativeTargetKey.FromTargetKey(
                            targetContext.TargetExecutionContext.TargetKey
                        ),
                        targetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                        DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
                    )
                );

            AssertAdministrativeCommandCompleted(phase, result, DocumentCacheLifecycleState.Tracking);

            await RefreshSingleTargetContextAsync();
            DocumentCacheQualificationPhaseCounts countsAfterCommand = await CaptureCountsAsync();
            IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics> repairDrainSlices =
                countsAfterCommand.DocumentProjectionWorkRows == 0 ? [] : await DrainUntilEmptyAsync();
            stopwatch.Stop();

            await RefreshSingleTargetContextAsync();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();
            DocumentCacheStatusResponse status = await StatusService.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("administrativeCommand", "explicit integrity scrub"),
                    Metric("commandStatus", result.Status),
                    Metric("commandClassification", result.Classification),
                    Metric("commandLifecycle", result.Lifecycle?.ToString() ?? "null"),
                    Metric(
                        "scrubWorkRowsAfterCommand",
                        countsAfterCommand.DocumentProjectionWorkRows,
                        "rows"
                    ),
                    Metric("scrubRepairDrainSliceCount", repairDrainSlices.Count, "slices"),
                    Metric("finalCacheRows", countsAfter.DocumentCacheRows, "rows"),
                    Metric("finalWorkRows", countsAfter.DocumentProjectionWorkRows, "rows"),
                ],
                commandResult: result,
                statusSnapshot: status,
                drainSlices: repairDrainSlices.Count == 0 ? null : repairDrainSlices
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Ran the production explicit integrity scrub command after all workload phases.",
                    "Drained any repair work the scrub queued before recording final caught-up status.",
                    $"Result status: `{result.Status}`; classification: `{result.Classification}`.",
                ]
            );

            AssertTrackingCaughtUp(phase, RequireSingleStatusTarget(status));
        }

        private async Task WritePostRunFinalCountsAsync()
        {
            const string phase = "post-run-final-counts";
            DocumentCacheQualificationPhaseCounts countsBefore = await CaptureCountsAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();
            DocumentCacheStatusResponse status = await StatusService.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );
            stopwatch.Stop();
            DocumentCacheQualificationPhaseCounts countsAfter = await CaptureCountsAsync();

            DocumentCacheQualificationPhaseMetrics metrics = CreatePhaseMetrics(
                phase,
                stopwatch.Elapsed,
                countsBefore,
                countsAfter,
                [
                    Metric("sourceDocumentRows", countsAfter.SourceDocumentRows, "rows"),
                    Metric("dmsDocumentRows", countsAfter.DmsDocumentRows, "rows"),
                    Metric("documentCacheRows", countsAfter.DocumentCacheRows, "rows"),
                    Metric("documentProjectionWorkRows", countsAfter.DocumentProjectionWorkRows, "rows"),
                    Metric("completedRuntimePhaseCount", _completedPhaseStems.Count + 1, "phases"),
                ],
                statusSnapshot: status
            );

            WritePhaseArtifacts(
                phase,
                metrics,
                [
                    "Captured final DocumentCache row counts and final direct status snapshot.",
                    "Provider-specific post-run maintenance metrics are collected by the provider metric capture phase.",
                ]
            );

            AssertTrackingCaughtUp(phase, RequireSingleStatusTarget(status));
        }

        private async Task<DocumentCacheAdministrativeCommandResult?> TryInterruptOnlineRebuildAsync(
            DocumentCacheProjectionTargetRuntimeContext targetContext
        )
        {
            using CancellationTokenSource cancellationSource = new();
            Task<DocumentCacheAdministrativeCommandResult> rebuildTask =
                OnlineCacheRebuildCommand.ExecuteAsync(
                    new DocumentCacheOnlineCacheRebuildRequest(
                        DocumentCacheAdministrativeTargetKey.FromTargetKey(
                            targetContext.TargetExecutionContext.TargetKey
                        ),
                        targetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                        DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
                    ),
                    cancellationSource.Token
                );

            for (int poll = 0; poll < NaturalInterruptionPollLimit; poll++)
            {
                Task completed = await Task.WhenAny(rebuildTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
                if (completed == rebuildTask)
                {
                    return await rebuildTask;
                }

                DocumentCacheQualificationPhaseCounts counts = await CaptureCountsAsync();
                if (
                    counts.ProjectionLifecycleState == nameof(DocumentCacheLifecycleState.Rebuilding)
                    && (
                        counts.DocumentProjectionWorkRows > 0
                        || counts.DocumentCacheRows
                            < _setup.FixtureSetupMetrics.InitialCounts.DocumentCacheRows
                    )
                )
                {
                    await cancellationSource.CancelAsync();
                    return await AwaitInterruptedCommandAsync(rebuildTask);
                }
            }

            await cancellationSource.CancelAsync();
            return await AwaitInterruptedCommandAsync(rebuildTask);
        }

        private static async Task<DocumentCacheAdministrativeCommandResult?> AwaitInterruptedCommandAsync(
            Task<DocumentCacheAdministrativeCommandResult> task
        )
        {
            try
            {
                return await task;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private static bool IsInterruptedRebuildingState(
            DocumentCacheAdministrativeCommandResult? interruptedResult,
            DocumentCacheQualificationPhaseCounts interruptedCounts
        ) =>
            interruptedCounts.ProjectionLifecycleState == nameof(DocumentCacheLifecycleState.Rebuilding)
            && (interruptedCounts.DocumentProjectionWorkRows > 0 || interruptedCounts.DocumentCacheRows > 0)
            && interruptedResult?.Status != DocumentCacheAdministrativeCommandStatus.Completed;

        private async Task<(
            DocumentCacheQualificationPhaseCounts InterruptedCounts,
            IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics> DrainSlices
        )> BuildDeterministicInterruptedRebuildStateAsync()
        {
            await SetLifecycleAsync(DocumentCacheLifecycleState.Resetting, cacheAheadRecoveryRequired: false);
            await ClearDocumentCacheAsync();
            await ClearProjectionWorkAsync();
            await SetLifecycleAsync(
                DocumentCacheLifecycleState.Rebuilding,
                cacheAheadRecoveryRequired: false
            );

            int seededRows = checked(
                (int)Math.Min(_configuration.HighWaterMark, Math.Max(_configuration.PageSize * 3L, 3L))
            );
            await InsertProjectionWorkForLeadingDocumentsAsync(seededRows);
            await RefreshSingleTargetContextAsync();

            DocumentCacheProjectionSchedulerDispatchResult dispatchResult =
                await RunCommandOwnedDrainSliceAsync();

            DocumentCacheQualificationDrainSliceMetrics drainSlice = DrainSliceMetric(1, dispatchResult);
            DocumentCacheQualificationPhaseCounts interruptedCounts = await CaptureCountsAsync();
            if (interruptedCounts.ProjectionLifecycleState != nameof(DocumentCacheLifecycleState.Rebuilding))
            {
                throw new PerfObservationException(
                    "Deterministic interrupted rebuild setup did not leave the target in Rebuilding."
                );
            }

            if (interruptedCounts.DocumentProjectionWorkRows == 0 && interruptedCounts.DocumentCacheRows == 0)
            {
                throw new PerfObservationException(
                    "Deterministic interrupted rebuild setup did not produce partial cache/work progress."
                );
            }

            return (interruptedCounts, [drainSlice]);
        }

        private async Task<IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics>> DrainUntilEmptyAsync()
        {
            List<DocumentCacheQualificationDrainSliceMetrics> slices = [];
            DocumentCacheQualificationPhaseCounts startingCounts = await CaptureCountsAsync();
            long expectedPages =
                _configuration.PageSize <= 0
                    ? startingCounts.DocumentProjectionWorkRows
                    : (startingCounts.DocumentProjectionWorkRows / _configuration.PageSize) + 1;
            long maxSlices = Math.Max(10, expectedPages + 1_000);

            for (int slice = 1; slice <= maxSlices; slice++)
            {
                DocumentCacheProjectionSchedulerDispatchResult dispatchResult =
                    await RunCommandOwnedDrainSliceAsync();
                DocumentCacheQualificationDrainSliceMetrics sliceMetric = DrainSliceMetric(
                    slice,
                    dispatchResult
                );
                slices.Add(sliceMetric);

                DocumentCacheProjectionDrainPageResult? drainResult = dispatchResult.DrainResult;
                if (drainResult?.Outcome == DocumentCacheProjectionDrainPageOutcome.NoEligibleWork)
                {
                    return slices;
                }

                if (
                    dispatchResult.Status != DocumentCacheProjectionSchedulerDispatchStatus.Dispatched
                    || drainResult?.Outcome != DocumentCacheProjectionDrainPageOutcome.PageProcessed
                    || drainResult.DocumentScopedFailureCount != 0
                    || drainResult.AdministrativeFailure is not null
                )
                {
                    throw new PerfObservationException(
                        "DocumentCache administrative drain did not process a clean page; "
                            + $"status={dispatchResult.Status}, outcome={drainResult?.Outcome.ToString() ?? "null"}"
                            + DrainFailureSummary(drainResult?.AdministrativeFailure)
                            + "."
                    );
                }
            }

            throw new PerfObservationException(
                $"DocumentCache administrative drain did not empty work after {maxSlices.ToString(CultureInfo.InvariantCulture)} slices."
            );
        }

        private async Task<
            IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics>
        > DrainWhileWritesRunAsync(Task writesTask)
        {
            List<DocumentCacheQualificationDrainSliceMetrics> slices = [];
            int slice = 0;
            while (!writesTask.IsCompleted || (await CaptureCountsAsync()).DocumentProjectionWorkRows > 0)
            {
                slice++;
                DocumentCacheProjectionSchedulerDispatchResult dispatchResult =
                    await RunCommandOwnedDrainSliceAsync();
                DocumentCacheQualificationDrainSliceMetrics sliceMetric = DrainSliceMetric(
                    slice,
                    dispatchResult
                );
                slices.Add(sliceMetric);

                DocumentCacheProjectionDrainPageResult? drainResult = dispatchResult.DrainResult;
                if (drainResult?.Outcome == DocumentCacheProjectionDrainPageOutcome.NoEligibleWork)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10));
                    continue;
                }

                if (
                    dispatchResult.Status != DocumentCacheProjectionSchedulerDispatchStatus.Dispatched
                    || drainResult?.Outcome != DocumentCacheProjectionDrainPageOutcome.PageProcessed
                    || drainResult.DocumentScopedFailureCount != 0
                    || drainResult.AdministrativeFailure is not null
                )
                {
                    throw new PerfObservationException(
                        "Same-document contention drain did not process a clean page; "
                            + $"status={dispatchResult.Status}, outcome={drainResult?.Outcome.ToString() ?? "null"}"
                            + DrainFailureSummary(drainResult?.AdministrativeFailure)
                            + "."
                    );
                }
            }

            return slices;
        }

        private async Task<DocumentCacheProjectionSchedulerDispatchResult> RunCommandOwnedDrainSliceAsync()
        {
            DocumentCacheProjectionTargetRuntimeContext targetContext =
                await RefreshSingleTargetContextAsync();
            DocumentCacheQualificationDrainSliceWorkflow workflow = new(Scheduler);
            DocumentCacheAdministrativeCommandResult result = await AdministrativeCommandRunner.ExecuteAsync(
                new DocumentCacheAdministrativeCommandRunnerRequest(
                    DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                    DocumentCacheAdministrativeTargetKey.FromTargetKey(
                        targetContext.TargetExecutionContext.TargetKey
                    ),
                    targetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                    confirmation: DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
                ),
                workflow
            );

            if (workflow.DispatchResult is { } dispatchResult)
            {
                return dispatchResult;
            }

            throw new PerfObservationException(
                "DocumentCache command-owned drain did not reach the scheduler; "
                    + $"commandStatus={result.Status}, classification={result.Classification}, "
                    + $"diagnostics={DiagnosticSummary(result)}."
            );
        }

        private static DocumentCacheQualificationDrainSliceMetrics DrainSliceMetric(
            int sliceNumber,
            DocumentCacheProjectionSchedulerDispatchResult dispatchResult
        )
        {
            DocumentCacheProjectionDrainPageResult? drainResult = dispatchResult.DrainResult;
            DocumentCacheAdministrativeDrainFailure? failure = drainResult?.AdministrativeFailure;
            double elapsedMs = dispatchResult.CompletedAt is null
                ? 0
                : (dispatchResult.CompletedAt.Value - dispatchResult.ObservedAt).TotalMilliseconds;

            return new DocumentCacheQualificationDrainSliceMetrics(
                sliceNumber,
                dispatchResult.Status.ToString(),
                drainResult?.Outcome.ToString(),
                drainResult?.ProcessedItemCount,
                drainResult?.AcknowledgedOrRemovedItemCount,
                drainResult?.DocumentScopedFailureCount,
                elapsedMs,
                failure?.Status.ToString(),
                failure?.Classification.ToString(),
                failure?.DiagnosticCategory.ToString(),
                failure?.Message,
                failure?.Retryable,
                failure?.AffectedDocumentIds.IsDefaultOrEmpty == false
                    ? [.. failure.AffectedDocumentIds]
                    : null
            );
        }

        private sealed class DocumentCacheQualificationDrainSliceWorkflow(
            IDocumentCacheProjectionScheduler scheduler
        ) : IDocumentCacheAdministrativeCommandWorkflow
        {
            public DocumentCacheProjectionSchedulerDispatchResult? DispatchResult { get; private set; }

            public Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
                DocumentCacheAdministrativeCommandExecutionContext context,
                CancellationToken cancellationToken
            )
            {
                ArgumentNullException.ThrowIfNull(context);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    context.EligiblePreflightResult(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                );
            }

            public async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
                DocumentCacheAdministrativeCommandExecutionContext context,
                CancellationToken cancellationToken
            )
            {
                ArgumentNullException.ThrowIfNull(context);
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.DrainWork);

                DispatchResult = await scheduler
                    .RunAdministrativeDrainSliceAsync(context.TargetContext, cancellationToken)
                    .ConfigureAwait(false);

                DocumentCacheProjectionDrainPageResult? drainResult = DispatchResult.DrainResult;
                if (
                    DispatchResult.Status != DocumentCacheProjectionSchedulerDispatchStatus.Dispatched
                    || drainResult is null
                )
                {
                    return context.Failed(
                        DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                        DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                        DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                        "Command-owned drain slice did not dispatch to the target.",
                        retryable: false
                    );
                }

                if (drainResult.AdministrativeFailure is { } failure)
                {
                    return context.Failed(
                        failure.Status,
                        failure.Classification,
                        failure.DiagnosticCategory,
                        failure.Message,
                        failure.Retryable,
                        failure.AffectedDocumentIds
                    );
                }

                if (
                    drainResult.Outcome == DocumentCacheProjectionDrainPageOutcome.PageProcessed
                    && (drainResult.ProcessedItemCount > 0 || drainResult.AcknowledgedOrRemovedItemCount > 0)
                )
                {
                    context.MarkMutated();
                }

                context.CompletePhase(DocumentCacheAdministrativeCommandPhase.DrainWork);
                return context.Completed();
            }
        }

        private async Task<DocumentCacheQualificationWriteBatchMetrics> WriteStudentBatchAsync(
            long firstOrdinal,
            int warmupCount,
            int measuredCount,
            string phase,
            Func<Task>? afterEachWriteAsync = null
        )
        {
            int totalCount = checked(warmupCount + measuredCount);
            GuardOrdinalRange(firstOrdinal, totalCount, phase);

            List<double> measuredSamples = [];
            List<DocumentCacheQualificationWriteFailure> failures = [];

            for (int index = 0; index < totalCount; index++)
            {
                long ordinal = firstOrdinal + index;
                DocumentCacheWriteObservation observation = await PutStudentAsync(ordinal, phase, index);
                if (index >= warmupCount && observation.Failure is null)
                {
                    measuredSamples.Add(observation.ElapsedMilliseconds);
                }

                if (observation.Failure is not null)
                {
                    failures.Add(observation.Failure);
                    break;
                }

                if (afterEachWriteAsync is not null)
                {
                    await afterEachWriteAsync();
                }
            }

            return new DocumentCacheQualificationWriteBatchMetrics(
                firstOrdinal,
                firstOrdinal + totalCount - 1,
                warmupCount,
                measuredCount,
                measuredSamples.Count,
                failures,
                measuredSamples.Count == 0 ? null : PerfLatencyMeasurement.Summarize(measuredSamples)
            );
        }

        private async Task<DocumentCacheQualificationWriteBatchMetrics> WriteSameDocumentConcurrentlyAsync(
            string phase
        )
        {
            long ordinal = SameDocumentOrdinal();
            Task<DocumentCacheWriteObservation>[] writeTasks =
            [
                .. Enumerable
                    .Range(0, _configuration.SameDocumentContention)
                    .Select(index => PutStudentAsync(ordinal, phase, index)),
            ];
            DocumentCacheWriteObservation[] observations = await Task.WhenAll(writeTasks);
            List<double> samples =
            [
                .. observations
                    .Where(observation => observation.Failure is null)
                    .Select(observation => observation.ElapsedMilliseconds),
            ];
            List<DocumentCacheQualificationWriteFailure> failures =
            [
                .. observations
                    .Select(observation => observation.Failure)
                    .Where(failure => failure is not null)
                    .Select(failure => failure!),
            ];

            return new DocumentCacheQualificationWriteBatchMetrics(
                ordinal,
                ordinal,
                0,
                _configuration.SameDocumentContention,
                samples.Count,
                failures,
                samples.Count == 0 ? null : PerfLatencyMeasurement.Summarize(samples)
            );
        }

        private async Task<DocumentCacheWriteObservation> PutStudentAsync(
            long ordinal,
            string phase,
            int iteration
        )
        {
            Guid documentUuid = PerfFixtureDefinition.DocumentUuidFor(ordinal);
            JsonObject payload = StudentPayload(ordinal, documentUuid, phase, iteration);
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                using StringContent content = new(
                    payload.ToJsonString(),
                    Encoding.UTF8,
                    StandardJsonContentType
                );
                using HttpResponseMessage response = await _harness.HttpClient.PutAsync(
                    $"{PerfFixtureDefinition.ResourceEndpoint}/{documentUuid:D}",
                    content
                );
                string responseBody = await response.Content.ReadAsStringAsync();
                stopwatch.Stop();

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return new DocumentCacheWriteObservation(
                        stopwatch.Elapsed.TotalMilliseconds,
                        Failure: null
                    );
                }

                return new DocumentCacheWriteObservation(
                    stopwatch.Elapsed.TotalMilliseconds,
                    new DocumentCacheQualificationWriteFailure(
                        ordinal,
                        documentUuid.ToString("D"),
                        (int)response.StatusCode,
                        TrimDiagnostic(responseBody)
                    )
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                stopwatch.Stop();
                return new DocumentCacheWriteObservation(
                    stopwatch.Elapsed.TotalMilliseconds,
                    new DocumentCacheQualificationWriteFailure(
                        ordinal,
                        documentUuid.ToString("D"),
                        StatusCode: null,
                        TrimDiagnostic(exception.Message)
                    )
                );
            }
        }

        private static JsonObject StudentPayload(
            long ordinal,
            Guid documentUuid,
            string phase,
            int iteration
        ) =>
            new()
            {
                ["id"] = documentUuid.ToString("D"),
                ["studentUniqueId"] = PerfFixtureDefinition.StudentUniqueIdFor(ordinal),
                ["firstName"] = FirstNameFor(phase, iteration),
                ["lastSurname"] = PerfFixtureDefinition.LastSurname,
                ["birthDate"] = PerfFixtureDefinition.BirthDateIso,
                ["birthSexDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                    PerfFixtureDefinition.SexDescriptorResource
                ),
                ["otherNames"] = new JsonArray(
                    new JsonObject
                    {
                        ["otherNameTypeDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                            PerfFixtureDefinition.OtherNameTypeDescriptorResource
                        ),
                        ["firstName"] = PerfFixtureDefinition.FirstName,
                        ["lastSurname"] = PerfFixtureDefinition.LastSurname,
                    }
                ),
                ["identificationDocuments"] = new JsonArray(
                    new JsonObject
                    {
                        ["identificationDocumentUseDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                            PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource
                        ),
                        ["personalInformationVerificationDescriptor"] =
                            PerfFixtureDefinition.DescriptorUriFor(
                                PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource
                            ),
                    }
                ),
                ["personalIdentificationDocuments"] = new JsonArray(
                    new JsonObject
                    {
                        ["identificationDocumentUseDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                            PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource
                        ),
                        ["personalInformationVerificationDescriptor"] =
                            PerfFixtureDefinition.DescriptorUriFor(
                                PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource
                            ),
                    }
                ),
                ["visas"] = new JsonArray(
                    new JsonObject
                    {
                        ["visaDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                            PerfFixtureDefinition.VisaDescriptorResource
                        ),
                    }
                ),
            };

        private async Task<DocumentCacheStatusSamplingResult> SampleStatusLatencyAsync()
        {
            DocumentCacheStatusResponse? lastStatus = null;
            PerfLatencySummary summary = await PerfLatencyMeasurement.MeasureAsync(
                async _ =>
                {
                    lastStatus = await StatusService.GetStatusAsync(
                        evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
                    );
                },
                _configuration.WarmupStatusSamples,
                _configuration.MeasuredStatusSamples
            );

            return new DocumentCacheStatusSamplingResult(
                summary,
                lastStatus
                    ?? throw new PerfObservationException("DocumentCache status sampling produced no status.")
            );
        }

        private async Task<DocumentCacheProjectionTargetRuntimeContext> RefreshSingleTargetContextAsync()
        {
            await Supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);
            IReadOnlyList<DocumentCacheProjectionTargetRuntimeContext> contexts =
            [
                .. Supervisor.CurrentTargetContexts,
            ];
            if (contexts.Count != 1)
            {
                throw new PerfObservationException(
                    $"DocumentCache representative pipeline expected exactly one target context; observed {contexts.Count}."
                );
            }

            return contexts[0];
        }

        private static DocumentCacheStatusTarget RequireSingleStatusTarget(DocumentCacheStatusResponse status)
        {
            if (status.Targets.Length != 1)
            {
                throw new PerfObservationException(
                    $"DocumentCache status expected exactly one target; observed {status.Targets.Length}."
                );
            }

            return status.Targets[0];
        }

        private void GuardTargetIdentity(DocumentCacheTargetExecutionContext executionContext)
        {
            RelationalProviderToken expectedProviderToken =
                _provider == PerfProvider.Postgresql
                    ? RelationalProviderToken.Postgresql
                    : RelationalProviderToken.SqlServer;

            if (executionContext.TargetKey.TenantKey != ExpectedHarnessTenantKey)
            {
                throw new PerfObservationException(
                    "DocumentCache representative target resolved an unexpected tenant key."
                );
            }

            if (executionContext.TargetKey.DataStoreId != ExpectedHarnessDataStoreId)
            {
                throw new PerfObservationException(
                    "DocumentCache representative target resolved an unexpected data store id."
                );
            }

            if (executionContext.ProviderToken != expectedProviderToken)
            {
                throw new PerfObservationException(
                    $"DocumentCache target provider was {executionContext.ProviderToken.Value}; "
                        + $"expected {expectedProviderToken.Value}."
                );
            }

            if (
                !string.Equals(
                    executionContext.ConnectionInput.Value,
                    _leasedConnectionString,
                    StringComparison.Ordinal
                )
            )
            {
                throw new PerfObservationException(
                    "DocumentCache target connection identity does not match the leased integration database."
                );
            }
        }

        private void GuardEffectiveSettings(DocumentCacheTargetEffectiveSettings settings)
        {
            if (!settings.ReadAccelerationEnabled)
            {
                throw new PerfObservationException(
                    "DocumentCache representative pipeline requires read acceleration enabled."
                );
            }

            if (settings.ProjectorPageSize != _configuration.PageSize)
            {
                throw new PerfObservationException(
                    $"DocumentCache projector page size was {settings.ProjectorPageSize}; expected {_configuration.PageSize}."
                );
            }

            if (settings.ProjectorMaxConcurrentTargets != _configuration.ProjectorConcurrency)
            {
                throw new PerfObservationException(
                    "DocumentCache projector max concurrent targets was "
                        + $"{settings.ProjectorMaxConcurrentTargets}; expected {_configuration.ProjectorConcurrency}."
                );
            }

            if (settings.ProjectorBaselineHighWaterMark != _configuration.HighWaterMark)
            {
                throw new PerfObservationException(
                    "DocumentCache projector baseline high-water mark was "
                        + $"{settings.ProjectorBaselineHighWaterMark}; expected {_configuration.HighWaterMark}."
                );
            }
        }

        private static void GuardProviderPrerequisites(DocumentCacheTargetExecutionContext executionContext)
        {
            if (executionContext.Inventory.Status != DocumentCacheInventoryStatus.Satisfied)
            {
                throw new PerfObservationException(
                    $"DocumentCache inventory prerequisite was {executionContext.Inventory.Status}: {executionContext.Inventory.Message}"
                );
            }

            if (executionContext.EnqueueTrigger.Status != DocumentCacheEnqueueTriggerStatus.Satisfied)
            {
                throw new PerfObservationException(
                    "DocumentCache enqueue trigger prerequisite was "
                        + $"{executionContext.EnqueueTrigger.Status}: {executionContext.EnqueueTrigger.Message}"
                );
            }

            if (executionContext.SqlServerPrerequisites is { HasFailure: true } sqlServerPrerequisites)
            {
                throw new PerfObservationException(
                    "DocumentCache SQL Server prerequisites failed: "
                        + $"{sqlServerPrerequisites.ReadCommittedSnapshot.Status}/"
                        + $"{sqlServerPrerequisites.NestedTriggers.Status}."
                );
            }
        }

        private static void GuardStatusTargetPreflight(DocumentCacheStatusResponse status)
        {
            DocumentCacheStatusTarget target = RequireSingleStatusTarget(status);
            if (target.Eligibility.Status != DocumentCacheStatusEligibilityStatus.Eligible)
            {
                throw new PerfObservationException(
                    $"DocumentCache status target was not eligible: {target.Eligibility.Status}."
                );
            }

            if (target.Inventory.State.Status != DocumentCacheStatusInventoryStatus.Valid)
            {
                throw new PerfObservationException(
                    $"DocumentCache status inventory state was not valid: {target.Inventory.State.Status}."
                );
            }

            if (target.Inventory.EnqueueTrigger.Status != DocumentCacheStatusEnqueueTriggerStatus.Enabled)
            {
                throw new PerfObservationException(
                    "DocumentCache status enqueue trigger was not enabled: "
                        + $"{target.Inventory.EnqueueTrigger.Status}."
                );
            }

            if (
                target.ProviderPrerequisites.Status
                is not (
                    DocumentCacheStatusProviderPrerequisiteStatus.Satisfied
                    or DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable
                )
            )
            {
                throw new PerfObservationException(
                    "DocumentCache status provider prerequisites were not satisfied: "
                        + $"{target.ProviderPrerequisites.Status}."
                );
            }
        }

        private static void AssertAdministrativeCommandCompleted(
            string phase,
            DocumentCacheAdministrativeCommandResult result,
            DocumentCacheLifecycleState expectedLifecycle
        )
        {
            if (
                result.Status != DocumentCacheAdministrativeCommandStatus.Completed
                || result.Classification != DocumentCacheAdministrativeCommandClassification.Succeeded
                || result.Lifecycle != expectedLifecycle
            )
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' administrative command failed: "
                        + $"status={result.Status}, classification={result.Classification}, lifecycle={result.Lifecycle?.ToString() ?? "null"}."
                );
            }
        }

        private static void AssertTrackingCaughtUp(string phase, DocumentCacheStatusTarget target)
        {
            if (target.Lifecycle.State != DocumentCacheStatusLifecycleState.Tracking)
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' expected Tracking lifecycle; observed {target.Lifecycle.State}."
                );
            }

            if (target.CacheAhead.State != DocumentCacheStatusCacheAheadState.Clear)
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' expected clear cache-ahead state; observed {target.CacheAhead.State}."
                );
            }

            if (target.QueueSummary.Presence != DocumentCacheStatusQueuePresence.Empty)
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' expected empty work queue; observed {target.QueueSummary.Presence}."
                );
            }

            if (target.CaughtUp.Status != DocumentCacheCaughtUpStatus.CaughtUp)
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' expected caught-up status; observed {target.CaughtUp.Status}."
                );
            }
        }

        private static void AssertTrackingNotCaughtUp(string phase, DocumentCacheStatusTarget target)
        {
            if (target.Lifecycle.State != DocumentCacheStatusLifecycleState.Tracking)
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' expected Tracking lifecycle; observed {target.Lifecycle.State}."
                );
            }

            if (target.QueueSummary.Presence != DocumentCacheStatusQueuePresence.NotEmpty)
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' expected non-empty work queue; observed {target.QueueSummary.Presence}."
                );
            }
        }

        private async Task<DocumentCacheQualificationPhaseCounts> CaptureCountsAsync()
        {
            await EnsureConnectionOpenAsync();
            await using DbCommand command = _harness.DbConnection.CreateCommand();
            command.CommandText =
                _provider == PerfProvider.Postgresql
                    ? """
                        SELECT
                            (SELECT COUNT(*) FROM "edfi"."Student") AS "SourceDocumentRows",
                            (SELECT COUNT(*) FROM "dms"."Document") AS "DmsDocumentRows",
                            (SELECT COUNT(*) FROM "dms"."DocumentCache") AS "DocumentCacheRows",
                            (SELECT COUNT(*) FROM "dms"."DocumentProjectionWork") AS "DocumentProjectionWorkRows",
                            (SELECT "ProjectionLifecycleState" FROM "dms"."DocumentCacheState" WHERE "StateId" = 1) AS "ProjectionLifecycleState",
                            (SELECT "CacheAheadRecoveryRequired" FROM "dms"."DocumentCacheState" WHERE "StateId" = 1) AS "CacheAheadRecoveryRequired";
                        """
                    : """
                        SELECT
                            (SELECT COUNT_BIG(1) FROM [edfi].[Student]) AS [SourceDocumentRows],
                            (SELECT COUNT_BIG(1) FROM [dms].[Document]) AS [DmsDocumentRows],
                            (SELECT COUNT_BIG(1) FROM [dms].[DocumentCache]) AS [DocumentCacheRows],
                            (SELECT COUNT_BIG(1) FROM [dms].[DocumentProjectionWork]) AS [DocumentProjectionWorkRows],
                            (SELECT [ProjectionLifecycleState] FROM [dms].[DocumentCacheState] WHERE [StateId] = 1) AS [ProjectionLifecycleState],
                            (SELECT [CacheAheadRecoveryRequired] FROM [dms].[DocumentCacheState] WHERE [StateId] = 1) AS [CacheAheadRecoveryRequired];
                        """;

            await using DbDataReader reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new PerfObservationException("DocumentCache phase count query returned no rows.");
            }

            string lifecycle = RequiredString(reader, "ProjectionLifecycleState");
            return new DocumentCacheQualificationPhaseCounts(
                _providerName,
                RequiredInt64(reader, "SourceDocumentRows"),
                RequiredInt64(reader, "DmsDocumentRows"),
                RequiredInt64(reader, "DocumentCacheRows"),
                RequiredInt64(reader, "DocumentProjectionWorkRows"),
                lifecycle,
                RequiredBoolean(reader, "CacheAheadRecoveryRequired")
            );
        }

        private async Task<DocumentProjectionWorkDmlSnapshot> CaptureDocumentProjectionWorkDmlSnapshotAsync()
        {
            await EnsureConnectionOpenAsync();
            if (_provider == PerfProvider.Postgresql)
            {
                await ExecuteNonQueryAsync("SELECT pg_stat_force_next_flush();");
            }

            await using DbCommand command = _harness.DbConnection.CreateCommand();
            command.CommandText =
                _provider == PerfProvider.Postgresql
                    ? """
                        SELECT
                            COALESCE(n_tup_ins, 0) AS "Inserted",
                            COALESCE(n_tup_upd, 0) AS "Updated",
                            COALESCE(n_tup_del, 0) AS "Deleted"
                        FROM pg_stat_user_tables
                        WHERE schemaname = 'dms'
                          AND relname = 'DocumentProjectionWork';
                        """
                    : """
                        SELECT
                            COALESCE(SUM(leaf_insert_count), 0) AS [Inserted],
                            COALESCE(SUM(leaf_update_count), 0) AS [Updated],
                            COALESCE(SUM(leaf_delete_count), 0) AS [Deleted]
                        FROM sys.dm_db_index_operational_stats(
                            DB_ID(),
                            OBJECT_ID(N'dms.DocumentProjectionWork'),
                            NULL,
                            NULL
                        )
                        WHERE index_id IN (0, 1);
                        """;

            await using DbDataReader reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new PerfObservationException(
                    "DocumentProjectionWork DML counter query returned no rows."
                );
            }

            return new(
                RequiredInt64(reader, "Inserted"),
                RequiredInt64(reader, "Updated"),
                RequiredInt64(reader, "Deleted")
            );
        }

        private async Task SetLifecycleAsync(
            DocumentCacheLifecycleState lifecycle,
            bool cacheAheadRecoveryRequired
        ) =>
            await ExecuteNonQueryAsync(
                _provider == PerfProvider.Postgresql
                    ? """
                    UPDATE "dms"."DocumentCacheState"
                    SET "ProjectionLifecycleState" = @lifecycle,
                        "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
                    WHERE "StateId" = 1;
                    """
                    : """
                    UPDATE [dms].[DocumentCacheState]
                    SET [ProjectionLifecycleState] = @lifecycle,
                        [CacheAheadRecoveryRequired] = @cacheAheadRecoveryRequired
                    WHERE [StateId] = 1;
                    """,
                ("@lifecycle", lifecycle.ToString()),
                ("@cacheAheadRecoveryRequired", cacheAheadRecoveryRequired)
            );

        private async Task ClearDocumentCacheAsync() =>
            await ExecuteNonQueryAsync(
                _provider == PerfProvider.Postgresql
                    ? """DELETE FROM "dms"."DocumentCache";"""
                    : """DELETE FROM [dms].[DocumentCache];"""
            );

        private async Task ClearProjectionWorkAsync() =>
            await ExecuteNonQueryAsync(
                _provider == PerfProvider.Postgresql
                    ? """DELETE FROM "dms"."DocumentProjectionWork";"""
                    : """DELETE FROM [dms].[DocumentProjectionWork];"""
            );

        private async Task InsertProjectionWorkForLeadingDocumentsAsync(int rowCount)
        {
            await ExecuteNonQueryAsync(
                _provider == PerfProvider.Postgresql
                    ? """
                    INSERT INTO "dms"."DocumentProjectionWork" (
                        "DocumentId",
                        "RequiredContentVersion",
                        "FirstEnqueuedAt",
                        "LastEnqueuedAt"
                    )
                    SELECT "DocumentId",
                           "ContentVersion",
                           @firstEnqueuedAt,
                           @lastEnqueuedAt
                    FROM "dms"."Document"
                    ORDER BY "DocumentId"
                    LIMIT @rowCount;
                    """
                    : """
                    INSERT INTO [dms].[DocumentProjectionWork] (
                        [DocumentId],
                        [RequiredContentVersion],
                        [FirstEnqueuedAt],
                        [LastEnqueuedAt]
                    )
                    SELECT TOP (@rowCount) [DocumentId],
                                           [ContentVersion],
                                           @firstEnqueuedAt,
                                           @lastEnqueuedAt
                    FROM [dms].[Document]
                    ORDER BY [DocumentId];
                    """,
                ("@rowCount", rowCount),
                ("@firstEnqueuedAt", DateTime.UtcNow),
                ("@lastEnqueuedAt", DateTime.UtcNow)
            );
        }

        private async Task ExecuteNonQueryAsync(
            string commandText,
            params (string Name, object Value)[] parameters
        )
        {
            await EnsureConnectionOpenAsync();
            await using DbCommand command = _harness.DbConnection.CreateCommand();
            command.CommandText = commandText;
            foreach ((string name, object value) in parameters)
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            await command.ExecuteNonQueryAsync();
        }

        private async Task EnsureConnectionOpenAsync()
        {
            if (_harness.DbConnection.State != ConnectionState.Open)
            {
                await _harness.DbConnection.OpenAsync();
            }
        }

        private DocumentCacheQualificationPhaseMetrics CreatePhaseMetrics(
            string phase,
            TimeSpan elapsed,
            DocumentCacheQualificationPhaseCounts countsBefore,
            DocumentCacheQualificationPhaseCounts countsAfter,
            IEnumerable<DocumentCacheQualificationPhaseMetricValue> metrics,
            PerfLatencySummary? latency = null,
            DocumentCacheQualificationWriteBatchMetrics? writeBatch = null,
            DocumentCacheAdministrativeCommandResult? commandResult = null,
            DocumentCacheStatusResponse? statusSnapshot = null,
            IReadOnlyList<DocumentCacheQualificationDrainSliceMetrics>? drainSlices = null
        ) =>
            DocumentCacheQualificationPhaseMetrics.Create(
                _providerName,
                phase,
                UtcTimestamp(),
                elapsed,
                countsBefore,
                countsAfter,
                metrics,
                latency,
                writeBatch,
                commandResult,
                statusSnapshot,
                drainSlices
            );

        private void WritePhaseArtifacts(
            string phase,
            DocumentCacheQualificationPhaseMetrics metrics,
            IReadOnlyList<string> transcriptObservations
        )
        {
            DocumentCacheQualificationPhaseDefinition phaseDefinition = _phaseDefinitions.Single(definition =>
                definition.ArtifactStem == phase
            );
            WriteText($"phase-metrics/{_providerName}-{phase}.json", PerfArtifactJson.Serialize(metrics));
            WriteText(
                $"command-transcripts/{_providerName}-{phase}.md",
                BuildTranscript(phaseDefinition, metrics, transcriptObservations)
            );
            _completedPhaseStems.Add(phase);
        }

        private string BuildTranscript(
            DocumentCacheQualificationPhaseDefinition phase,
            DocumentCacheQualificationPhaseMetrics metrics,
            IReadOnlyList<string> observations
        )
        {
            StringBuilder builder = new();
            builder.Append("# ").Append(phase.Title).Append('\n').Append('\n');
            builder.Append("- Provider: `").Append(_providerName).Append("`.\n");
            builder.Append("- Phase: `").Append(phase.ArtifactStem).Append("`.\n");
            builder.Append("- Captured at: `").Append(metrics.CapturedAtUtc).Append("`.\n");
            builder
                .Append("- Elapsed milliseconds: `")
                .Append(metrics.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                .Append("`.\n");
            builder
                .Append("- Counts before: cache `")
                .Append(metrics.CountsBefore.DocumentCacheRows.ToString(CultureInfo.InvariantCulture))
                .Append("`, work `")
                .Append(
                    metrics.CountsBefore.DocumentProjectionWorkRows.ToString(CultureInfo.InvariantCulture)
                )
                .Append("`, lifecycle `")
                .Append(metrics.CountsBefore.ProjectionLifecycleState)
                .Append("`.\n");
            builder
                .Append("- Counts after: cache `")
                .Append(metrics.CountsAfter.DocumentCacheRows.ToString(CultureInfo.InvariantCulture))
                .Append("`, work `")
                .Append(metrics.CountsAfter.DocumentProjectionWorkRows.ToString(CultureInfo.InvariantCulture))
                .Append("`, lifecycle `")
                .Append(metrics.CountsAfter.ProjectionLifecycleState)
                .Append("`.\n");

            foreach (DocumentCacheQualificationPhaseMetricValue metric in metrics.Metrics)
            {
                builder
                    .Append("- Metric `")
                    .Append(metric.Name)
                    .Append("`: `")
                    .Append(metric.Value)
                    .Append("` ")
                    .Append(metric.Unit)
                    .Append(".\n");
            }

            foreach (string observation in observations)
            {
                builder.Append("- ").Append(observation).Append('\n');
            }

            return builder.ToString();
        }

        private void WriteQualificationSummary()
        {
            StringBuilder builder = new();
            builder.Append("# DocumentCache Qualification Phase Run\n\n");
            builder.Append("- Run id: `").Append(_setup.RunManifest.RunId).Append("`.\n");
            builder.Append("- Provider: `").Append(_providerName).Append("`.\n");
            builder
                .Append("- Fixture: `")
                .Append(_setup.FixtureManifest.FixtureId)
                .Append("` with `")
                .Append(_setup.FixtureManifest.RowCount.ToString(CultureInfo.InvariantCulture))
                .Append("` canonical documents.\n");
            builder
                .Append("- Outage writes: `")
                .Append(_configuration.OutageDistinctDocumentWrites.ToString(CultureInfo.InvariantCulture))
                .Append("` distinct documents.\n");
            builder
                .Append("- Same-document contenders: `")
                .Append(_configuration.SameDocumentContention.ToString(CultureInfo.InvariantCulture))
                .Append("` writers.\n");
            builder
                .Append("- Runtime phase artifacts: `")
                .Append(_completedPhaseStems.Count.ToString(CultureInfo.InvariantCulture))
                .Append(
                    "` metrics files under `phase-metrics/` and transcripts under `command-transcripts/`.\n"
                );
            builder
                .Append("- Completed phases: `")
                .Append(string.Join("`, `", _completedPhaseStems))
                .Append("`.\n");
            builder.Append(
                "- Provider metrics artifacts were written under `provider-metrics/`; threshold results are produced by subsequent qualification steps.\n"
            );

            WriteText("qualification-summary.md", builder.ToString());
        }

        private void WriteText(string relativePath, string content)
        {
            string fullPath = Path.Combine(
                _setup.RunDirectory,
                relativePath
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)
            );
            Directory.CreateDirectory(
                Path.GetDirectoryName(fullPath)
                    ?? throw new PerfArtifactValidationException([
                        $"DocumentCache qualification artifact path '{relativePath}' has no directory.",
                    ])
            );
            File.WriteAllText(fullPath, content, _utf8NoBom);
        }

        private static IReadOnlyList<DocumentCacheQualificationPhaseMetricValue> StatusMetrics(
            DocumentCacheStatusTarget target,
            string inventorySize
        ) =>
            [
                Metric("statusInventorySize", inventorySize),
                Metric("statusLifecycle", target.Lifecycle.State),
                Metric("queuePresence", target.QueueSummary.Presence),
                Metric("caughtUpStatus", target.CaughtUp.Status),
                Metric("oldestWorkAgeSeconds", target.QueueSummary.OldestWorkAgeSeconds, "s"),
                Metric("oldestWorkObserved", target.QueueSummary.OldestWorkFirstEnqueuedAt is not null),
                Metric("workRowsObserved", target.QueueSummary.Presence, "presence"),
            ];

        private static DocumentCacheQualificationPhaseMetricValue Metric(
            string name,
            object? value,
            string unit = "value"
        ) => new(name, FormatMetricValue(value), unit);

        private static string FormatMetricValue(object? value) =>
            value switch
            {
                null => "null",
                double doubleValue => doubleValue.ToString("G17", CultureInfo.InvariantCulture),
                float floatValue => floatValue.ToString("G9", CultureInfo.InvariantCulture),
                decimal decimalValue => decimalValue.ToString(CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };

        private static void ThrowIfWriteBatchFailed(
            string phase,
            DocumentCacheQualificationWriteBatchMetrics batch
        )
        {
            if (batch.Failures.Count == 0)
            {
                return;
            }

            DocumentCacheQualificationWriteFailure firstFailure = batch.Failures[0];
            throw new PerfObservationException(
                $"DocumentCache phase '{phase}' observed {batch.Failures.Count} failed HTTP writes; "
                    + $"first failure status={firstFailure.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "null"} "
                    + $"uuid={firstFailure.DocumentUuid}: {firstFailure.Message}"
            );
        }

        private void GuardOrdinalRange(long firstOrdinal, int count, string phase)
        {
            if (count < 1)
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' requires at least one write sample."
                );
            }

            long lastOrdinal = firstOrdinal + count - 1;
            if (firstOrdinal < 1 || lastOrdinal > _setup.FixtureManifest.RowCount)
            {
                throw new PerfObservationException(
                    $"DocumentCache phase '{phase}' requested ordinals {firstOrdinal}..{lastOrdinal}, "
                        + $"outside fixture row count {_setup.FixtureManifest.RowCount}."
                );
            }
        }

        private long FirstOutageOrdinal() =>
            _setup.FixtureManifest.RowCount - _configuration.OutageDistinctDocumentWrites + 1;

        private long SameDocumentOrdinal() => _setup.FixtureManifest.RowCount;

        private static string FirstNameFor(string phase, int iteration)
        {
            string normalized = new([.. phase.Where(IsAsciiLetterOrDigit).Take(24)]);
            return $"Perf{normalized}{iteration.ToString("D6", CultureInfo.InvariantCulture)}";
        }

        private static bool IsAsciiLetterOrDigit(char value) =>
            (value >= 'a' && value <= 'z')
            || (value >= 'A' && value <= 'Z')
            || (value >= '0' && value <= '9');

        private static bool HasPhaseDiagnosticOrResult(
            DocumentCacheAdministrativeCommandResult result,
            string phase
        ) =>
            result.PhaseDiagnostics.Any(diagnostic =>
                diagnostic.CurrentPhase.ToString().Contains(phase, StringComparison.OrdinalIgnoreCase)
            )
            || result.Status == DocumentCacheAdministrativeCommandStatus.Completed;

        private static string DrainFailureSummary(DocumentCacheAdministrativeDrainFailure? failure)
        {
            if (failure is null)
            {
                return string.Empty;
            }

            return $", administrativeFailureStatus={failure.Status}, classification={failure.Classification}, "
                + $"category={failure.DiagnosticCategory}, retryable={failure.Retryable}, "
                + $"affectedDocumentIds={AffectedDocumentIdsSummary(failure.AffectedDocumentIds)}, "
                + $"message={TrimDiagnostic(failure.Message)}";
        }

        private static string DiagnosticSummary(DocumentCacheAdministrativeCommandResult result)
        {
            if (result.PhaseDiagnostics.IsDefaultOrEmpty)
            {
                return "none";
            }

            return string.Join(
                " | ",
                result.PhaseDiagnostics.Select(diagnostic =>
                    $"{diagnostic.CurrentPhase}:{diagnostic.DiagnosticCategory}:{TrimDiagnostic(diagnostic.Message)}"
                )
            );
        }

        private static string AffectedDocumentIdsSummary(IEnumerable<long> affectedDocumentIds)
        {
            string summary = string.Join(
                ",",
                affectedDocumentIds
                    .Take(10)
                    .Select(documentId => documentId.ToString(CultureInfo.InvariantCulture))
            );

            return string.IsNullOrEmpty(summary) ? "[]" : $"[{summary}]";
        }

        private static long RequiredInt64(DbDataReader reader, string name)
        {
            int ordinal = reader.GetOrdinal(name);
            if (reader.IsDBNull(ordinal))
            {
                throw new PerfObservationException($"DocumentCache phase count '{name}' was null.");
            }

            return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static string RequiredString(DbDataReader reader, string name)
        {
            int ordinal = reader.GetOrdinal(name);
            if (reader.IsDBNull(ordinal))
            {
                throw new PerfObservationException($"DocumentCache phase count '{name}' was null.");
            }

            return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
                ?? throw new PerfObservationException($"DocumentCache phase count '{name}' was null.");
        }

        private static bool RequiredBoolean(DbDataReader reader, string name)
        {
            int ordinal = reader.GetOrdinal(name);
            if (reader.IsDBNull(ordinal))
            {
                throw new PerfObservationException($"DocumentCache phase count '{name}' was null.");
            }

            return Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static string TrimDiagnostic(string value)
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
            return normalized.Length <= 512 ? normalized : normalized[..512];
        }

        private static string UtcTimestamp() =>
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        private sealed record DocumentProjectionWorkDmlSnapshot(long Inserted, long Updated, long Deleted)
        {
            public long Total => Inserted + Updated + Deleted;

            public DocumentProjectionWorkDmlSnapshot DeltaFrom(
                DocumentProjectionWorkDmlSnapshot before,
                string phase
            )
            {
                long insertedDelta = Inserted - before.Inserted;
                long updatedDelta = Updated - before.Updated;
                long deletedDelta = Deleted - before.Deleted;
                if (insertedDelta < 0 || updatedDelta < 0 || deletedDelta < 0)
                {
                    throw new PerfObservationException(
                        $"DocumentCache phase '{phase}' observed a negative DocumentProjectionWork DML counter delta; "
                            + "database statistics were reset during the measured phase."
                    );
                }

                return new(insertedDelta, updatedDelta, deletedDelta);
            }
        }
    }

    private sealed record DocumentCacheQualificationPhaseDefinition(string ArtifactStem, string Title);

    private sealed record DocumentCacheWriteObservation(
        double ElapsedMilliseconds,
        DocumentCacheQualificationWriteFailure? Failure
    );

    private sealed record DocumentCacheStatusSamplingResult(
        PerfLatencySummary Latency,
        DocumentCacheStatusResponse StatusSnapshot
    );
}
