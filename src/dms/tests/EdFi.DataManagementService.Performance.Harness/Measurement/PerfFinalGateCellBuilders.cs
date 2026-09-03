// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Builds cursor cell requests from the variant candidate math: the analytic start anchor,
/// the exact expected page membership, and the exact expected next-token bound. Shared by
/// the pipeline and the executor smokes so both measure identically defined cells.
/// </summary>
public static class PerfFinalGateCellBuilders
{
    /// <summary>
    /// The query string that selects the filtered variant's candidate set.
    /// </summary>
    public static string FilteredQueryString => $"birthDate={PerfFilteredOverlay.OverlayBirthDateIso}";

    public static PerfCursorCellRequest StudentCursorCell(
        PerfFinalGateVariant variant,
        PerfCursorRange range,
        PerfFixtureDefinition definition,
        int pageSize,
        string? filterQueryString
    )
    {
        long candidateCount = PerfVariantCandidates.CandidateCount(variant, definition.RowCount);
        long startCandidate = PerfVariantCandidates.StartCandidateIndex(range, candidateCount, pageSize);

        IReadOnlyList<Guid> expected =
        [
            .. Enumerable
                .Range(0, pageSize)
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
            PerfVariantCandidates.RowOrdinalOfCandidate(variant, startCandidate + pageSize - 1)
        );

        return new PerfCursorCellRequest(
            PerfFinalGateScenarios.CursorScenarioId(variant, range),
            PerfFixtureDefinition.ResourceEndpoint,
            pageSize,
            startAnchor,
            lastAnchor + 1,
            expected,
            filterQueryString
        );
    }

    public static PerfCursorCellRequest DescriptorCursorCell(
        PerfCursorRange range,
        PerfDescriptorFixtureDefinition definition,
        int pageSize
    )
    {
        long candidateCount = PerfVariantCandidates.CandidateCount(
            PerfFinalGateVariant.Descriptor,
            definition.RowCount
        );
        long startCandidate = PerfVariantCandidates.StartCandidateIndex(range, candidateCount, pageSize);

        IReadOnlyList<Guid> expected =
        [
            .. Enumerable
                .Range(0, pageSize)
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
                startCandidate + pageSize - 1
            )
        );

        return new PerfCursorCellRequest(
            PerfFinalGateScenarios.CursorScenarioId(PerfFinalGateVariant.Descriptor, range),
            PerfDescriptorFixtureDefinition.ResourceEndpoint,
            pageSize,
            startAnchor,
            lastAnchor + 1,
            expected,
            FilterQueryString: null,
            PerfCursorCaptureChannel.RelationalCommand
        );
    }
}
