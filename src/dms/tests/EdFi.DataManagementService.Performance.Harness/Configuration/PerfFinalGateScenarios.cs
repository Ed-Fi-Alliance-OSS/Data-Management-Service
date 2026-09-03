// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// The fixed final-gate scenario catalog: the traditional rerun (the unchanged DMS-1391 six
/// cells), cursor range walks per variant, and partition boundary cells. The matrix is
/// deliberately closed. Primary cells are ordered by phase because the authorization seeding
/// and the filtered overlay mutate the shared load irreversibly; descriptor cells run in
/// their own database and carry no phase.
/// </summary>
public static class PerfFinalGateScenarios
{
    /// <summary>
    /// Requested partition counts on the unfiltered primary variant. The epic's
    /// number=200-vs-number=1 latency gate is defined over these cells only.
    /// </summary>
    public static readonly IReadOnlyList<int> UnfilteredPartitionNumbers = [1, 10, 200];

    /// <summary>
    /// The single requested partition count measured on the authorized, filtered, and
    /// descriptor variants. These cells produce recorded evidence and structural-gate
    /// checks, never a 200-vs-1 latency gate.
    /// </summary>
    public const int ScopedPartitionNumber = 10;

    public static readonly IReadOnlyList<PerfCursorRange> CursorRanges =
    [
        PerfCursorRange.First,
        PerfCursorRange.Middle,
        PerfCursorRange.Last,
    ];

    /// <summary>
    /// All cells measured against the shared primary load, in mandatory execution order:
    /// every pristine cell before the authorization seeding, every authorized cell before
    /// the filtered overlay.
    /// </summary>
    public static readonly IReadOnlyList<PerfFinalGateCell> PrimaryCellsInExecutionOrder =
    [
        .. PerfScenarios.AllIds.SelectMany(scenarioId =>
            PerfScenarios.PageSizes.Select(pageSize => new PerfFinalGateCell(
                scenarioId,
                PerfScenarioFamily.Traditional,
                PerfFinalGateVariant.Unfiltered,
                pageSize,
                CursorRange: null,
                PartitionNumber: null
            ))
        ),
        .. CursorCells(PerfFinalGateVariant.Unfiltered),
        .. UnfilteredPartitionNumbers.Select(number =>
            PartitionCell(PerfFinalGateVariant.Unfiltered, number)
        ),
        .. CursorCells(PerfFinalGateVariant.Authorized),
        PartitionCell(PerfFinalGateVariant.Authorized, ScopedPartitionNumber),
        .. CursorCells(PerfFinalGateVariant.Filtered),
        PartitionCell(PerfFinalGateVariant.Filtered, ScopedPartitionNumber),
    ];

    /// <summary>
    /// All cells measured against the separate descriptor fixture, in execution order.
    /// </summary>
    public static readonly IReadOnlyList<PerfFinalGateCell> DescriptorCellsInExecutionOrder =
    [
        .. CursorCells(PerfFinalGateVariant.Descriptor),
        PartitionCell(PerfFinalGateVariant.Descriptor, ScopedPartitionNumber),
    ];

    /// <summary>
    /// The primary-load phase a variant's cells measure under, or null for the descriptor
    /// variant, which runs in its own database.
    /// </summary>
    public static PerfPrimaryPhase? PhaseOf(PerfFinalGateVariant variant) =>
        variant switch
        {
            PerfFinalGateVariant.Unfiltered => PerfPrimaryPhase.Pristine,
            PerfFinalGateVariant.Authorized => PerfPrimaryPhase.AuthorizedSeeded,
            PerfFinalGateVariant.Filtered => PerfPrimaryPhase.FilteredOverlay,
            PerfFinalGateVariant.Descriptor => null,
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };

    public static string CursorScenarioId(PerfFinalGateVariant variant, PerfCursorRange range) =>
        $"cursor-{VariantName(variant)}-{RangeName(range)}";

    public static string PartitionScenarioId(PerfFinalGateVariant variant, int partitionNumber) =>
        $"partition-{VariantName(variant)}-{partitionNumber}";

    /// <summary>
    /// The canonical lowercase family name used in result artifacts.
    /// </summary>
    public static string FamilyName(PerfScenarioFamily family) =>
        family switch
        {
            PerfScenarioFamily.Traditional => "traditional",
            PerfScenarioFamily.Cursor => "cursor",
            PerfScenarioFamily.Partition => "partition",
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
        };

    /// <summary>
    /// The canonical lowercase phase name used in result artifacts.
    /// </summary>
    public static string PhaseName(PerfPrimaryPhase phase) =>
        phase switch
        {
            PerfPrimaryPhase.Pristine => "pristine",
            PerfPrimaryPhase.AuthorizedSeeded => "authorized-seeded",
            PerfPrimaryPhase.FilteredOverlay => "filtered-overlay",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
        };

    /// <summary>
    /// The canonical lowercase variant name used in scenario ids and result artifacts.
    /// </summary>
    public static string VariantName(PerfFinalGateVariant variant) =>
        variant switch
        {
            PerfFinalGateVariant.Unfiltered => "unfiltered",
            PerfFinalGateVariant.Authorized => "authorized",
            PerfFinalGateVariant.Filtered => "filtered",
            PerfFinalGateVariant.Descriptor => "descriptor",
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };

    /// <summary>
    /// The canonical lowercase range name used in scenario ids and result artifacts.
    /// </summary>
    public static string RangeName(PerfCursorRange range) =>
        range switch
        {
            PerfCursorRange.First => "first",
            PerfCursorRange.Middle => "middle",
            PerfCursorRange.Last => "last",
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
        };

    private static IEnumerable<PerfFinalGateCell> CursorCells(PerfFinalGateVariant variant) =>
        CursorRanges.SelectMany(range =>
            PerfScenarios.PageSizes.Select(pageSize => new PerfFinalGateCell(
                CursorScenarioId(variant, range),
                PerfScenarioFamily.Cursor,
                variant,
                pageSize,
                range,
                PartitionNumber: null
            ))
        );

    private static PerfFinalGateCell PartitionCell(PerfFinalGateVariant variant, int partitionNumber) =>
        new(
            PartitionScenarioId(variant, partitionNumber),
            PerfScenarioFamily.Partition,
            variant,
            PageSize: null,
            CursorRange: null,
            partitionNumber
        );
}
