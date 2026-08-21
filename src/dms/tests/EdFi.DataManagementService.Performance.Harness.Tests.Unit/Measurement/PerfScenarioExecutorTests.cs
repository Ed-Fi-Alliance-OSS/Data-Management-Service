// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_The_Offset_Policy
{
    [Test]
    public void It_resolves_the_zero_offset()
    {
        PerfScenarioExecutor.OffsetFor(PerfScenarios.TraditionalOffsetZero, 500, 450_000).Should().Be(0);
    }

    [Test]
    public void It_resolves_the_shallow_offset_to_the_page_size()
    {
        PerfScenarioExecutor.OffsetFor(PerfScenarios.TraditionalOffsetShallow, 25, 450_000).Should().Be(25);
        PerfScenarioExecutor.OffsetFor(PerfScenarios.TraditionalOffsetShallow, 500, 450_000).Should().Be(500);
    }

    [Test]
    public void It_resolves_the_deep_offset_from_configuration()
    {
        PerfScenarioExecutor.OffsetFor(PerfScenarios.TraditionalOffsetDeep, 25, 450_000).Should().Be(450_000);
    }

    [Test]
    public void It_rejects_unknown_scenarios()
    {
        FluentActions
            .Invoking(() => PerfScenarioExecutor.OffsetFor("cursor-first-range", 25, 450_000))
            .Should()
            .Throw<ArgumentException>();
    }
}

[TestFixture]
public class Given_A_GetMany_Response_Body
{
    [Test]
    public void It_counts_the_returned_rows_from_the_observed_body()
    {
        PerfScenarioExecutor.CountReturnedRows("""[{"id":"a"},{"id":"b"},{"id":"c"}]""").Should().Be(3);
    }

    [Test]
    public void It_counts_an_empty_page_as_zero()
    {
        PerfScenarioExecutor.CountReturnedRows("[]").Should().Be(0);
    }

    [Test]
    public void It_rejects_a_non_array_body()
    {
        FluentActions
            .Invoking(() => PerfScenarioExecutor.CountReturnedRows("""{"error":"not a page"}"""))
            .Should()
            .Throw<PerfObservationException>();
    }
}

[TestFixture]
public class Given_The_Cell_Execution_Order
{
    private IReadOnlyList<PerfExecutedCell> _cells = null!;

    [SetUp]
    public void Setup()
    {
        _cells = PerfScenarioExecutor.CellsInExecutionOrder(450_000);
    }

    [Test]
    public void It_enumerates_the_full_matrix_in_canonical_order()
    {
        _cells
            .Select(cell => (cell.ScenarioId, cell.PageSize, cell.Offset))
            .Should()
            .Equal(
                (PerfScenarios.TraditionalOffsetZero, 25, 0),
                (PerfScenarios.TraditionalOffsetZero, 500, 0),
                (PerfScenarios.TraditionalOffsetShallow, 25, 25),
                (PerfScenarios.TraditionalOffsetShallow, 500, 500),
                (PerfScenarios.TraditionalOffsetDeep, 25, 450_000),
                (PerfScenarios.TraditionalOffsetDeep, 500, 450_000)
            );
    }
}
