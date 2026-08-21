// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_A_Plan_Index_For_A_Replayed_Batch
{
    private const string StatisticsFile = "plans/mssql.traditional-offset-zero.25.stats.txt";

    private static readonly IReadOnlyList<string> _planFiles =
    [
        "plans/mssql.traditional-offset-zero.25.plan01.sqlplan",
        "plans/mssql.traditional-offset-zero.25.plan02.sqlplan",
        "plans/mssql.traditional-offset-zero.25.plan03.sqlplan",
    ];

    private string _indexJson = null!;

    [SetUp]
    public void Setup()
    {
        _indexJson = MssqlPlanCapture.PlanIndexJson(_planFiles, StatisticsFile);
    }

    [Test]
    public void It_lists_every_plan_file_in_arrival_order()
    {
        JsonNode index = JsonNode.Parse(_indexJson)!;
        index["planFiles"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal(_planFiles);
    }

    [Test]
    public void It_points_at_the_raw_statistics_text()
    {
        JsonNode.Parse(_indexJson)!["statisticsFile"]!.GetValue<string>().Should().Be(StatisticsFile);
    }

    [Test]
    public void It_uses_lf_only_newlines()
    {
        _indexJson.Should().NotContain("\r");
    }
}

[TestFixture]
public class Given_A_Plan_Index_With_No_Plans
{
    [Test]
    public void It_rejects_an_empty_plan_file_list()
    {
        FluentActions
            .Invoking(() => MssqlPlanCapture.PlanIndexJson([], "plans/mssql.x.stats.txt"))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*at least one plan file*");
    }
}
