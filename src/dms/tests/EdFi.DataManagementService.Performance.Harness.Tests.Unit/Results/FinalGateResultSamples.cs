// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

/// <summary>
/// Canned final-gate artifacts that satisfy the validator: catalog-ordered rows whose
/// summary statistics genuinely derive from their retained samples, over the smoke-scale
/// fixtures. Individual tests break specific rules from this valid base.
/// </summary>
internal static class FinalGateResultSamples
{
    public const long DeepOffset = 9_000;

    private static PerfLatencySummary SummaryOf(double baseMs) =>
        PerfLatencyMeasurement.Summarize([.. Enumerable.Range(0, 30).Select(i => baseMs + (i * 0.5))]);

    public static PerfFinalGateScenarioResult Row(PerfFinalGateCell cell, string provider = "postgresql")
    {
        string family = PerfFinalGateScenarios.FamilyName(cell.Family);
        string variant = PerfFinalGateScenarios.VariantName(cell.Variant);
        PerfPrimaryPhase? phase = PerfFinalGateScenarios.PhaseOf(cell.Variant);
        string? phaseName = phase is null ? null : PerfFinalGateScenarios.PhaseName(phase.Value);
        string cellKey = (cell.PageSize ?? cell.PartitionNumber)!.Value.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );

        long? offset =
            cell.Family == PerfScenarioFamily.Traditional
                ? cell.ScenarioId switch
                {
                    PerfScenarios.TraditionalOffsetZero => 0,
                    PerfScenarios.TraditionalOffsetShallow => cell.PageSize!.Value,
                    _ => DeepOffset,
                }
                : null;

        string replaySource =
            cell.Family == PerfScenarioFamily.Partition || cell.Variant == PerfFinalGateVariant.Descriptor
                ? PerfFinalGateReplaySources.RelationalCommand
                : PerfFinalGateReplaySources.HydrationKeyset;

        PerfDatabaseMetrics database =
            provider == "postgresql"
                ? new(
                    BuffersHit: 1200,
                    BuffersRead: 34,
                    DbExecutionMs: 6.25,
                    LogicalReads: null,
                    PhysicalReads: null,
                    DbCpuMs: null,
                    DbElapsedMs: null
                )
                : new(
                    BuffersHit: null,
                    BuffersRead: null,
                    DbExecutionMs: null,
                    LogicalReads: 2100,
                    PhysicalReads: 12,
                    DbCpuMs: 5.0,
                    DbElapsedMs: 7.75
                );

        string planSuffix = provider == "postgresql" ? "explain.json" : "plans.json";

        return new PerfFinalGateScenarioResult(
            provider,
            cell.ScenarioId,
            family,
            variant,
            phaseName,
            cell.PageSize,
            offset,
            cell.CursorRange is null ? null : PerfFinalGateScenarios.RangeName(cell.CursorRange.Value),
            cell.Family == PerfScenarioFamily.Cursor ? 100 : null,
            cell.PartitionNumber,
            cell.Family == PerfScenarioFamily.Partition ? null : cell.PageSize,
            cell.Family == PerfScenarioFamily.Partition ? Math.Min(cell.PartitionNumber!.Value, 4) : null,
            CommandCountPerRequest: 1,
            WarmupIterations: 5,
            MeasuredIterations: 30,
            SummaryOf(10.0),
            SummaryOf(7.5),
            database,
            $"plans/{provider}.{cell.ScenarioId}.{cellKey}.{planSuffix}",
            ResultSamples.Sha256,
            replaySource,
            ResultSamples.RunnerCommit,
            ResultSamples.SubjectCommit
        );
    }

    public static PerfFinalGateResultsDocument PrimaryDocument(string provider = "postgresql") =>
        PerfFinalGateResultsDocument.Create(
            PerfFinalGateScenarios.PrimaryCellsInExecutionOrder.Select(cell => Row(cell, provider))
        );

    public static PerfFinalGateResultsDocument DescriptorDocument(string provider = "postgresql") =>
        PerfFinalGateResultsDocument.Create(
            PerfFinalGateScenarios.DescriptorCellsInExecutionOrder.Select(cell => Row(cell, provider))
        );

    public static PerfFinalGateRunManifest PrimaryManifest(string provider = "postgresql") =>
        PerfFinalGateRunManifest.Create(
            PerfFinalGateRunKinds.Primary,
            new PerfRunIdentity(
                $"{provider}-final-primary-smoke-10k-20260901",
                "2026-09-01T12:00:00Z",
                provider
            ),
            new PerfCommitIdentity(ResultSamples.RunnerCommit, ResultSamples.SubjectCommit, []),
            new PerfFinalGateManifestFixture("smoke-10k", 10_000, DeepOffset),
            [
                new PerfFinalGatePhaseLogEntry(
                    PerfFinalGateScenarios.PhaseName(PerfPrimaryPhase.AuthorizedSeeded),
                    "Seeded the authorization source tables.",
                    [new PerfSetting("enrolledStudentCount", "5000")]
                ),
                new PerfFinalGatePhaseLogEntry(
                    PerfFinalGateScenarios.PhaseName(PerfPrimaryPhase.FilteredOverlay),
                    "Applied the birth-date overlay.",
                    [new PerfSetting("overlaidStudentCount", "1000")]
                ),
            ],
            new PerfFinalGateIterationPlan(5, 30, ExecutedCells(PerfFinalGateRunKinds.Primary, provider)),
            ResultSamples.Manifest(provider).Environment
        );

    public static PerfFinalGateRunManifest DescriptorManifest(string provider = "postgresql") =>
        PerfFinalGateRunManifest.Create(
            PerfFinalGateRunKinds.Descriptors,
            new PerfRunIdentity(
                $"{provider}-final-descriptors-smoke-2k-20260901",
                "2026-09-01T12:00:00Z",
                provider
            ),
            new PerfCommitIdentity(ResultSamples.RunnerCommit, ResultSamples.SubjectCommit, []),
            new PerfFinalGateManifestFixture("descriptors-smoke-2k", 2_000, DeepOffset: null),
            [],
            new PerfFinalGateIterationPlan(5, 30, ExecutedCells(PerfFinalGateRunKinds.Descriptors, provider)),
            ResultSamples.Manifest(provider).Environment
        );

    private static IReadOnlyList<PerfFinalGateExecutedCell> ExecutedCells(string runKind, string provider)
    {
        IReadOnlyList<PerfFinalGateCell> catalog =
            runKind == PerfFinalGateRunKinds.Primary
                ? PerfFinalGateScenarios.PrimaryCellsInExecutionOrder
                : PerfFinalGateScenarios.DescriptorCellsInExecutionOrder;
        return
        [
            .. catalog
                .Select(cell => Row(cell, provider))
                .Select(row => new PerfFinalGateExecutedCell(
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
        ];
    }
}
