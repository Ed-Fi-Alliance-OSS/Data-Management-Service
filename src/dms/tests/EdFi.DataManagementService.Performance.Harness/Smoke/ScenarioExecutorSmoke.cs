// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// End-to-end executor validation at smoke scale: load 10,000 rows, run the full six-cell
/// matrix with reduced iterations, and check the measured cells' guardrail properties —
/// including that all six cells compiled byte-identical page-selection SQL, which is the
/// textual-identity property the baseline exists to defend.
/// </summary>
internal static class ScenarioExecutorSmoke
{
    public static async Task RunAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Smoke10k);
        await PerfFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);

        long deepOffset = definition.RowCount * 9 / 10;
        IReadOnlyList<PerfMeasuredCell> cells = await PerfScenarioExecutor.RunAsync(
            harness,
            provider,
            deepOffset,
            warmupIterations: 2,
            measuredIterations: 3
        );

        cells.Should().HaveCount(6);
        cells.Select(cell => (cell.ScenarioId, cell.PageSize)).Should().OnlyHaveUniqueItems();

        foreach (PerfMeasuredCell cell in cells)
        {
            cell.CommandCountPerRequest.Should().Be(1);
            cell.ReturnedRows.Should().Be(cell.PageSize);
            cell.Offset.Should()
                .Be(PerfScenarioExecutor.OffsetFor(cell.ScenarioId, cell.PageSize, deepOffset));
            cell.LatencyMs.SamplesMs.Should().HaveCount(3);
            cell.DriverExecuteMs.SamplesMs.Should().HaveCount(3);
            cell.PageSelection.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        }

        cells
            .Select(cell => cell.PageSelection.Sha256)
            .Distinct()
            .Should()
            .ContainSingle(
                "traditional page-selection SQL text must be identical across offsets and page sizes"
            );
        cells[0].PageSelection.PageDocumentIdSql.Should().Contain("ORDER BY");
    }
}
