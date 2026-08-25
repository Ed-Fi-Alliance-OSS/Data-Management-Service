// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Starting identifiers become inclusive ranges. Every range but the last is bounded above, which is
/// what stops a later insert moving into a partition a client has already finished.
/// </summary>
[TestFixture]
public class PartitionRangeAssemblerTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_Ascending_Start_Ids : PartitionRangeAssemblerTests
    {
        [Test]
        public void It_closes_each_range_immediately_before_the_next_start()
        {
            PartitionRangeAssembler
                .ToInclusiveRanges([1L, 2501L, 5001L])
                .Should()
                .Equal(
                    new CursorRange(1, 2500),
                    new CursorRange(2501, 5000),
                    new CursorRange(5001, long.MaxValue)
                );
        }

        [Test]
        public void It_leaves_a_single_partition_unbounded_above()
        {
            PartitionRangeAssembler.ToInclusiveRanges([7L]).Should().Equal(new CursorRange(7, long.MaxValue));
        }

        [Test]
        public void It_produces_non_overlapping_contiguous_ranges()
        {
            IReadOnlyList<CursorRange> ranges = PartitionRangeAssembler.ToInclusiveRanges([
                10L,
                20L,
                30L,
                40L,
            ]);

            for (int index = 1; index < ranges.Count; index++)
            {
                ranges[index]
                    .InclusiveMinimum.Should()
                    .Be(
                        ranges[index - 1].InclusiveMaximum + 1,
                        "a gap would skip documents and an overlap would return them twice"
                    );
            }
        }

        [Test]
        public void It_covers_adjacent_starts_with_a_single_row_range()
        {
            PartitionRangeAssembler
                .ToInclusiveRanges([4L, 5L])
                .Should()
                .Equal(new CursorRange(4, 4), new CursorRange(5, long.MaxValue));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Start_Ids : PartitionRangeAssemblerTests
    {
        [Test]
        public void It_returns_no_ranges()
        {
            PartitionRangeAssembler.ToInclusiveRanges([]).Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extreme_Start_Ids : PartitionRangeAssemblerTests
    {
        [Test]
        public void It_handles_a_final_start_at_the_maximum_identity()
        {
            PartitionRangeAssembler
                .ToInclusiveRanges([long.MaxValue])
                .Should()
                .Equal(new CursorRange(long.MaxValue, long.MaxValue));
        }

        [Test]
        public void It_does_not_underflow_on_the_smallest_representable_start()
        {
            // Strictly ascending starts mean only the first can be long.MinValue, so subtracting one
            // from a following start can never underflow.
            PartitionRangeAssembler
                .ToInclusiveRanges([long.MinValue, long.MinValue + 1])
                .Should()
                .Equal(
                    new CursorRange(long.MinValue, long.MinValue),
                    new CursorRange(long.MinValue + 1, long.MaxValue)
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Starts_That_Are_Not_Strictly_Ascending : PartitionRangeAssemblerTests
    {
        [Test]
        public void It_rejects_a_descending_pair()
        {
            Action assemble = () => PartitionRangeAssembler.ToInclusiveRanges([10L, 5L]);

            assemble.Should().Throw<ArgumentException>().WithMessage("*ascending*");
        }

        [Test]
        public void It_rejects_a_duplicate_start()
        {
            // A duplicate would produce an inverted match-nothing range and hand the client a token that
            // silently returns nothing, so it is rejected rather than repaired: the statement orders the
            // rows, so a violation means the compiler changed.
            Action assemble = () => PartitionRangeAssembler.ToInclusiveRanges([10L, 10L]);

            assemble.Should().Throw<ArgumentException>().WithMessage("*ascending*");
        }

        [Test]
        public void It_rejects_a_repeated_content_version_start()
        {
            // Under a ContentVersion anchor the strictly-ascending guard also carries that anchor's
            // uniqueness assumption. Boundaries are cut at row numbers, so two candidates stamped at the
            // same change version would put 250 at two boundaries and leave an inverted range between
            // them. The change-version sequence assigns a distinct value per document write, so this
            // rejects an upstream invariant break rather than an unusual input.
            Action assemble = () => PartitionRangeAssembler.ToInclusiveRanges([100L, 250L, 250L]);

            assemble.Should().Throw<ArgumentException>().WithMessage("*ascending*");
        }
    }
}
