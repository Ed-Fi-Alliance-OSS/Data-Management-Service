// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Structural validation of a final-gate run's artifacts. Every rule guards against the
/// harness reporting work it did not actually do: the row sequence must be exactly the
/// catalog's cell order for the manifest's run kind, every row's family/variant/phase/range
/// facts must match the catalog cell they claim to be, per-family field shapes are enforced,
/// and the shared identity/latency/metric rules are the baseline validator's. Timing values
/// are never judged; only completeness and internal consistency are.
/// </summary>
public static class PerfFinalGateArtifactValidator
{
    public static void EnsureValid(PerfFinalGateRunManifest manifest, PerfFinalGateResultsDocument document)
    {
        IReadOnlyList<string> errors = Validate(manifest, document);
        if (errors.Count > 0)
        {
            throw new PerfArtifactValidationException(errors);
        }
    }

    public static IReadOnlyList<string> Validate(
        PerfFinalGateRunManifest manifest,
        PerfFinalGateResultsDocument document
    )
    {
        List<string> errors = [];

        if (manifest is null)
        {
            errors.Add("manifest: manifest is required.");
        }
        else
        {
            ValidateManifest(manifest, errors);
        }

        if (document is null)
        {
            errors.Add("results: results document is required.");
        }
        else
        {
            ValidateResults(document, manifest, errors);
        }

        return errors;
    }

    private static void ValidateManifest(PerfFinalGateRunManifest manifest, List<string> errors)
    {
        if (manifest.SchemaVersion != PerfFinalGateArtifactSchema.Version)
        {
            errors.Add(
                $"manifest: schema version '{manifest.SchemaVersion}' must be "
                    + $"'{PerfFinalGateArtifactSchema.Version}'."
            );
        }

        if (manifest.RunKind is not (PerfFinalGateRunKinds.Primary or PerfFinalGateRunKinds.Descriptors))
        {
            errors.Add($"manifest: run kind '{manifest.RunKind}' is unknown.");
        }

        PerfArtifactValidator.ValidateRunIdentity(manifest.Run, errors);
        PerfArtifactValidator.ValidateCommits(manifest.Commits, errors);
        ValidateFixture(manifest, errors);
        ValidatePhaseLog(manifest, errors);
        ValidateIterationPlan(manifest, errors);
        PerfArtifactValidator.ValidateEnvironment(manifest.Environment, errors);
    }

    private static void ValidateFixture(PerfFinalGateRunManifest manifest, List<string> errors)
    {
        PerfFinalGateManifestFixture? fixture = manifest.Fixture;
        if (fixture is null)
        {
            errors.Add("manifest: fixture is required.");
            return;
        }

        if (manifest.RunKind == PerfFinalGateRunKinds.Primary)
        {
            PerfFixtureKind? kind = PerfFixtureKind.FindById(fixture.FixtureId ?? string.Empty);
            if (kind is null)
            {
                errors.Add($"manifest: primary fixture id '{fixture.FixtureId}' is unknown.");
                return;
            }

            if (fixture.RowCount != kind.RowCount)
            {
                errors.Add(
                    $"manifest: fixture row count {fixture.RowCount} must be {kind.RowCount} for '{kind.Id}'."
                );
            }

            if (
                fixture.DeepOffset is not { } deepOffset
                || !PerfRunConfigurationLoader.IsWithinDeepOffsetBounds(kind, deepOffset)
            )
            {
                errors.Add(
                    "manifest: a primary run requires a deep offset between 0 and "
                        + $"{PerfRunConfigurationLoader.MaximumDeepOffset(kind)}."
                );
            }
        }
        else if (manifest.RunKind == PerfFinalGateRunKinds.Descriptors)
        {
            PerfDescriptorFixtureKind? kind = PerfDescriptorFixtureKind.FindById(
                fixture.FixtureId ?? string.Empty
            );
            if (kind is null)
            {
                errors.Add($"manifest: descriptor fixture id '{fixture.FixtureId}' is unknown.");
                return;
            }

            if (fixture.RowCount != kind.RowCount)
            {
                errors.Add(
                    $"manifest: fixture row count {fixture.RowCount} must be {kind.RowCount} for '{kind.Id}'."
                );
            }

            if (fixture.DeepOffset is not null)
            {
                errors.Add("manifest: a descriptor run carries no deep offset.");
            }
        }
    }

    /// <summary>
    /// The primary run must record its two mutations — the authorization seeding and the
    /// filtered overlay — in phase order; the descriptor run mutates nothing after its load.
    /// </summary>
    private static void ValidatePhaseLog(PerfFinalGateRunManifest manifest, List<string> errors)
    {
        IReadOnlyList<PerfFinalGatePhaseLogEntry>? phaseLog = manifest.PhaseLog;
        if (phaseLog is null)
        {
            errors.Add("manifest: phase log is required, even when empty.");
            return;
        }

        if (manifest.RunKind == PerfFinalGateRunKinds.Descriptors)
        {
            if (phaseLog.Count != 0)
            {
                errors.Add("manifest: a descriptor run's phase log must be empty.");
            }

            return;
        }

        if (manifest.RunKind != PerfFinalGateRunKinds.Primary)
        {
            return;
        }

        IReadOnlyList<string> expectedPhases =
        [
            PerfFinalGateScenarios.PhaseName(PerfPrimaryPhase.AuthorizedSeeded),
            PerfFinalGateScenarios.PhaseName(PerfPrimaryPhase.FilteredOverlay),
        ];
        if (!phaseLog.Select(entry => entry?.Phase).SequenceEqual(expectedPhases))
        {
            errors.Add(
                "manifest: a primary run's phase log must record the authorized seeding and the "
                    + "filtered overlay, in that order."
            );
            return;
        }

        foreach (PerfFinalGatePhaseLogEntry entry in phaseLog)
        {
            if (string.IsNullOrWhiteSpace(entry.Description))
            {
                errors.Add($"manifest: phase log entry '{entry.Phase}' requires a description.");
            }

            if (entry.Facts is null || entry.Facts.Count == 0)
            {
                errors.Add($"manifest: phase log entry '{entry.Phase}' requires at least one fact.");
            }
        }
    }

    private static void ValidateIterationPlan(PerfFinalGateRunManifest manifest, List<string> errors)
    {
        PerfFinalGateIterationPlan? iterations = manifest.Iterations;
        if (iterations is null)
        {
            errors.Add("manifest: iteration plan is required.");
            return;
        }

        if (iterations.WarmupIterations < PerfRunConfigurationLoader.MinimumWarmupIterations)
        {
            errors.Add(
                $"manifest: warmup iterations must be at least "
                    + $"{PerfRunConfigurationLoader.MinimumWarmupIterations}; got {iterations.WarmupIterations}."
            );
        }

        if (iterations.MeasuredIterations < PerfRunConfigurationLoader.MinimumMeasuredIterations)
        {
            errors.Add(
                $"manifest: measured iterations must be at least "
                    + $"{PerfRunConfigurationLoader.MinimumMeasuredIterations}; got {iterations.MeasuredIterations}."
            );
        }

        IReadOnlyList<PerfFinalGateCell> catalog = CatalogCells(manifest.RunKind);
        if (catalog.Count == 0)
        {
            return;
        }

        IReadOnlyList<PerfFinalGateExecutedCell>? cells = iterations.CellExecutionOrder;
        if (cells is null || cells.Any(cell => cell is null))
        {
            errors.Add("manifest: cell execution order is required with non-null entries.");
            return;
        }

        if (
            !cells
                .Select(cell => (cell.ScenarioId, cell.PageSize, cell.RequestedPartitionNumber))
                .SequenceEqual(catalog.Select(cell => (cell.ScenarioId, cell.PageSize, cell.PartitionNumber)))
        )
        {
            errors.Add(
                "manifest: cell execution order must be exactly the catalog's cell sequence for "
                    + $"run kind '{manifest.RunKind}'."
            );
        }
    }

    private static void ValidateResults(
        PerfFinalGateResultsDocument document,
        PerfFinalGateRunManifest? manifest,
        List<string> errors
    )
    {
        if (document.SchemaVersion != PerfFinalGateArtifactSchema.Version)
        {
            errors.Add(
                $"results: schema version '{document.SchemaVersion}' must be "
                    + $"'{PerfFinalGateArtifactSchema.Version}'."
            );
        }

        if (document.Results is null || document.Results.Count == 0)
        {
            errors.Add("results: at least one result row is required.");
            return;
        }

        IReadOnlyList<PerfFinalGateCell> catalog = manifest is null ? [] : CatalogCells(manifest.RunKind);
        if (catalog.Count > 0 && document.Results.Count != catalog.Count)
        {
            errors.Add(
                $"results: must contain exactly {catalog.Count} rows for run kind "
                    + $"'{manifest!.RunKind}'; got {document.Results.Count}."
            );
        }

        for (int index = 0; index < document.Results.Count; index++)
        {
            PerfFinalGateScenarioResult row = document.Results[index];
            if (row is null)
            {
                errors.Add($"results[{index}]: row is required.");
                continue;
            }

            PerfFinalGateCell? catalogCell = index < catalog.Count ? catalog[index] : null;
            ValidateRow(row, index, catalogCell, manifest, errors);
        }
    }

    private static void ValidateRow(
        PerfFinalGateScenarioResult row,
        int index,
        PerfFinalGateCell? catalogCell,
        PerfFinalGateRunManifest? manifest,
        List<string> errors
    )
    {
        string at = $"results[{index}]";

        if (!PerfArtifactValidator.IsCanonicalProvider(row.Provider))
        {
            errors.Add($"{at}: provider '{row.Provider}' must be the canonical 'postgresql' or 'mssql'.");
        }

        if (manifest?.Run is { } run && row.Provider != run.Provider)
        {
            errors.Add($"{at}: provider '{row.Provider}' must match the run provider '{run.Provider}'.");
        }

        if (catalogCell is not null)
        {
            ValidateAgainstCatalog(row, catalogCell, at, manifest, errors);
        }

        if (row.CommandCountPerRequest != 1)
        {
            errors.Add($"{at}: command count per request must be 1; got {row.CommandCountPerRequest}.");
        }

        if (manifest?.Iterations is { } iterations)
        {
            if (row.WarmupIterations != iterations.WarmupIterations)
            {
                errors.Add(
                    $"{at}: warmup iterations {row.WarmupIterations} must match the manifest's "
                        + $"{iterations.WarmupIterations}."
                );
            }

            if (row.MeasuredIterations != iterations.MeasuredIterations)
            {
                errors.Add(
                    $"{at}: measured iterations {row.MeasuredIterations} must match the manifest's "
                        + $"{iterations.MeasuredIterations}."
                );
            }
        }

        PerfArtifactValidator.ValidateLatency(at, "latency", row.LatencyMs, row.MeasuredIterations, errors);
        PerfArtifactValidator.ValidateLatency(
            at,
            "driver execute",
            row.DriverExecuteMs,
            row.MeasuredIterations,
            errors
        );
        PerfArtifactValidator.ValidateCommit(
            at,
            "runner commit",
            row.RunnerCommit,
            manifest?.Commits?.RunnerCommit,
            errors
        );
        PerfArtifactValidator.ValidateCommit(
            at,
            "subject commit",
            row.SubjectCommit,
            manifest?.Commits?.SubjectCommit,
            errors
        );
        PerfArtifactValidator.ValidateDatabaseMetricsSide(at, row.Provider, row.Database, errors);

        if (!PerfArtifactValidator.IsLowercaseHex(row.SelectionSqlSha256, 64))
        {
            errors.Add($"{at}: selection SQL hash must be 64 lowercase hex characters.");
        }

        string expectedPlanPrefix = $"plans/{row.Provider}.{row.ScenarioId}.{row.CellKey}.";
        if (string.IsNullOrWhiteSpace(row.PlanFile))
        {
            errors.Add($"{at}: plan file is required.");
        }
        else if (!row.PlanFile.StartsWith(expectedPlanPrefix, StringComparison.Ordinal))
        {
            errors.Add($"{at}: plan file '{row.PlanFile}' must start with '{expectedPlanPrefix}'.");
        }
    }

    private static void ValidateAgainstCatalog(
        PerfFinalGateScenarioResult row,
        PerfFinalGateCell catalogCell,
        string at,
        PerfFinalGateRunManifest? manifest,
        List<string> errors
    )
    {
        if (row.ScenarioId != catalogCell.ScenarioId)
        {
            errors.Add(
                $"{at}: scenario id '{row.ScenarioId}' must be '{catalogCell.ScenarioId}' at this position."
            );
            return;
        }

        if (row.Family != PerfFinalGateScenarios.FamilyName(catalogCell.Family))
        {
            errors.Add($"{at}: family '{row.Family}' does not match the catalog cell.");
        }

        if (row.Variant != PerfFinalGateScenarios.VariantName(catalogCell.Variant))
        {
            errors.Add($"{at}: variant '{row.Variant}' does not match the catalog cell.");
        }

        PerfPrimaryPhase? phase = PerfFinalGateScenarios.PhaseOf(catalogCell.Variant);
        string? expectedPhase = phase is null ? null : PerfFinalGateScenarios.PhaseName(phase.Value);
        if (row.Phase != expectedPhase)
        {
            errors.Add($"{at}: phase '{row.Phase}' must be '{expectedPhase ?? "null"}'.");
        }

        if (row.PageSize != catalogCell.PageSize)
        {
            errors.Add($"{at}: page size {row.PageSize} does not match the catalog cell.");
        }

        string? expectedRange = catalogCell.CursorRange is null
            ? null
            : PerfFinalGateScenarios.RangeName(catalogCell.CursorRange.Value);
        if (row.CursorRange != expectedRange)
        {
            errors.Add($"{at}: cursor range '{row.CursorRange}' must be '{expectedRange ?? "null"}'.");
        }

        if (row.RequestedPartitionNumber != catalogCell.PartitionNumber)
        {
            errors.Add(
                $"{at}: requested partition number {row.RequestedPartitionNumber} does not match the "
                    + "catalog cell."
            );
        }

        switch (catalogCell.Family)
        {
            case PerfScenarioFamily.Traditional:
                ValidateTraditionalRow(row, at, manifest, errors);
                break;
            case PerfScenarioFamily.Cursor:
                ValidateCursorRow(row, at, errors);
                break;
            case PerfScenarioFamily.Partition:
                ValidatePartitionRow(row, at, errors);
                break;
            default:
                errors.Add($"{at}: unknown family '{catalogCell.Family}'.");
                break;
        }

        string expectedReplaySource =
            catalogCell.Family == PerfScenarioFamily.Partition
            || catalogCell.Variant == PerfFinalGateVariant.Descriptor
                ? PerfFinalGateReplaySources.RelationalCommand
                : PerfFinalGateReplaySources.HydrationKeyset;
        if (row.ReplayParameterSource != expectedReplaySource)
        {
            errors.Add(
                $"{at}: replay parameter source '{row.ReplayParameterSource}' must be "
                    + $"'{expectedReplaySource}'."
            );
        }
    }

    private static void ValidateTraditionalRow(
        PerfFinalGateScenarioResult row,
        string at,
        PerfFinalGateRunManifest? manifest,
        List<string> errors
    )
    {
        if (row.PageSize is not { } pageSize)
        {
            errors.Add($"{at}: a traditional row requires a page size.");
            return;
        }

        if (manifest?.Fixture?.DeepOffset is { } deepOffset)
        {
            long? expectedOffset = row.ScenarioId switch
            {
                PerfScenarios.TraditionalOffsetZero => 0,
                PerfScenarios.TraditionalOffsetShallow => pageSize,
                PerfScenarios.TraditionalOffsetDeep => deepOffset,
                _ => null,
            };
            if (expectedOffset is null || row.Offset != expectedOffset)
            {
                errors.Add($"{at}: offset {row.Offset} must be {expectedOffset} for '{row.ScenarioId}'.");
            }
        }

        if (row.ReturnedRows != pageSize)
        {
            errors.Add($"{at}: returned rows {row.ReturnedRows} must equal page size {pageSize}.");
        }

        if (row.CursorRange is not null || row.StartAnchorDocumentId is not null)
        {
            errors.Add($"{at}: a traditional row carries no cursor fields.");
        }

        if (row.ReturnedTokenCount is not null)
        {
            errors.Add($"{at}: a traditional row carries no token count.");
        }
    }

    private static void ValidateCursorRow(PerfFinalGateScenarioResult row, string at, List<string> errors)
    {
        if (row.PageSize is not { } pageSize)
        {
            errors.Add($"{at}: a cursor row requires a page size.");
            return;
        }

        if (row.ReturnedRows != pageSize)
        {
            errors.Add($"{at}: returned rows {row.ReturnedRows} must equal page size {pageSize}.");
        }

        if (row.StartAnchorDocumentId is not > 0)
        {
            errors.Add($"{at}: a cursor row requires a positive start anchor.");
        }

        if (row.Offset is not null)
        {
            errors.Add($"{at}: a cursor row carries no offset.");
        }

        if (row.ReturnedTokenCount is not null)
        {
            errors.Add($"{at}: a cursor row carries no token count.");
        }
    }

    private static void ValidatePartitionRow(PerfFinalGateScenarioResult row, string at, List<string> errors)
    {
        if (row.RequestedPartitionNumber is not { } requestedNumber)
        {
            errors.Add($"{at}: a partition row requires a requested number.");
            return;
        }

        if (row.ReturnedTokenCount is not { } tokenCount || tokenCount < 1 || tokenCount > requestedNumber)
        {
            errors.Add(
                $"{at}: returned token count {row.ReturnedTokenCount} must be between 1 and "
                    + $"{requestedNumber}."
            );
        }

        if (
            row.PageSize is not null
            || row.Offset is not null
            || row.CursorRange is not null
            || row.StartAnchorDocumentId is not null
            || row.ReturnedRows is not null
        )
        {
            errors.Add($"{at}: a partition row carries no page-shaped fields.");
        }
    }

    private static IReadOnlyList<PerfFinalGateCell> CatalogCells(string runKind) =>
        runKind switch
        {
            PerfFinalGateRunKinds.Primary => PerfFinalGateScenarios.PrimaryCellsInExecutionOrder,
            PerfFinalGateRunKinds.Descriptors => PerfFinalGateScenarios.DescriptorCellsInExecutionOrder,
            _ => [],
        };
}
