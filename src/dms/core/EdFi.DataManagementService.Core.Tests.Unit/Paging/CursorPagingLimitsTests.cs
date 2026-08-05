// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

/// <summary>
/// A partition is never smaller than five maximum-sized pages, so a small collection is not sliced
/// into partitions that cost more to coordinate than to read.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Minimum_Partition_Size
{
    [Test]
    public void It_is_five_maximum_sized_pages()
    {
        CursorPagingLimits.MinimumPartitionSize(500).Should().Be(2500);
    }

    [Test]
    public void It_scales_with_the_smallest_usable_page_size()
    {
        CursorPagingLimits.MinimumPartitionSize(1).Should().Be(5);
    }

    [Test]
    public void It_widens_before_multiplying_so_a_large_page_size_cannot_wrap()
    {
        CursorPagingLimits.MinimumPartitionSize(int.MaxValue).Should().Be(10_737_418_235);
    }

    [Test]
    public void It_uses_the_documented_page_multiplier()
    {
        CursorPagingLimits.MinimumPartitionPageMultiplier.Should().Be(5);
    }
}
