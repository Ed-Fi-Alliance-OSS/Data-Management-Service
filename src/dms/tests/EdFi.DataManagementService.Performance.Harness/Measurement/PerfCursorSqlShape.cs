// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// The textual cursor-shape gate over a captured page-selection statement: cursor SQL must
/// contain no offset, no row-number skip, and no count query, must bind the cursor range and
/// page-size parameters — never the traditional offset/limit pair — and must bind the exact
/// values the cell requested. A page selection that fell back to traditional semantics is not
/// evidence, whatever its latency.
/// </summary>
public static class PerfCursorSqlShape
{
    /// <summary>
    /// Text fragments whose presence disqualifies a cursor page selection, matched
    /// case-insensitively against the whole statement.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenFragments = ["offset", "row_number", "count("];

    public static void EnsureCursorShaped(
        PageSelectionQueryCapture capture,
        long expectedInclusiveMinimum,
        int expectedPageSize,
        string at
    )
    {
        foreach (string fragment in ForbiddenFragments)
        {
            if (capture.PageDocumentIdSql.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                throw new PerfObservationException(
                    $"{at}: cursor page-selection SQL must not contain '{fragment}'."
                );
            }
        }

        foreach (
            string forbiddenParameter in (string[])
                [PageCandidateParameterNames.Offset, PageCandidateParameterNames.Limit]
        )
        {
            if (capture.ParameterValues.ContainsKey(forbiddenParameter))
            {
                throw new PerfObservationException(
                    $"{at}: cursor page selection must not bind the traditional '{forbiddenParameter}' parameter."
                );
            }
        }

        VerifyBoundValue(
            capture,
            PageCandidateParameterNames.CursorInclusiveMinimum,
            expectedInclusiveMinimum,
            at
        );
        VerifyBoundValue(capture, PageCandidateParameterNames.PageSize, expectedPageSize, at);

        if (!capture.ParameterValues.ContainsKey(PageCandidateParameterNames.CursorInclusiveMaximum))
        {
            throw new PerfObservationException(
                $"{at}: cursor page selection must bind the inclusive maximum bound."
            );
        }
    }

    private static void VerifyBoundValue(
        PageSelectionQueryCapture capture,
        string parameterName,
        long expected,
        string at
    )
    {
        if (!capture.ParameterValues.TryGetValue(parameterName, out object? value) || value is null)
        {
            throw new PerfObservationException($"{at}: bound parameter '{parameterName}' was not captured.");
        }

        long actual = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (actual != expected)
        {
            throw new PerfObservationException(
                $"{at}: bound parameter '{parameterName}' was {actual}; expected {expected}."
            );
        }
    }
}
