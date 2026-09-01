// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// Which fixture rows belong to each variant's candidate set, and where a cursor range
/// enters it. Row ordinals and candidate indexes are both 1-based. The selections are the
/// contract the loaders implement: the authorization seeding grants every second row, the
/// filtered overlay varies every tenth row, and the descriptor load gives odd rows the
/// accessible namespace — interleaved rather than contiguous, so an accessible page always
/// spans excluded rows and a candidate relation that lost its predicate cannot return a
/// coincidentally plausible range.
/// </summary>
public static class PerfVariantCandidates
{
    /// <summary>
    /// Every this-many-th row is a filtered-variant candidate, giving the epic's ~10%
    /// selectivity exactly.
    /// </summary>
    public const int FilteredRowStride = 10;

    private const int AuthorizedRowStride = 2;

    public static bool IsCandidateRowOrdinal(PerfFinalGateVariant variant, long rowOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rowOrdinal, 1);
        return variant switch
        {
            PerfFinalGateVariant.Unfiltered => true,
            PerfFinalGateVariant.Authorized => rowOrdinal % AuthorizedRowStride == 0,
            PerfFinalGateVariant.Filtered => rowOrdinal % FilteredRowStride == 0,
            PerfFinalGateVariant.Descriptor => rowOrdinal % 2 == 1,
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
    }

    public static long CandidateCount(PerfFinalGateVariant variant, long rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rowCount, 1);
        return variant switch
        {
            PerfFinalGateVariant.Unfiltered => rowCount,
            PerfFinalGateVariant.Authorized => rowCount / AuthorizedRowStride,
            PerfFinalGateVariant.Filtered => rowCount / FilteredRowStride,
            PerfFinalGateVariant.Descriptor => (rowCount + 1) / 2,
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
    }

    /// <summary>
    /// The row ordinal carrying the variant's candidateIndex-th candidate.
    /// </summary>
    public static long RowOrdinalOfCandidate(PerfFinalGateVariant variant, long candidateIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(candidateIndex, 1);
        return variant switch
        {
            PerfFinalGateVariant.Unfiltered => candidateIndex,
            PerfFinalGateVariant.Authorized => AuthorizedRowStride * candidateIndex,
            PerfFinalGateVariant.Filtered => FilteredRowStride * candidateIndex,
            PerfFinalGateVariant.Descriptor => (2 * candidateIndex) - 1,
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
    }

    /// <summary>
    /// The candidate index a cursor range's page begins at: the first candidate, a page
    /// centered on the middle of the set, or the page ending exactly at the final candidate.
    /// </summary>
    public static long StartCandidateIndex(PerfCursorRange range, long candidateCount, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(candidateCount, pageSize);
        return range switch
        {
            PerfCursorRange.First => 1,
            PerfCursorRange.Middle => ((candidateCount - pageSize) / 2) + 1,
            PerfCursorRange.Last => candidateCount - pageSize + 1,
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
        };
    }

    /// <summary>
    /// The row ordinal a cursor cell's page begins at, composed from the variant's candidate
    /// count over the fixture and the range's start candidate.
    /// </summary>
    public static long StartRowOrdinal(
        PerfFinalGateVariant variant,
        PerfCursorRange range,
        long rowCount,
        int pageSize
    ) =>
        RowOrdinalOfCandidate(
            variant,
            StartCandidateIndex(range, CandidateCount(variant, rowCount), pageSize)
        );
}
