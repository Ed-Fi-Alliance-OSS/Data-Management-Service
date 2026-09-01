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
/// Live executor proofs at smoke scale: cursor and partition cells run against real hosts
/// whose principal carries the variant's actual claim-set/education-organization/namespace
/// configuration, so page membership is decided by the production authorization and filter
/// predicates, not by fixture row shape. The executors' own guardrails carry the semantics —
/// exact page membership per iteration, cursor-shaped page selection, single-command
/// windows, decodable stable partition tokens — so a passing run here proves the executors
/// both measure and refuse correctly shaped work.
/// </summary>
internal static class PerfFinalGateExecutorSmokeScenario
{
    private const int PageSize = 25;
    private const int WarmupIterations = 2;
    private const int MeasuredIterations = 3;

    /// <summary>
    /// Pristine primary data under the bypassed-authorization principal: all three cursor
    /// ranges plus the unfiltered partition counts that fit a smoke run.
    /// </summary>
    public static async Task RunUnfilteredAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Smoke10k);
        await PerfFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);

        foreach (PerfCursorRange range in PerfFinalGateScenarios.CursorRanges)
        {
            PerfCursorMeasuredCell cell = await PerfCursorScenarioExecutor.RunCellAsync(
                harness,
                provider,
                StudentCursorCell(PerfFinalGateVariant.Unfiltered, range, definition, filter: null),
                WarmupIterations,
                MeasuredIterations
            );
            AssertCursorCell(cell, PerfFinalGateVariant.Unfiltered, range);
        }

        foreach (int number in (int[])[10, 200])
        {
            PerfPartitionMeasuredCell partition = await PerfPartitionScenarioExecutor.RunCellAsync(
                harness,
                provider,
                new PerfPartitionCellRequest(
                    PerfFinalGateScenarios.PartitionScenarioId(PerfFinalGateVariant.Unfiltered, number),
                    PerfFixtureDefinition.ResourceEndpoint,
                    number
                ),
                WarmupIterations,
                MeasuredIterations
            );
            AssertPartitionCell(partition, number);
        }
    }

    /// <summary>
    /// The authorized variant under the real second principal: a relationship claim on
    /// Ed-Fi/Student with the seed school's education organization id. The expected page
    /// membership is the even-ordinal candidate selection, so a lost authorization predicate
    /// fails the cell rather than passing with plausible rows.
    /// </summary>
    public static async Task RunAuthorizedAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Smoke10k);
        PerfAuthorizationSeedDefinition seed = new(definition);
        await PerfFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);
        await PerfAuthorizationSeeder.SeedAndVerifyAsync(harness.DbConnection, provider, seed);

        foreach (PerfCursorRange range in (PerfCursorRange[])[PerfCursorRange.First, PerfCursorRange.Last])
        {
            PerfCursorMeasuredCell cell = await PerfCursorScenarioExecutor.RunCellAsync(
                harness,
                provider,
                StudentCursorCell(PerfFinalGateVariant.Authorized, range, definition, filter: null),
                WarmupIterations,
                MeasuredIterations
            );
            AssertCursorCell(cell, PerfFinalGateVariant.Authorized, range);
        }

        PerfPartitionMeasuredCell partition = await PerfPartitionScenarioExecutor.RunCellAsync(
            harness,
            provider,
            new PerfPartitionCellRequest(
                PerfFinalGateScenarios.PartitionScenarioId(
                    PerfFinalGateVariant.Authorized,
                    PerfFinalGateScenarios.ScopedPartitionNumber
                ),
                PerfFixtureDefinition.ResourceEndpoint,
                PerfFinalGateScenarios.ScopedPartitionNumber
            ),
            WarmupIterations,
            MeasuredIterations
        );
        AssertPartitionCell(partition, PerfFinalGateScenarios.ScopedPartitionNumber);
    }

    /// <summary>
    /// The filtered variant after the overlay: the birthDate equality filter selects the
    /// every-tenth-student candidate set, and expected membership is that selection exactly.
    /// </summary>
    public static async Task RunFilteredAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Smoke10k);
        await PerfFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);
        await PerfFilteredOverlay.ApplyAndVerifyAsync(harness.DbConnection, provider, definition);

        string filter = $"birthDate={PerfFilteredOverlay.OverlayBirthDateIso}";

        foreach (PerfCursorRange range in (PerfCursorRange[])[PerfCursorRange.First, PerfCursorRange.Last])
        {
            PerfCursorMeasuredCell cell = await PerfCursorScenarioExecutor.RunCellAsync(
                harness,
                provider,
                StudentCursorCell(PerfFinalGateVariant.Filtered, range, definition, filter),
                WarmupIterations,
                MeasuredIterations
            );
            AssertCursorCell(cell, PerfFinalGateVariant.Filtered, range);
        }

        PerfPartitionMeasuredCell partition = await PerfPartitionScenarioExecutor.RunCellAsync(
            harness,
            provider,
            new PerfPartitionCellRequest(
                PerfFinalGateScenarios.PartitionScenarioId(
                    PerfFinalGateVariant.Filtered,
                    PerfFinalGateScenarios.ScopedPartitionNumber
                ),
                PerfFixtureDefinition.ResourceEndpoint,
                PerfFinalGateScenarios.ScopedPartitionNumber,
                filter
            ),
            WarmupIterations,
            MeasuredIterations
        );
        AssertPartitionCell(partition, PerfFinalGateScenarios.ScopedPartitionNumber);
    }

    /// <summary>
    /// The descriptor variant under the real namespace principal: only odd ordinals sit
    /// under the caller's prefix, and expected membership is that interleaved selection.
    /// </summary>
    public static async Task RunDescriptorAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        PerfDescriptorFixtureDefinition definition = new(PerfDescriptorFixtureKind.DescriptorsSmoke2k);
        await PerfDescriptorFixtureLoader.LoadAndVerifyAsync(harness.DbConnection, provider, definition);

        foreach (PerfCursorRange range in (PerfCursorRange[])[PerfCursorRange.First, PerfCursorRange.Last])
        {
            PerfCursorMeasuredCell cell = await PerfCursorScenarioExecutor.RunCellAsync(
                harness,
                provider,
                DescriptorCursorCell(range, definition),
                WarmupIterations,
                MeasuredIterations
            );
            AssertCursorCell(cell, PerfFinalGateVariant.Descriptor, range);
        }

        PerfPartitionMeasuredCell partition = await PerfPartitionScenarioExecutor.RunCellAsync(
            harness,
            provider,
            new PerfPartitionCellRequest(
                PerfFinalGateScenarios.PartitionScenarioId(
                    PerfFinalGateVariant.Descriptor,
                    PerfFinalGateScenarios.ScopedPartitionNumber
                ),
                PerfDescriptorFixtureDefinition.ResourceEndpoint,
                PerfFinalGateScenarios.ScopedPartitionNumber
            ),
            WarmupIterations,
            MeasuredIterations
        );
        AssertPartitionCell(partition, PerfFinalGateScenarios.ScopedPartitionNumber);
    }

    /// <summary>
    /// Builds a student cursor cell from the variant candidate math: the analytic start
    /// anchor, the exact expected page membership, and the exact expected next-token bound.
    /// </summary>
    private static PerfCursorCellRequest StudentCursorCell(
        PerfFinalGateVariant variant,
        PerfCursorRange range,
        PerfFixtureDefinition definition,
        string? filter
    )
    {
        long candidateCount = PerfVariantCandidates.CandidateCount(variant, definition.RowCount);
        long startCandidate = PerfVariantCandidates.StartCandidateIndex(range, candidateCount, PageSize);

        IReadOnlyList<Guid> expected =
        [
            .. Enumerable
                .Range(0, PageSize)
                .Select(offset =>
                    PerfFixtureDefinition.DocumentUuidFor(
                        PerfVariantCandidates.RowOrdinalOfCandidate(variant, startCandidate + offset)
                    )
                ),
        ];

        long startAnchor = PerfFixtureDefinition.DocumentIdFor(
            PerfVariantCandidates.RowOrdinalOfCandidate(variant, startCandidate)
        );
        long lastAnchor = PerfFixtureDefinition.DocumentIdFor(
            PerfVariantCandidates.RowOrdinalOfCandidate(variant, startCandidate + PageSize - 1)
        );

        return new PerfCursorCellRequest(
            PerfFinalGateScenarios.CursorScenarioId(variant, range),
            PerfFixtureDefinition.ResourceEndpoint,
            PageSize,
            startAnchor,
            lastAnchor + 1,
            expected,
            filter
        );
    }

    private static PerfCursorCellRequest DescriptorCursorCell(
        PerfCursorRange range,
        PerfDescriptorFixtureDefinition definition
    )
    {
        long candidateCount = PerfVariantCandidates.CandidateCount(
            PerfFinalGateVariant.Descriptor,
            definition.RowCount
        );
        long startCandidate = PerfVariantCandidates.StartCandidateIndex(range, candidateCount, PageSize);

        IReadOnlyList<Guid> expected =
        [
            .. Enumerable
                .Range(0, PageSize)
                .Select(offset =>
                    PerfDescriptorFixtureDefinition.DocumentUuidFor(
                        PerfVariantCandidates.RowOrdinalOfCandidate(
                            PerfFinalGateVariant.Descriptor,
                            startCandidate + offset
                        )
                    )
                ),
        ];

        long startAnchor = PerfDescriptorFixtureDefinition.DocumentIdFor(
            PerfVariantCandidates.RowOrdinalOfCandidate(PerfFinalGateVariant.Descriptor, startCandidate)
        );
        long lastAnchor = PerfDescriptorFixtureDefinition.DocumentIdFor(
            PerfVariantCandidates.RowOrdinalOfCandidate(
                PerfFinalGateVariant.Descriptor,
                startCandidate + PageSize - 1
            )
        );

        return new PerfCursorCellRequest(
            PerfFinalGateScenarios.CursorScenarioId(PerfFinalGateVariant.Descriptor, range),
            PerfDescriptorFixtureDefinition.ResourceEndpoint,
            PageSize,
            startAnchor,
            lastAnchor + 1,
            expected,
            FilterQueryString: null,
            PerfCursorCaptureChannel.RelationalCommand
        );
    }

    private static void AssertCursorCell(
        PerfCursorMeasuredCell cell,
        PerfFinalGateVariant variant,
        PerfCursorRange range
    )
    {
        cell.ScenarioId.Should().Be(PerfFinalGateScenarios.CursorScenarioId(variant, range));
        cell.ReturnedRows.Should().Be(PageSize);
        cell.CommandCountPerRequest.Should().Be(1);
        cell.PageSelection.Sha256.Should().NotBeNullOrEmpty();
        cell.HydrationBatchSql.Should().NotBeNullOrEmpty();
        cell.LatencyMs.P95Ms.Should().BeGreaterThan(0);
        cell.DriverExecuteMs.SamplesMs.Should().NotBeEmpty();
    }

    private static void AssertPartitionCell(PerfPartitionMeasuredCell cell, int requestedNumber)
    {
        cell.RequestedNumber.Should().Be(requestedNumber);
        cell.ReturnedTokenCount.Should().BeInRange(1, requestedNumber);
        cell.CommandCountPerRequest.Should().Be(1);
        cell.BoundarySql.Should().NotBeNullOrEmpty();
        cell.BoundarySqlSha256.Should().HaveLength(64);
        cell.LatencyMs.P95Ms.Should().BeGreaterThan(0);
    }
}
