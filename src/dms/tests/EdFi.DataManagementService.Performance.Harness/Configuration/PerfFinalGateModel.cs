// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// The measurement families of the final-gate matrix: the traditional offset rerun compared
/// against the DMS-1391 baseline, cursor range walks, and partition boundary selection.
/// </summary>
public enum PerfScenarioFamily
{
    Traditional,
    Cursor,
    Partition,
}

/// <summary>
/// The candidate-set variants the final gate measures. Unfiltered, Authorized, and Filtered
/// all read the one shared primary student load; Descriptor reads the separate small
/// descriptor fixture in its own database.
/// </summary>
public enum PerfFinalGateVariant
{
    Unfiltered,
    Authorized,
    Filtered,
    Descriptor,
}

/// <summary>
/// Where a cursor cell enters the candidate set: the first candidate, a page centered on the
/// middle of the set, or the page that ends exactly at the final candidate.
/// </summary>
public enum PerfCursorRange
{
    First,
    Middle,
    Last,
}

/// <summary>
/// The measurement phases over the shared primary load, in mandatory execution order. The
/// pristine phase measures data byte-identical to the DMS-1391 baseline capture; the
/// authorization seeding and the filtered overlay each mutate the database irreversibly, so
/// no earlier phase's cell may run once a later phase has begun.
/// </summary>
public enum PerfPrimaryPhase
{
    Pristine,
    AuthorizedSeeded,
    FilteredOverlay,
}

/// <summary>
/// One cell of the final-gate matrix. Traditional and cursor cells carry a page size; cursor
/// cells additionally carry their range; partition cells carry the requested partition count
/// instead of either.
/// </summary>
public sealed record PerfFinalGateCell(
    string ScenarioId,
    PerfScenarioFamily Family,
    PerfFinalGateVariant Variant,
    int? PageSize,
    PerfCursorRange? CursorRange,
    int? PartitionNumber
);
