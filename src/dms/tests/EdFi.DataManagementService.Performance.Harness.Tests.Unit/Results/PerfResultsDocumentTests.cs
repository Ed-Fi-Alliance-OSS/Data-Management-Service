// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

[TestFixture]
public class Given_Results_Collected_Out_Of_Order
{
    private PerfResultsDocument _document = null!;

    [SetUp]
    public void Setup()
    {
        _document = PerfResultsDocument.Create([
            ResultSamples.Postgresql(PerfScenarios.TraditionalOffsetDeep, 500),
            ResultSamples.Mssql(PerfScenarios.TraditionalOffsetShallow, 25),
            ResultSamples.Postgresql(PerfScenarios.TraditionalOffsetZero, 500),
            ResultSamples.Postgresql(PerfScenarios.TraditionalOffsetZero, 25),
            ResultSamples.Mssql(PerfScenarios.TraditionalOffsetZero, 25),
        ]);
    }

    [Test]
    public void It_stamps_the_schema_version()
    {
        _document.SchemaVersion.Should().Be("1.3.0");
    }

    [Test]
    public void It_orders_by_provider_then_scenario_then_page_size()
    {
        _document
            .Results.Select(result => (result.Provider, result.ScenarioId, result.PageSize))
            .Should()
            .Equal(
                ("mssql", PerfScenarios.TraditionalOffsetZero, 25),
                ("mssql", PerfScenarios.TraditionalOffsetShallow, 25),
                ("postgresql", PerfScenarios.TraditionalOffsetZero, 25),
                ("postgresql", PerfScenarios.TraditionalOffsetZero, 500),
                ("postgresql", PerfScenarios.TraditionalOffsetDeep, 500)
            );
    }
}
