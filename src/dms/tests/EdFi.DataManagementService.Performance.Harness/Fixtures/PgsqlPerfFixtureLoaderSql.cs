// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// PostgreSQL loader SQL for the fixture definition. Rows are generated set-based with
/// generate_series so the arithmetic below must mirror <see cref="PerfFixtureDefinition" />
/// exactly. dms.Document is inserted before edfi.Student as two separate commands, because
/// the Student stamp trigger reads the matching dms.Document row. Production triggers and
/// constraints stay enabled throughout.
/// </summary>
public static class PgsqlPerfFixtureLoaderSql
{
    public const string ResourceKeyLookupSql = $"""
        SELECT "ResourceKeyId"
        FROM "dms"."ResourceKey"
        WHERE "ProjectName" = '{PerfFixtureDefinition.ProjectName}'
            AND "ResourceName" = '{PerfFixtureDefinition.ResourceName}';
        """;

    public const string DocumentInsertSql = $"""
        INSERT INTO "dms"."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId")
        OVERRIDING SYSTEM VALUE
        SELECT
            ((n - 1) / 9) * 10 + ((n - 1) % 9) + 2,
            ('{PerfFixtureDefinition.DocumentUuidPrefix}' || lpad(to_hex(n), 12, '0'))::uuid,
            @{PerfFixtureLoaderParameters.ResourceKeyId}
        FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
        """;

    public const string StudentInsertSql = $"""
        INSERT INTO "edfi"."Student" ("DocumentId", "StudentUniqueId", "FirstName", "LastSurname", "BirthDate")
        SELECT
            ((n - 1) / 9) * 10 + ((n - 1) % 9) + 2,
            'perf-' || lpad(n::text, 9, '0'),
            '{PerfFixtureDefinition.FirstName}',
            '{PerfFixtureDefinition.LastSurname}',
            DATE '{PerfFixtureDefinition.BirthDateIso}'
        FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
        """;

    /// <summary>
    /// RESTART WITH takes the next value to generate, so the first post-fixture insert
    /// receives MaxDocumentId + 1.
    /// </summary>
    public static string ReseedSql(PerfFixtureDefinition definition) =>
        $"""
            ALTER TABLE "dms"."Document" ALTER COLUMN "DocumentId" RESTART WITH {definition.MaxDocumentId
                + 1};
            """;

    /// <summary>
    /// Run outside a transaction; VACUUM cannot execute inside one.
    /// </summary>
    public static readonly IReadOnlyList<string> StatisticsRefreshSqls =
    [
        """VACUUM (ANALYZE) "dms"."Document";""",
        """VACUUM (ANALYZE) "edfi"."Student";""",
    ];

    /// <summary>
    /// Expected document counts assume the freshly provisioned baseline database holds no
    /// dms.Document rows before the load.
    /// </summary>
    public static IReadOnlyList<PerfVerificationQuery> VerificationQueries(
        PerfFixtureDefinition definition
    ) =>
        [
            new("student-row-count", """SELECT COUNT(*) FROM "edfi"."Student";""", definition.RowCount),
            new("document-row-count", """SELECT COUNT(*) FROM "dms"."Document";""", definition.RowCount),
            new(
                "document-student-pairing",
                """
                SELECT COUNT(*)
                FROM "edfi"."Student" s
                INNER JOIN "dms"."Document" d ON d."DocumentId" = s."DocumentId";
                """,
                definition.RowCount
            ),
            new(
                "min-document-id",
                """SELECT MIN("DocumentId") FROM "dms"."Document";""",
                PerfFixtureDefinition.MinDocumentId
            ),
            new(
                "max-document-id",
                """SELECT MAX("DocumentId") FROM "dms"."Document";""",
                definition.MaxDocumentId
            ),
            new(
                "gap-count",
                """SELECT MAX("DocumentId") - COUNT(*) FROM "dms"."Document";""",
                definition.GapCount
            ),
            new(
                "gap-id-emissions",
                """SELECT COUNT(*) FROM "dms"."Document" WHERE "DocumentId" % 10 = 1;""",
                0
            ),
            new(
                "document-id-sum",
                """SELECT SUM("DocumentId")::bigint FROM "dms"."Document";""",
                definition.DocumentIdSum()
            ),
        ];
}
