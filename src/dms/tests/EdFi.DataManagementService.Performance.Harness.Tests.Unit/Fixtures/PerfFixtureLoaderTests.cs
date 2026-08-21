// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Loader_Chunking
{
    [Test]
    public void It_covers_the_row_range_without_overlap()
    {
        PerfFixtureLoader
            .Chunks(rowCount: 10_000, chunkSize: 3_000)
            .Should()
            .Equal((1, 3_000), (3_001, 6_000), (6_001, 9_000), (9_001, 10_000));
    }

    [Test]
    public void It_emits_one_chunk_when_the_size_covers_everything()
    {
        PerfFixtureLoader.Chunks(rowCount: 500, chunkSize: 50_000).Should().Equal((1, 500));
    }

    [Test]
    public void It_handles_an_exact_multiple()
    {
        PerfFixtureLoader
            .Chunks(rowCount: 6_000, chunkSize: 3_000)
            .Should()
            .Equal((1, 3_000), (3_001, 6_000));
    }
}
