// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// PostgreSQL loader SQL for the fixture definition. Rows are generated set-based with
/// generate_series so the arithmetic below must mirror <see cref="PerfFixtureDefinition" />
/// exactly. Descriptor documents are inserted first (child rows and the root birth-sex
/// column reference them), then dms.Document before edfi.Student as two separate commands,
/// because the Student stamp trigger reads the matching dms.Document row, then the four
/// child collection tables after the root rows their stamp triggers update. Production
/// triggers and constraints stay enabled throughout.
/// </summary>
public static class PgsqlPerfFixtureLoaderSql
{
    public const string ResourceKeyLookupSql = $"""
        SELECT "ResourceKeyId"
        FROM "dms"."ResourceKey"
        WHERE "ProjectName" = '{PerfFixtureDefinition.ProjectName}'
            AND "ResourceName" = '{PerfFixtureDefinition.ResourceName}';
        """;

    public static string DescriptorResourceKeyLookupSql(string resourceName) =>
        $"""
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = '{PerfFixtureDefinition.ProjectName}'
                AND "ResourceName" = '{resourceName}';
            """;

    public const string DescriptorDocumentInsertSql = $"""
        INSERT INTO "dms"."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId")
        OVERRIDING SYSTEM VALUE
        VALUES (@{PerfFixtureLoaderParameters.DescriptorDocumentId}, @{PerfFixtureLoaderParameters.DescriptorDocumentUuid}, @{PerfFixtureLoaderParameters.ResourceKeyId});
        """;

    /// <summary>
    /// Mirrors the production descriptor write: Uri is namespace#codeValue, Discriminator is
    /// the resource name, and ShortDescription echoes the code value. ContentVersion is
    /// stamped by the production trigger.
    /// </summary>
    public static string DescriptorInsertSql(string resourceName) =>
        $"""
            INSERT INTO "dms"."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription", "Discriminator", "Uri")
            VALUES (
                @{PerfFixtureLoaderParameters.DescriptorDocumentId},
                @{PerfFixtureLoaderParameters.ResourceKeyId},
                '{PerfFixtureDefinition.DescriptorNamespaceFor(resourceName)}',
                '{PerfFixtureDefinition.DescriptorCodeValue}',
                '{PerfFixtureDefinition.DescriptorCodeValue}',
                '{resourceName}',
                '{PerfFixtureDefinition.DescriptorUriFor(resourceName)}');
            """;

    /// <summary>
    /// The production descriptor write inserts one referential-identity row per descriptor;
    /// reference validation resolves descriptor URIs against it.
    /// </summary>
    public const string DescriptorReferentialIdentityInsertSql = $"""
        INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
        VALUES (@{PerfFixtureLoaderParameters.DescriptorReferentialId}, @{PerfFixtureLoaderParameters.DescriptorDocumentId}, @{PerfFixtureLoaderParameters.ResourceKeyId});
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
        INSERT INTO "edfi"."Student" ("DocumentId", "StudentUniqueId", "FirstName", "LastSurname", "BirthDate", "BirthSexDescriptor_DescriptorId")
        SELECT
            ((n - 1) / 9) * 10 + ((n - 1) % 9) + 2,
            'perf-' || lpad(n::text, 9, '0'),
            '{PerfFixtureDefinition.FirstName}',
            '{PerfFixtureDefinition.LastSurname}',
            DATE '{PerfFixtureDefinition.BirthDateIso}',
            @{PerfFixtureLoaderParameters.BirthSexDescriptorId}
        FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
        """;

    /// <summary>
    /// One row per student in each child collection table, mirroring what a production POST
    /// of the control payload writes: Ordinal 0 for the single item, CollectionItemId from
    /// the shared sequence default, and only the payload-backed columns non-null.
    /// </summary>
    public static readonly IReadOnlyList<string> ChildCollectionInsertSqls =
    [
        $"""
            INSERT INTO "edfi"."StudentIdentificationDocument" ("Ordinal", "Student_DocumentId", "IdentificationDocumentUseDescriptor_DescriptorId", "PersonalInformationVerificationDescriptor_DescriptorId")
            SELECT
                0,
                ((n - 1) / 9) * 10 + ((n - 1) % 9) + 2,
                @{PerfFixtureLoaderParameters.IdentificationDocumentUseDescriptorId},
                @{PerfFixtureLoaderParameters.PersonalInformationVerificationDescriptorId}
            FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
            """,
        $"""
            INSERT INTO "edfi"."StudentOtherName" ("Ordinal", "Student_DocumentId", "OtherNameTypeDescriptor_DescriptorId", "FirstName", "LastSurname")
            SELECT
                0,
                ((n - 1) / 9) * 10 + ((n - 1) % 9) + 2,
                @{PerfFixtureLoaderParameters.OtherNameTypeDescriptorId},
                '{PerfFixtureDefinition.FirstName}',
                '{PerfFixtureDefinition.LastSurname}'
            FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
            """,
        $"""
            INSERT INTO "edfi"."StudentPersonalIdentificationDocument" ("Ordinal", "Student_DocumentId", "IdentificationDocumentUseDescriptor_DescriptorId", "PersonalInformationVerificationDescriptor_DescriptorId")
            SELECT
                0,
                ((n - 1) / 9) * 10 + ((n - 1) % 9) + 2,
                @{PerfFixtureLoaderParameters.IdentificationDocumentUseDescriptorId},
                @{PerfFixtureLoaderParameters.PersonalInformationVerificationDescriptorId}
            FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
            """,
        $"""
            INSERT INTO "edfi"."StudentVisa" ("Ordinal", "Student_DocumentId", "VisaDescriptor_DescriptorId")
            SELECT
                0,
                ((n - 1) / 9) * 10 + ((n - 1) % 9) + 2,
                @{PerfFixtureLoaderParameters.VisaDescriptorId}
            FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS n;
            """,
    ];

    /// <summary>
    /// RESTART WITH takes the next value to generate, so the first post-fixture insert
    /// receives the id after the descriptor block that tops the student range.
    /// </summary>
    public static string ReseedSql(PerfFixtureDefinition definition) =>
        $"""
            ALTER TABLE "dms"."Document" ALTER COLUMN "DocumentId" RESTART WITH {definition.ReseedTargetDocumentId
                + 1};
            """;

    /// <summary>
    /// Run outside a transaction; VACUUM cannot execute inside one.
    /// </summary>
    public static readonly IReadOnlyList<string> StatisticsRefreshSqls =
    [
        """VACUUM (ANALYZE) "dms"."Document";""",
        """VACUUM (ANALYZE) "edfi"."Student";""",
        """VACUUM (ANALYZE) "edfi"."StudentIdentificationDocument";""",
        """VACUUM (ANALYZE) "edfi"."StudentOtherName";""",
        """VACUUM (ANALYZE) "edfi"."StudentPersonalIdentificationDocument";""",
        """VACUUM (ANALYZE) "edfi"."StudentVisa";""",
        """VACUUM (ANALYZE) "dms"."Descriptor";""",
    ];

    /// <summary>
    /// Restricts a document query to student documents, so the analytic id-scheme checks
    /// stay exact with the descriptor block loaded above the student range.
    /// </summary>
    private const string StudentResourceKeyFilter = $"""
        "ResourceKeyId" = (
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = '{PerfFixtureDefinition.ProjectName}'
                AND "ResourceName" = '{PerfFixtureDefinition.ResourceName}')
        """;

    /// <summary>
    /// Expected document counts assume the freshly provisioned baseline database holds no
    /// dms.Document rows before the load. Student-id-scheme checks are scoped to student
    /// documents; the descriptor block and the child collection tables get their own checks.
    /// </summary>
    public static IReadOnlyList<PerfVerificationQuery> VerificationQueries(
        PerfFixtureDefinition definition
    ) =>
        [
            new("student-row-count", """SELECT COUNT(*) FROM "edfi"."Student";""", definition.RowCount),
            new(
                "student-document-count",
                $"""SELECT COUNT(*) FROM "dms"."Document" WHERE {StudentResourceKeyFilter};""",
                definition.RowCount
            ),
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
                $"""SELECT MIN("DocumentId") FROM "dms"."Document" WHERE {StudentResourceKeyFilter};""",
                PerfFixtureDefinition.MinDocumentId
            ),
            new(
                "max-student-document-id",
                $"""SELECT MAX("DocumentId") FROM "dms"."Document" WHERE {StudentResourceKeyFilter};""",
                definition.MaxDocumentId
            ),
            new(
                "max-document-id",
                """SELECT MAX("DocumentId") FROM "dms"."Document";""",
                definition.ReseedTargetDocumentId
            ),
            new(
                "gap-count",
                $"""
                SELECT MAX("DocumentId") - COUNT(*) FROM "dms"."Document" WHERE {StudentResourceKeyFilter};
                """,
                definition.GapCount
            ),
            new(
                "gap-id-emissions",
                // Scoped to student documents: the primary fixture's descriptor block above
                // the student range can legitimately occupy ids congruent to 1 modulo 10.
                $"""
                SELECT COUNT(*) FROM "dms"."Document" WHERE "DocumentId" % 10 = 1 AND {StudentResourceKeyFilter};
                """,
                0
            ),
            new(
                "document-id-sum",
                $"""
                SELECT SUM("DocumentId")::bigint FROM "dms"."Document" WHERE {StudentResourceKeyFilter};
                """,
                definition.DocumentIdSum()
            ),
            new(
                "descriptor-row-count",
                """SELECT COUNT(*) FROM "dms"."Descriptor";""",
                PerfFixtureDefinition.DescriptorCount
            ),
            new(
                "descriptor-document-pairing",
                """
                SELECT COUNT(*)
                FROM "dms"."Descriptor" r
                INNER JOIN "dms"."Document" d ON d."DocumentId" = r."DocumentId";
                """,
                PerfFixtureDefinition.DescriptorCount
            ),
            new(
                "students-with-birth-sex-descriptor",
                """
                SELECT COUNT(*) FROM "edfi"."Student" WHERE "BirthSexDescriptor_DescriptorId" IS NOT NULL;
                """,
                definition.RowCount
            ),
            new(
                "student-identification-document-row-count",
                """SELECT COUNT(*) FROM "edfi"."StudentIdentificationDocument";""",
                definition.RowCount
            ),
            new(
                "student-identification-document-descriptor-bindings",
                """
                SELECT COUNT(*)
                FROM "edfi"."StudentIdentificationDocument"
                WHERE "IdentificationDocumentUseDescriptor_DescriptorId" IS NOT NULL
                    AND "PersonalInformationVerificationDescriptor_DescriptorId" IS NOT NULL;
                """,
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
