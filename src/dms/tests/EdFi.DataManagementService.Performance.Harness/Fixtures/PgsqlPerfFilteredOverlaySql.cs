// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// PostgreSQL SQL for the filtered-variant overlay. The update joins the candidate index k
/// to student ordinal 10k through the fixture id scheme, so the arithmetic must mirror
/// <see cref="PerfFilteredOverlay" /> exactly, and it targets exact DocumentIds rather than
/// an open predicate so rows outside the fixture id scheme can never be varied.
/// </summary>
public static class PgsqlPerfFilteredOverlaySql
{
    public const string UpdateSql = $"""
        UPDATE "edfi"."Student" s
        SET "BirthDate" = DATE '{PerfFilteredOverlay.OverlayBirthDateIso}'
        FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS k
        WHERE s."DocumentId" = ((k * 10 - 1) / 9) * 10 + ((k * 10 - 1) % 9) + 2;
        """;

    public static readonly IReadOnlyList<string> StatisticsRefreshSqls =
    [
        """VACUUM (ANALYZE) "edfi"."Student";""",
        """VACUUM (ANALYZE) "dms"."Document";""",
    ];

    public static IReadOnlyList<PerfVerificationQuery> VerificationQueries(
        PerfFixtureDefinition definition
    ) =>
        [
            new(
                "overlaid-student-count",
                $"""
                SELECT COUNT(*) FROM "edfi"."Student"
                WHERE "BirthDate" = DATE '{PerfFilteredOverlay.OverlayBirthDateIso}';
                """,
                PerfFilteredOverlay.OverlaidStudentCount(definition)
            ),
            new(
                "unvaried-student-count",
                $"""
                SELECT COUNT(*) FROM "edfi"."Student"
                WHERE "BirthDate" = DATE '{PerfFixtureDefinition.BirthDateIso}';
                """,
                definition.RowCount - PerfFilteredOverlay.OverlaidStudentCount(definition)
            ),
            new("student-row-count", """SELECT COUNT(*) FROM "edfi"."Student";""", definition.RowCount),
            new(
                "overlaid-document-id-sum",
                $"""
                SELECT SUM("DocumentId")::bigint FROM "edfi"."Student"
                WHERE "BirthDate" = DATE '{PerfFilteredOverlay.OverlayBirthDateIso}';
                """,
                PerfFilteredOverlay.OverlaidDocumentIdSum(definition)
            ),
            new(
                "student-identification-document-row-count",
                """SELECT COUNT(*) FROM "edfi"."StudentIdentificationDocument";""",
                definition.RowCount
            ),
            new(
                "student-other-name-row-count",
                """SELECT COUNT(*) FROM "edfi"."StudentOtherName";""",
                definition.RowCount
            ),
            new(
                "student-personal-identification-document-row-count",
                """SELECT COUNT(*) FROM "edfi"."StudentPersonalIdentificationDocument";""",
                definition.RowCount
            ),
            new(
                "student-visa-row-count",
                """SELECT COUNT(*) FROM "edfi"."StudentVisa";""",
                definition.RowCount
            ),
        ];
}
