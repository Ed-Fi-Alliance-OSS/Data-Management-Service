// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// PostgreSQL loader SQL for the descriptor fixture. Rows are generated set-based with
/// generate_series and must mirror <see cref="PerfDescriptorFixtureDefinition" /> exactly:
/// dense DocumentIds, odd ordinals under the accessible namespace, Uri as namespace#codeValue,
/// and one referential identity per descriptor derived through the database's own uuidv5
/// function over the lowercased URI — the same derivation the production descriptor write
/// records. Production triggers and constraints stay enabled throughout.
/// </summary>
public static class PgsqlPerfDescriptorFixtureLoaderSql
{
    private const string NamespaceCaseExpression = $"""
        CASE WHEN n % 2 = 1 THEN '{PerfDescriptorFixtureDefinition.AccessibleNamespace}' ELSE '{PerfDescriptorFixtureDefinition.InaccessibleNamespace}' END
        """;

    private const string CodeValueExpression = "'perf-' || lpad(n::text, 9, '0')";

    public const string ResourceKeyLookupSql = $"""
        SELECT "ResourceKeyId"
        FROM "dms"."ResourceKey"
        WHERE "ProjectName" = '{PerfDescriptorFixtureDefinition.ProjectName}'
            AND "ResourceName" = '{PerfDescriptorFixtureDefinition.ResourceName}';
        """;

    public const string DocumentInsertSql = $"""
        INSERT INTO "dms"."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId")
        OVERRIDING SYSTEM VALUE
        SELECT
            n,
            ('{PerfDescriptorFixtureDefinition.DocumentUuidPrefix}' || lpad(to_hex(n), 12, '0'))::uuid,
            @{PerfFixtureLoaderParameters.ResourceKeyId}
        FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
        """;

    public const string DescriptorInsertSql = $"""
        INSERT INTO "dms"."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription", "Discriminator", "Uri")
        SELECT
            n,
            @{PerfFixtureLoaderParameters.ResourceKeyId},
            {NamespaceCaseExpression},
            {CodeValueExpression},
            {CodeValueExpression},
            '{PerfDescriptorFixtureDefinition.ResourceName}',
            {NamespaceCaseExpression} || '#' || {CodeValueExpression}
        FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
        """;

    /// <summary>
    /// Mirrors the production descriptor write's referential identity: uuidv5 over the
    /// project, resource, and the lowercased $.descriptor URI, computed by the database's
    /// own function so the set-based load and the write path can never drift apart.
    /// </summary>
    public const string ReferentialIdentityInsertSql = $"""
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        SELECT
            "dms"."uuidv5"(
                'edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid,
                '{PerfDescriptorFixtureDefinition.ProjectName}{PerfDescriptorFixtureDefinition.ResourceName}'
                    || '$.descriptor=' || LOWER({NamespaceCaseExpression} || '#' || {CodeValueExpression})),
            n,
            @{PerfFixtureLoaderParameters.ResourceKeyId}
        FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
        """;

    public static string ReseedSql(PerfDescriptorFixtureDefinition definition) =>
        $"""
            ALTER TABLE "dms"."Document" ALTER COLUMN "DocumentId" RESTART WITH {definition.ReseedTargetDocumentId
                + 1};
            """;

    public static readonly IReadOnlyList<string> StatisticsRefreshSqls =
    [
        """VACUUM (ANALYZE) "dms"."Document";""",
        """VACUUM (ANALYZE) "dms"."Descriptor";""",
        """VACUUM (ANALYZE) "dms"."ReferentialIdentity";""",
    ];

    public static IReadOnlyList<PerfVerificationQuery> VerificationQueries(
        PerfDescriptorFixtureDefinition definition
    ) =>
        [
            new("descriptor-row-count", """SELECT COUNT(*) FROM "dms"."Descriptor";""", definition.RowCount),
            new("document-count", """SELECT COUNT(*) FROM "dms"."Document";""", definition.RowCount),
            new(
                "descriptor-document-pairing",
                """
                SELECT COUNT(*)
                FROM "dms"."Descriptor" r
                INNER JOIN "dms"."Document" d ON d."DocumentId" = r."DocumentId";
                """,
                definition.RowCount
            ),
            new(
                "accessible-count",
                $"""
                SELECT COUNT(*) FROM "dms"."Descriptor"
                WHERE "Namespace" = '{PerfDescriptorFixtureDefinition.AccessibleNamespace}';
                """,
                definition.AccessibleCount
            ),
            new(
                "inaccessible-count",
                $"""
                SELECT COUNT(*) FROM "dms"."Descriptor"
                WHERE "Namespace" = '{PerfDescriptorFixtureDefinition.InaccessibleNamespace}';
                """,
                definition.RowCount - definition.AccessibleCount
            ),
            new(
                "accessible-even-ordinal-emissions",
                $"""
                SELECT COUNT(*) FROM "dms"."Descriptor"
                WHERE "Namespace" = '{PerfDescriptorFixtureDefinition.AccessibleNamespace}'
                    AND "DocumentId" % 2 = 0;
                """,
                0
            ),
            new("min-document-id", """SELECT MIN("DocumentId") FROM "dms"."Document";""", 1),
            new(
                "max-document-id",
                """SELECT MAX("DocumentId") FROM "dms"."Document";""",
                definition.MaxDocumentId
            ),
            new(
                "document-id-sum",
                """SELECT SUM("DocumentId")::bigint FROM "dms"."Document";""",
                definition.DocumentIdSum()
            ),
            new(
                "referential-identity-count",
                """SELECT COUNT(*) FROM "dms"."ReferentialIdentity";""",
                definition.RowCount
            ),
            new(
                "referential-identity-pairing",
                """
                SELECT COUNT(*)
                FROM "dms"."ReferentialIdentity" ri
                INNER JOIN "dms"."Descriptor" r ON r."DocumentId" = ri."DocumentId";
                """,
                definition.RowCount
            ),
            new(
                "uri-shape-count",
                """
                SELECT COUNT(*) FROM "dms"."Descriptor"
                WHERE "Uri" = "Namespace" || '#' || "CodeValue";
                """,
                definition.RowCount
            ),
        ];
}
