// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

[TestFixture]
public class Given_Variant_Candidate_Selection
{
    [Test]
    public void It_counts_every_row_as_an_unfiltered_candidate()
    {
        PerfVariantCandidates.CandidateCount(PerfFinalGateVariant.Unfiltered, 500_000).Should().Be(500_000);
    }

    [Test]
    public void It_counts_every_second_row_as_an_authorized_candidate()
    {
        PerfVariantCandidates.CandidateCount(PerfFinalGateVariant.Authorized, 500_000).Should().Be(250_000);
    }

    [Test]
    public void It_counts_every_tenth_row_as_a_filtered_candidate()
    {
        PerfVariantCandidates.CandidateCount(PerfFinalGateVariant.Filtered, 500_000).Should().Be(50_000);
    }

    [Test]
    public void It_counts_odd_rows_as_accessible_descriptor_candidates()
    {
        PerfVariantCandidates.CandidateCount(PerfFinalGateVariant.Descriptor, 25_000).Should().Be(12_500);
    }

    [Test]
    public void It_maps_candidate_indexes_onto_rows_the_membership_test_accepts()
    {
        foreach (PerfFinalGateVariant variant in Enum.GetValues<PerfFinalGateVariant>())
        {
            foreach (long candidateIndex in (long[])[1, 2, 3, 7, 50])
            {
                long rowOrdinal = PerfVariantCandidates.RowOrdinalOfCandidate(variant, candidateIndex);

                PerfVariantCandidates
                    .IsCandidateRowOrdinal(variant, rowOrdinal)
                    .Should()
                    .BeTrue($"{variant} candidate {candidateIndex} maps to row {rowOrdinal}");
            }
        }
    }

    [Test]
    public void It_agrees_with_a_brute_force_count_over_a_small_fixture()
    {
        const long RowCount = 100;

        foreach (PerfFinalGateVariant variant in Enum.GetValues<PerfFinalGateVariant>())
        {
            long bruteForce = Enumerable
                .Range(1, (int)RowCount)
                .LongCount(ordinal => PerfVariantCandidates.IsCandidateRowOrdinal(variant, ordinal));

            PerfVariantCandidates
                .CandidateCount(variant, RowCount)
                .Should()
                .Be(bruteForce, variant.ToString());
        }
    }

    [Test]
    public void It_maps_the_kth_candidate_to_the_kth_matching_row()
    {
        foreach (PerfFinalGateVariant variant in Enum.GetValues<PerfFinalGateVariant>())
        {
            IReadOnlyList<long> matchingRows =
            [
                .. Enumerable
                    .Range(1, 200)
                    .Select(ordinal => (long)ordinal)
                    .Where(ordinal => PerfVariantCandidates.IsCandidateRowOrdinal(variant, ordinal)),
            ];

            for (int index = 0; index < matchingRows.Count; index++)
            {
                PerfVariantCandidates
                    .RowOrdinalOfCandidate(variant, index + 1)
                    .Should()
                    .Be(matchingRows[index], $"{variant} candidate {index + 1}");
            }
        }
    }

    [Test]
    public void It_rejects_candidate_indexes_below_one()
    {
        Action act = () => PerfVariantCandidates.RowOrdinalOfCandidate(PerfFinalGateVariant.Unfiltered, 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

[TestFixture]
public class Given_Cursor_Range_Starts
{
    [Test]
    public void It_starts_the_first_range_at_the_first_candidate()
    {
        PerfVariantCandidates.StartCandidateIndex(PerfCursorRange.First, 500_000, 25).Should().Be(1);
    }

    [Test]
    public void It_centers_the_middle_range_page()
    {
        PerfVariantCandidates.StartCandidateIndex(PerfCursorRange.Middle, 500_000, 25).Should().Be(249_988);
    }

    [Test]
    public void It_ends_the_last_range_exactly_at_the_final_candidate()
    {
        foreach ((long candidateCount, int pageSize) in ((long, int)[])[(500_000, 25), (12_500, 500)])
        {
            long start = PerfVariantCandidates.StartCandidateIndex(
                PerfCursorRange.Last,
                candidateCount,
                pageSize
            );

            (start + pageSize - 1).Should().Be(candidateCount);
        }
    }

    [Test]
    public void It_computes_the_filtered_last_range_start_row_within_the_fixture()
    {
        long startRow = PerfVariantCandidates.StartRowOrdinal(
            PerfFinalGateVariant.Filtered,
            PerfCursorRange.Last,
            rowCount: 500_000,
            pageSize: 500
        );

        startRow.Should().Be(495_010);
    }

    [Test]
    public void It_computes_the_descriptor_last_range_start_row_within_the_fixture()
    {
        long startRow = PerfVariantCandidates.StartRowOrdinal(
            PerfFinalGateVariant.Descriptor,
            PerfCursorRange.Last,
            rowCount: 25_000,
            pageSize: 500
        );

        startRow.Should().Be(24_001);
    }

    [Test]
    public void It_refuses_a_page_larger_than_the_candidate_set()
    {
        Action act = () => PerfVariantCandidates.StartCandidateIndex(PerfCursorRange.Last, 400, 500);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_refuses_page_sizes_below_one()
    {
        Action act = () => PerfVariantCandidates.StartCandidateIndex(PerfCursorRange.First, 400, 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
