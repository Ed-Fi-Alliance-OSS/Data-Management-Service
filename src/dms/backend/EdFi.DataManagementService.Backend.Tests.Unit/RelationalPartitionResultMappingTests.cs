// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The query-to-partition restatement of shared read-path outcomes.
/// </summary>
/// <remarks>
/// Exercised directly rather than only through the repository because this is the single seam where a
/// query outcome becomes a partition one, so a fact dropped here is dropped for every partition request
/// that reaches the shared capability, authorization, or budget path.
/// </remarks>
[TestFixture]
public class Given_RelationalPartitionResultMapping
{
    // Whether a selection command was issued is a fact about the request, not about the result shape, so
    // it has to survive the restatement in both directions. If only the true case were covered, a
    // mapping that hardcoded true would pass.
    [TestCase(true)]
    [TestCase(false)]
    public void It_carries_the_selection_skipped_fact_across_the_restatement(bool selectionSkipped)
    {
        var queryResult = new QueryResult.QuerySuccess([], null) { SelectionSkipped = selectionSkipped };

        var result = RelationalPartitionResultMapping.FromQueryResult(queryResult);

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success.Ranges.Should().BeEmpty();
        success.SelectionSkipped.Should().Be(selectionSkipped);
    }

    // A shared empty success with a compiled total count still restates as an empty boundary set: the
    // count describes the page contract the partition operation does not have.
    [Test]
    public void It_restates_a_total_count_bearing_empty_success_as_an_empty_executed_boundary_set()
    {
        var result = RelationalPartitionResultMapping.FromQueryResult(new QueryResult.QuerySuccess([], 0));

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success.Ranges.Should().BeEmpty();
        success.SelectionSkipped.Should().BeFalse();
    }
}
