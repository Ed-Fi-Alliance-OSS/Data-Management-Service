// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// The final-gate run pipeline. The primary run spans three phases over one shared load —
/// pristine (the traditional rerun plus unfiltered cursor/partition cells, against data
/// byte-identical to the baseline capture), authorized (after the authorization seeding),
/// and filtered (after the overlay) — each phase measured under its own host so the
/// principal is boot-time real, with the accumulator structurally enforcing the phase
/// order. The descriptor run is a separate single-phase pipeline over its own fixture.
/// Guardrails run before any measurement; artifacts are validated, written, reloaded, and
/// validated again, because the committed evidence is the files.
/// </summary>
public static class PerfFinalGateRunPipeline
{
    public static async Task<PerfFinalGateRunAccumulator> RunPristinePhaseAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        PerfFixtureDefinition definition,
        long deepOffset,
        int warmupIterations,
        int measuredIterations,
        string runnerCommit,
        PerfEvidenceRunSettings settings
    )
    {
        PerfBaselineRunPipeline.GuardCiEnvironment(
            settings.AllowCi,
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS")
        );
        string subjectCommit = GitIdentity.HeadCommit(AppContext.BaseDirectory);
        IReadOnlyList<string> dirtyPaths = GitIdentity.DirtyPaths(AppContext.BaseDirectory);
        if (!settings.AllowAnyDirtyPath)
        {
            PerfBaselineRunPipeline.GuardDirtyPaths(dirtyPaths, settings.AllowedDirtyPrefixes);
        }

        await PerfFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);

        PerfFinalGateRunAccumulator accumulator = new(
            provider,
            definition,
            deepOffset,
            warmupIterations,
            measuredIterations,
            runnerCommit,
            subjectCommit,
            dirtyPaths
        );

        accumulator.BeginPhase(PerfPrimaryPhase.Pristine);
        await MeasurePhaseCellsAsync(
            accumulator,
            harness,
            openReplayConnectionAsync,
            PerfPrimaryPhase.Pristine
        );
        accumulator.CompletePhase(PerfPrimaryPhase.Pristine);
        return accumulator;
    }

    public static async Task RunAuthorizedPhaseAsync(
        PerfFinalGateRunAccumulator accumulator,
        ApiIntegrationHarness harness,
        Func<Task<DbConnection>> openReplayConnectionAsync
    )
    {
        accumulator.BeginPhase(PerfPrimaryPhase.AuthorizedSeeded);

        PerfAuthorizationSeedDefinition seed = new(accumulator.Definition);
        await PerfAuthorizationSeeder.SeedAndVerifyAsync(harness.DbConnection, accumulator.Provider, seed);
        accumulator.RecordMutation(
            new PerfFinalGatePhaseLogEntry(
                PerfFinalGateScenarios.PhaseName(PerfPrimaryPhase.AuthorizedSeeded),
                "Seeded one school, one grade-level descriptor, and one StudentSchoolAssociation per "
                    + "even-ordinal student through durable source tables; the generated authorization "
                    + "view and hierarchy self-edge are trigger-fed.",
                [
                    new PerfSetting(
                        "enrolledStudentCount",
                        seed.EnrolledStudentCount.ToString(CultureInfo.InvariantCulture)
                    ),
                    new PerfSetting(
                        "claimEducationOrganizationId",
                        PerfAuthorizationSeedDefinition.SchoolId.ToString(CultureInfo.InvariantCulture)
                    ),
                    new PerfSetting(
                        "ssaDocumentIdBase",
                        seed.SsaDocumentIdBase.ToString(CultureInfo.InvariantCulture)
                    ),
                ]
            )
        );

        await MeasurePhaseCellsAsync(
            accumulator,
            harness,
            openReplayConnectionAsync,
            PerfPrimaryPhase.AuthorizedSeeded
        );
        accumulator.CompletePhase(PerfPrimaryPhase.AuthorizedSeeded);
    }

    public static async Task<string> RunFilteredPhaseAndWriteAsync(
        PerfFinalGateRunAccumulator accumulator,
        ApiIntegrationHarness harness,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        string resultsDirectoryBase,
        PerfEvidenceRunSettings settings
    )
    {
        accumulator.BeginPhase(PerfPrimaryPhase.FilteredOverlay);

        await PerfFilteredOverlay.ApplyAndVerifyAsync(
            harness.DbConnection,
            accumulator.Provider,
            accumulator.Definition
        );
        accumulator.RecordMutation(
            new PerfFinalGatePhaseLogEntry(
                PerfFinalGateScenarios.PhaseName(PerfPrimaryPhase.FilteredOverlay),
                "Varied every tenth student's birth date in place, at equal ISO text length, so the "
                    + "birthDate equality filter selects exactly ten percent of the candidate set.",
                [
                    new PerfSetting(
                        "overlaidStudentCount",
                        PerfFilteredOverlay
                            .OverlaidStudentCount(accumulator.Definition)
                            .ToString(CultureInfo.InvariantCulture)
                    ),
                    new PerfSetting("overlayBirthDate", PerfFilteredOverlay.OverlayBirthDateIso),
                    new PerfSetting(
                        "overlaidDocumentIdSum",
                        PerfFilteredOverlay
                            .OverlaidDocumentIdSum(accumulator.Definition)
                            .ToString(CultureInfo.InvariantCulture)
                    ),
                ]
            )
        );

        await MeasurePhaseCellsAsync(
            accumulator,
            harness,
            openReplayConnectionAsync,
            PerfPrimaryPhase.FilteredOverlay
        );
        accumulator.CompletePhase(PerfPrimaryPhase.FilteredOverlay);

        if (!accumulator.AllPhasesComplete)
        {
            throw new PerfObservationException("All primary phases must complete before writing.");
        }

        string fixtureManifestJson = PerfArtifactJson.Serialize(
            PerfFixtureManifest.Create(accumulator.Definition) with
            {
                SchemaVersion = PerfFinalGateArtifactSchema.Version,
            }
        );

        return await AssembleAndWriteAsync(
            accumulator.Provider,
            openReplayConnectionAsync,
            leasedConnectionString,
            settings,
            resultsDirectoryBase,
            PerfFinalGateRunKinds.Primary,
            new PerfFinalGateManifestFixture(
                accumulator.Definition.Kind.Id,
                accumulator.Definition.RowCount,
                accumulator.DeepOffset
            ),
            accumulator.PhaseLog,
            fixtureManifestJson,
            accumulator.Cells,
            accumulator.WarmupIterations,
            accumulator.MeasuredIterations,
            accumulator.RunnerCommit,
            accumulator.SubjectCommit,
            accumulator.WorktreeDirtyPaths
        );
    }

    public static async Task<string> RunDescriptorFixtureAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        PerfDescriptorFixtureDefinition definition,
        int warmupIterations,
        int measuredIterations,
        string resultsDirectoryBase,
        string runnerCommit,
        PerfEvidenceRunSettings settings
    )
    {
        PerfBaselineRunPipeline.GuardCiEnvironment(
            settings.AllowCi,
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS")
        );
        string subjectCommit = GitIdentity.HeadCommit(AppContext.BaseDirectory);
        IReadOnlyList<string> dirtyPaths = GitIdentity.DirtyPaths(AppContext.BaseDirectory);
        if (!settings.AllowAnyDirtyPath)
        {
            PerfBaselineRunPipeline.GuardDirtyPaths(dirtyPaths, settings.AllowedDirtyPrefixes);
        }

        await PerfDescriptorFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);

        string providerName = PerfProviders.ArtifactName(provider);
        List<PerfFinalGateCellArtifacts> cells = [];
        List<(PerfFinalGateCell Cell, PerfCursorMeasuredCell Measured)> cursorCells = [];
        List<(PerfFinalGateCell Cell, PerfPartitionMeasuredCell Measured)> partitionCells = [];

        foreach (PerfFinalGateCell cell in PerfFinalGateScenarios.DescriptorCellsInExecutionOrder)
        {
            if (cell.Family == PerfScenarioFamily.Cursor)
            {
                PerfCursorMeasuredCell measured = await PerfCursorScenarioExecutor.RunCellAsync(
                    harness,
                    provider,
                    PerfFinalGateCellBuilders.DescriptorCursorCell(
                        cell.CursorRange!.Value,
                        definition,
                        cell.PageSize!.Value
                    ),
                    warmupIterations,
                    measuredIterations
                );
                cursorCells.Add((cell, measured));
            }
            else
            {
                PerfPartitionMeasuredCell measured = await PerfPartitionScenarioExecutor.RunCellAsync(
                    harness,
                    provider,
                    new PerfPartitionCellRequest(
                        cell.ScenarioId,
                        PerfDescriptorFixtureDefinition.ResourceEndpoint,
                        cell.PartitionNumber!.Value
                    ),
                    warmupIterations,
                    measuredIterations
                );
                partitionCells.Add((cell, measured));
            }
        }

        await using (DbConnection replayConnection = await openReplayConnectionAsync())
        {
            foreach (PerfFinalGateCell cell in PerfFinalGateScenarios.DescriptorCellsInExecutionOrder)
            {
                if (cell.Family == PerfScenarioFamily.Cursor)
                {
                    PerfCursorMeasuredCell measured = cursorCells
                        .Single(candidate => candidate.Cell == cell)
                        .Measured;
                    cells.Add(
                        await ConvertCursorCellAsync(
                            replayConnection,
                            provider,
                            providerName,
                            cell,
                            measured,
                            warmupIterations,
                            measuredIterations,
                            runnerCommit,
                            subjectCommit
                        )
                    );
                }
                else
                {
                    PerfPartitionMeasuredCell measured = partitionCells
                        .Single(candidate => candidate.Cell == cell)
                        .Measured;
                    cells.Add(
                        await ConvertPartitionCellAsync(
                            replayConnection,
                            provider,
                            providerName,
                            cell,
                            measured,
                            warmupIterations,
                            measuredIterations,
                            runnerCommit,
                            subjectCommit
                        )
                    );
                }
            }
        }

        return await AssembleAndWriteAsync(
            provider,
            openReplayConnectionAsync,
            leasedConnectionString,
            settings,
            resultsDirectoryBase,
            PerfFinalGateRunKinds.Descriptors,
            new PerfFinalGateManifestFixture(definition.Kind.Id, definition.RowCount, DeepOffset: null),
            PhaseLog: [],
            PerfArtifactJson.Serialize(PerfDescriptorFixtureManifest.Create(definition)),
            cells,
            warmupIterations,
            measuredIterations,
            runnerCommit,
            subjectCommit,
            dirtyPaths
        );
    }

    /// <summary>
    /// Measures and converts every primary-catalog cell belonging to one phase, in catalog
    /// order: measurement first for every cell, then one replay pass on a dedicated
    /// out-of-band connection.
    /// </summary>
    private static async Task MeasurePhaseCellsAsync(
        PerfFinalGateRunAccumulator accumulator,
        ApiIntegrationHarness harness,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        PerfPrimaryPhase phase
    )
    {
        PerfProvider provider = accumulator.Provider;
        string providerName = PerfProviders.ArtifactName(provider);
        IReadOnlyList<PerfFinalGateCell> phaseCells =
        [
            .. PerfFinalGateScenarios.PrimaryCellsInExecutionOrder.Where(cell =>
                PerfFinalGateScenarios.PhaseOf(cell.Variant) == phase
            ),
        ];
        string? filterQueryString = phaseCells.Any(cell => cell.Variant == PerfFinalGateVariant.Filtered)
            ? PerfFinalGateCellBuilders.FilteredQueryString
            : null;

        Dictionary<(string ScenarioId, int PageSize), PerfMeasuredCell> traditional = [];
        if (phaseCells.Any(cell => cell.Family == PerfScenarioFamily.Traditional))
        {
            IReadOnlyList<PerfMeasuredCell> measuredTraditional = await PerfScenarioExecutor.RunAsync(
                harness,
                provider,
                accumulator.DeepOffset,
                accumulator.WarmupIterations,
                accumulator.MeasuredIterations
            );
            traditional = measuredTraditional.ToDictionary(cell => (cell.ScenarioId, cell.PageSize));
        }

        List<(PerfFinalGateCell Cell, PerfCursorMeasuredCell Measured)> cursorCells = [];
        List<(PerfFinalGateCell Cell, PerfPartitionMeasuredCell Measured)> partitionCells = [];
        foreach (PerfFinalGateCell cell in phaseCells)
        {
            switch (cell.Family)
            {
                case PerfScenarioFamily.Cursor:
                    cursorCells.Add(
                        (
                            cell,
                            await PerfCursorScenarioExecutor.RunCellAsync(
                                harness,
                                provider,
                                PerfFinalGateCellBuilders.StudentCursorCell(
                                    cell.Variant,
                                    cell.CursorRange!.Value,
                                    accumulator.Definition,
                                    cell.PageSize!.Value,
                                    filterQueryString
                                ),
                                accumulator.WarmupIterations,
                                accumulator.MeasuredIterations
                            )
                        )
                    );
                    break;
                case PerfScenarioFamily.Partition:
                    partitionCells.Add(
                        (
                            cell,
                            await PerfPartitionScenarioExecutor.RunCellAsync(
                                harness,
                                provider,
                                new PerfPartitionCellRequest(
                                    cell.ScenarioId,
                                    PerfFixtureDefinition.ResourceEndpoint,
                                    cell.PartitionNumber!.Value,
                                    filterQueryString
                                ),
                                accumulator.WarmupIterations,
                                accumulator.MeasuredIterations
                            )
                        )
                    );
                    break;
                case PerfScenarioFamily.Traditional:
                    // Measured above through the shared six-cell executor.
                    break;
                default:
                    throw new PerfObservationException($"Unknown scenario family '{cell.Family}'.");
            }
        }

        await using DbConnection replayConnection = await openReplayConnectionAsync();
        foreach (PerfFinalGateCell cell in phaseCells)
        {
            PerfFinalGateCellArtifacts artifacts = cell.Family switch
            {
                PerfScenarioFamily.Traditional => await ConvertTraditionalCellAsync(
                    replayConnection,
                    provider,
                    providerName,
                    cell,
                    traditional[(cell.ScenarioId, cell.PageSize!.Value)],
                    accumulator
                ),
                PerfScenarioFamily.Cursor => await ConvertCursorCellAsync(
                    replayConnection,
                    provider,
                    providerName,
                    cell,
                    cursorCells.Single(candidate => candidate.Cell == cell).Measured,
                    accumulator.WarmupIterations,
                    accumulator.MeasuredIterations,
                    accumulator.RunnerCommit,
                    accumulator.SubjectCommit
                ),
                PerfScenarioFamily.Partition => await ConvertPartitionCellAsync(
                    replayConnection,
                    provider,
                    providerName,
                    cell,
                    partitionCells.Single(candidate => candidate.Cell == cell).Measured,
                    accumulator.WarmupIterations,
                    accumulator.MeasuredIterations,
                    accumulator.RunnerCommit,
                    accumulator.SubjectCommit
                ),
                _ => throw new PerfObservationException($"Unknown scenario family '{cell.Family}'."),
            };
            accumulator.AddCell(artifacts);
        }
    }

    private static async Task<PerfFinalGateCellArtifacts> ConvertTraditionalCellAsync(
        DbConnection replayConnection,
        PerfProvider provider,
        string providerName,
        PerfFinalGateCell cell,
        PerfMeasuredCell measured,
        PerfFinalGateRunAccumulator accumulator
    )
    {
        string cellKey = measured.PageSize.ToString(CultureInfo.InvariantCulture);
        (PerfDatabaseMetrics metrics, string planFile, List<PerfArtifactFile> files) =
            await CaptureReplayEvidenceAsync(
                replayConnection,
                provider,
                $"plans/{providerName}.{cell.ScenarioId}.{cellKey}",
                measured.HydrationBatchSql,
                measured.PageSelection.ParameterValues
            );
        files.AddRange(
            SqlFiles(
                providerName,
                cell.ScenarioId,
                cellKey,
                measured.PageSelection.PageDocumentIdSql,
                measured.HydrationBatchSql,
                PerfFinalGateReplaySources.HydrationKeyset,
                measured.PageSelection.ParameterValues
            )
        );

        PerfFinalGateScenarioResult row = new(
            providerName,
            cell.ScenarioId,
            PerfFinalGateScenarios.FamilyName(cell.Family),
            PerfFinalGateScenarios.VariantName(cell.Variant),
            PhaseNameOf(cell.Variant),
            measured.PageSize,
            measured.Offset,
            CursorRange: null,
            StartAnchorDocumentId: null,
            RequestedPartitionNumber: null,
            measured.ReturnedRows,
            ReturnedTokenCount: null,
            measured.CommandCountPerRequest,
            accumulator.WarmupIterations,
            accumulator.MeasuredIterations,
            measured.LatencyMs,
            measured.DriverExecuteMs,
            metrics,
            planFile,
            measured.PageSelection.Sha256,
            PerfFinalGateReplaySources.HydrationKeyset,
            accumulator.RunnerCommit,
            accumulator.SubjectCommit
        );
        return new PerfFinalGateCellArtifacts(row, files);
    }

    private static async Task<PerfFinalGateCellArtifacts> ConvertCursorCellAsync(
        DbConnection replayConnection,
        PerfProvider provider,
        string providerName,
        PerfFinalGateCell cell,
        PerfCursorMeasuredCell measured,
        int warmupIterations,
        int measuredIterations,
        string runnerCommit,
        string subjectCommit
    )
    {
        string cellKey = measured.PageSize.ToString(CultureInfo.InvariantCulture);
        bool relationalChannel = cell.Variant == PerfFinalGateVariant.Descriptor;
        string replaySource = relationalChannel
            ? PerfFinalGateReplaySources.RelationalCommand
            : PerfFinalGateReplaySources.HydrationKeyset;

        (PerfDatabaseMetrics metrics, string planFile, List<PerfArtifactFile> files) =
            await CaptureReplayEvidenceAsync(
                replayConnection,
                provider,
                $"plans/{providerName}.{cell.ScenarioId}.{cellKey}",
                measured.HydrationBatchSql,
                measured.PageSelection.ParameterValues
            );
        files.AddRange(
            SqlFiles(
                providerName,
                cell.ScenarioId,
                cellKey,
                measured.PageSelection.PageDocumentIdSql,
                relationalChannel ? null : measured.HydrationBatchSql,
                replaySource,
                measured.PageSelection.ParameterValues
            )
        );

        PerfFinalGateScenarioResult row = new(
            providerName,
            cell.ScenarioId,
            PerfFinalGateScenarios.FamilyName(cell.Family),
            PerfFinalGateScenarios.VariantName(cell.Variant),
            PhaseNameOf(cell.Variant),
            measured.PageSize,
            Offset: null,
            PerfFinalGateScenarios.RangeName(cell.CursorRange!.Value),
            measured.StartAnchorDocumentId,
            RequestedPartitionNumber: null,
            measured.ReturnedRows,
            ReturnedTokenCount: null,
            measured.CommandCountPerRequest,
            warmupIterations,
            measuredIterations,
            measured.LatencyMs,
            measured.DriverExecuteMs,
            metrics,
            planFile,
            measured.PageSelection.Sha256,
            replaySource,
            runnerCommit,
            subjectCommit
        );
        return new PerfFinalGateCellArtifacts(row, files);
    }

    private static async Task<PerfFinalGateCellArtifacts> ConvertPartitionCellAsync(
        DbConnection replayConnection,
        PerfProvider provider,
        string providerName,
        PerfFinalGateCell cell,
        PerfPartitionMeasuredCell measured,
        int warmupIterations,
        int measuredIterations,
        string runnerCommit,
        string subjectCommit
    )
    {
        string cellKey = measured.RequestedNumber.ToString(CultureInfo.InvariantCulture);
        (PerfDatabaseMetrics metrics, string planFile, List<PerfArtifactFile> files) =
            await CaptureReplayEvidenceAsync(
                replayConnection,
                provider,
                $"plans/{providerName}.{cell.ScenarioId}.{cellKey}",
                measured.BoundarySql,
                measured.BoundaryParameterValues
            );
        files.AddRange(
            SqlFiles(
                providerName,
                cell.ScenarioId,
                cellKey,
                measured.BoundarySql,
                BatchSql: null,
                PerfFinalGateReplaySources.RelationalCommand,
                measured.BoundaryParameterValues
            )
        );

        PerfFinalGateScenarioResult row = new(
            providerName,
            cell.ScenarioId,
            PerfFinalGateScenarios.FamilyName(cell.Family),
            PerfFinalGateScenarios.VariantName(cell.Variant),
            PhaseNameOf(cell.Variant),
            PageSize: null,
            Offset: null,
            CursorRange: null,
            StartAnchorDocumentId: null,
            measured.RequestedNumber,
            ReturnedRows: null,
            measured.ReturnedTokenCount,
            measured.CommandCountPerRequest,
            warmupIterations,
            measuredIterations,
            measured.LatencyMs,
            measured.DriverExecuteMs,
            metrics,
            planFile,
            measured.BoundarySqlSha256,
            PerfFinalGateReplaySources.RelationalCommand,
            runnerCommit,
            subjectCommit
        );
        return new PerfFinalGateCellArtifacts(row, files);
    }

    private static async Task<(
        PerfDatabaseMetrics Metrics,
        string PlanFile,
        List<PerfArtifactFile> Files
    )> CaptureReplayEvidenceAsync(
        DbConnection replayConnection,
        PerfProvider provider,
        string baseName,
        string sql,
        IReadOnlyDictionary<string, object?> parameterValues
    )
    {
        if (provider == PerfProvider.Postgresql)
        {
            PgsqlPlanCaptureResult capture = await PgsqlPlanCapture.CaptureAsync(
                replayConnection,
                sql,
                parameterValues
            );
            string planFile = $"{baseName}.explain.json";
            return (capture.Metrics, planFile, [new PerfArtifactFile(planFile, capture.PlanArtifactJson)]);
        }

        MssqlPlanCaptureResult mssqlCapture = await MssqlPlanCapture.CaptureAsync(
            replayConnection,
            sql,
            parameterValues
        );
        List<string> sqlPlanFiles =
        [
            .. mssqlCapture.ShowplanXmlDocuments.Select(
                (_, index) => $"{baseName}.plan{index + 1:D2}.sqlplan"
            ),
        ];
        string statisticsFile = $"{baseName}.stats.txt";
        string planIndexFile = $"{baseName}.plans.json";
        List<PerfArtifactFile> files =
        [
            new PerfArtifactFile(planIndexFile, MssqlPlanCapture.PlanIndexJson(sqlPlanFiles, statisticsFile)),
            .. sqlPlanFiles.Select(
                (path, index) => new PerfArtifactFile(path, mssqlCapture.ShowplanXmlDocuments[index])
            ),
            new PerfArtifactFile(statisticsFile, mssqlCapture.StatisticsText),
        ];
        return (mssqlCapture.Metrics, planIndexFile, files);
    }

    /// <summary>
    /// Per-cell sql/ evidence: the selection text, the hydration batch where it is a separate
    /// text, and the replay parameters with their capture source — the values the plan replay
    /// bound, whichever channel captured them.
    /// </summary>
    private static IEnumerable<PerfArtifactFile> SqlFiles(
        string providerName,
        string scenarioId,
        string cellKey,
        string selectionSql,
        string? BatchSql,
        string source,
        IReadOnlyDictionary<string, object?> parameterValues
    )
    {
        string baseName = $"sql/{providerName}.{scenarioId}.{cellKey}";
        yield return new PerfArtifactFile($"{baseName}.selection.sql", selectionSql);
        if (BatchSql is not null && BatchSql != selectionSql)
        {
            yield return new PerfArtifactFile($"{baseName}.batch.sql", BatchSql);
        }

        JsonObject parameters = new();
        foreach (
            (string name, object? value) in parameterValues.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        )
        {
            parameters[name] = value is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(value);
        }

        JsonObject payload = new() { ["source"] = source, ["parameters"] = parameters };
        yield return new PerfArtifactFile(
            $"{baseName}.parameters.json",
            payload.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }
            )
        );
    }

    private static string? PhaseNameOf(PerfFinalGateVariant variant)
    {
        PerfPrimaryPhase? phase = PerfFinalGateScenarios.PhaseOf(variant);
        return phase is null ? null : PerfFinalGateScenarios.PhaseName(phase.Value);
    }

    private static async Task<string> AssembleAndWriteAsync(
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        PerfEvidenceRunSettings settings,
        string resultsDirectoryBase,
        string runKind,
        PerfFinalGateManifestFixture fixture,
        IReadOnlyList<PerfFinalGatePhaseLogEntry> PhaseLog,
        string fixtureManifestJson,
        IReadOnlyList<PerfFinalGateCellArtifacts> cells,
        int warmupIterations,
        int measuredIterations,
        string runnerCommit,
        string subjectCommit,
        IReadOnlyList<string> worktreeDirtyPaths
    )
    {
        string providerName = PerfProviders.ArtifactName(provider);

        PerfEnvironmentIdentity environment;
        await using (DbConnection environmentConnection = await openReplayConnectionAsync())
        {
            environment = await PerfEnvironmentCapture.CaptureAsync(
                environmentConnection,
                provider,
                settings.ImageTag,
                settings.ImageDigest,
                settings.StorageNote,
                leasedConnectionString
            );
        }

        DateTime capturedAt = DateTime.UtcNow;
        string runId =
            $"{providerName}-{runKind}-{fixture.FixtureId}-"
            + capturedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        List<PerfFinalGateScenarioResult> rows = [.. cells.Select(cell => cell.Row)];
        PerfFinalGateRunManifest manifest = PerfFinalGateRunManifest.Create(
            runKind,
            new PerfRunIdentity(
                runId,
                capturedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                providerName
            ),
            new PerfCommitIdentity(runnerCommit, subjectCommit, worktreeDirtyPaths),
            fixture,
            PhaseLog,
            new PerfFinalGateIterationPlan(
                warmupIterations,
                measuredIterations,
                [
                    .. rows.Select(row => new PerfFinalGateExecutedCell(
                        row.ScenarioId,
                        row.Family,
                        row.Variant,
                        row.Phase,
                        row.PageSize,
                        row.Offset,
                        row.CursorRange,
                        row.StartAnchorDocumentId,
                        row.RequestedPartitionNumber
                    )),
                ]
            ),
            environment
        );

        string runDirectory = Path.Combine(resultsDirectoryBase, runId);
        PerfFinalGateArtifactWriter.Write(
            runDirectory,
            manifest,
            PerfFinalGateResultsDocument.Create(rows),
            fixtureManifestJson,
            [.. cells.SelectMany(cell => cell.Files)]
        );

        // Reload what was written and validate again: the committed evidence is the files,
        // not the in-memory objects.
        PerfFinalGateRunManifest reloadedManifest = PerfArtifactJson.Deserialize<PerfFinalGateRunManifest>(
            await File.ReadAllTextAsync(Path.Combine(runDirectory, "run-manifest.json"))
        );
        PerfFinalGateResultsDocument reloadedResults =
            PerfArtifactJson.Deserialize<PerfFinalGateResultsDocument>(
                await File.ReadAllTextAsync(Path.Combine(runDirectory, "results.json"))
            );
        PerfFinalGateArtifactValidator.EnsureValid(reloadedManifest, reloadedResults);

        return runDirectory;
    }
}
