// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

[TestFixture]
public class Given_The_Scenario_Matrix
{
    [Test]
    public void It_contains_exactly_the_three_traditional_offset_scenarios()
    {
        PerfScenarios
            .AllIds.Should()
            .Equal("traditional-offset-zero", "traditional-offset-shallow", "traditional-offset-deep");
    }

    [Test]
    public void It_measures_exactly_the_two_epic_page_sizes()
    {
        PerfScenarios.PageSizes.Should().Equal(25, 500);
    }

    [Test]
    public void It_keeps_the_maximum_page_size_consistent_with_the_matrix()
    {
        PerfScenarios.MaximumPageSize.Should().Be(PerfScenarios.PageSizes.Max());
    }

    [Test]
    public void It_recognizes_known_scenario_ids()
    {
        PerfScenarios.IsKnown(PerfScenarios.TraditionalOffsetDeep).Should().BeTrue();
    }

    [Test]
    public void It_rejects_unknown_scenario_ids()
    {
        PerfScenarios.IsKnown("cursor-first-range").Should().BeFalse();
    }
}
