// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// PostgreSQL SQL for the authorized-variant seeding. Association rows are generated
/// set-based over the candidate index k, enrolling student ordinal 2k, so the arithmetic
/// must mirror <see cref="PerfAuthorizationSeedDefinition" /> exactly. Only durable source
/// tables are written: the production triggers on edfi.School and
/// edfi.StudentSchoolAssociation create the hierarchy self-edge, the abstract identity row,
/// and every referential identity. Production triggers and constraints stay enabled
/// throughout.
/// </summary>
public static class PgsqlPerfAuthorizationSeedSql
{
    public static string SchoolDocumentInsertSql(PerfAuthorizationSeedDefinition seed) =>
        $"""
            INSERT INTO "dms"."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId")
            OVERRIDING SYSTEM VALUE
            VALUES ({seed.SchoolDocumentId}, '{PerfAuthorizationSeedDefinition.SchoolDocumentUuid}'::uuid, @{PerfFixtureLoaderParameters.ResourceKeyId});
            """;

    /// <summary>
    /// Only the payload-backed columns are written; the AuthHierarchy, AbstractIdentity, and
    /// ReferentialIdentity triggers do the rest, exactly as they do for a production POST.
    /// </summary>
    public static string SchoolInsertSql(PerfAuthorizationSeedDefinition seed) =>
        $"""
            INSERT INTO "edfi"."School" ("DocumentId", "NameOfInstitution", "SchoolId")
            VALUES ({seed.SchoolDocumentId}, '{PerfAuthorizationSeedDefinition.NameOfInstitution}', {PerfAuthorizationSeedDefinition.SchoolId});
            """;

    public static string SsaDocumentInsertSql(PerfAuthorizationSeedDefinition seed) =>
        $"""
            INSERT INTO "dms"."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId")
            OVERRIDING SYSTEM VALUE
            SELECT
                {seed.SsaDocumentIdBase} + k,
                ('{PerfAuthorizationSeedDefinition.SsaDocumentUuidPrefix}' || lpad(to_hex(k), 12, '0'))::uuid,
                @{PerfFixtureLoaderParameters.ResourceKeyId}
            FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS k;
            """;

    public static string SsaInsertSql(PerfAuthorizationSeedDefinition seed) =>
        $"""
            INSERT INTO "edfi"."StudentSchoolAssociation" ("DocumentId", "SchoolId_Unified", "School_DocumentId", "Student_DocumentId", "Student_StudentUniqueId", "EntryGradeLevelDescriptor_DescriptorId", "EntryDate")
            SELECT
                {seed.SsaDocumentIdBase} + k,
                {PerfAuthorizationSeedDefinition.SchoolId},
                {seed.SchoolDocumentId},
                ((k * 2 - 1) / 9) * 10 + ((k * 2 - 1) % 9) + 2,
                'perf-' || lpad((k * 2)::text, 9, '0'),
                {seed.GradeLevelDescriptorDocumentId},
                DATE '{PerfAuthorizationSeedDefinition.EntryDateIso}'
            FROM generate_series(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS k;
            """;

    public static string ReseedSql(PerfAuthorizationSeedDefinition seed) =>
        $"""
            ALTER TABLE "dms"."Document" ALTER COLUMN "DocumentId" RESTART WITH {seed.ReseedTargetDocumentId
                + 1};
            """;

    public static readonly IReadOnlyList<string> StatisticsRefreshSqls =
    [
        """VACUUM (ANALYZE) "dms"."Document";""",
        """VACUUM (ANALYZE) "edfi"."StudentSchoolAssociation";""",
        """VACUUM (ANALYZE) "edfi"."School";""",
        """VACUUM (ANALYZE) "auth"."EducationOrganizationIdToEducationOrganizationId";""",
        """VACUUM (ANALYZE) "dms"."ReferentialIdentity";""",
    ];

    private const string SsaResourceKeyFilter = $"""
        "ResourceKeyId" = (
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = '{PerfFixtureDefinition.ProjectName}'
                AND "ResourceName" = '{PerfAuthorizationSeedDefinition.StudentSchoolAssociationResourceName}')
        """;

    /// <summary>
    /// The candidate-parity check inverts the fixture id scheme: for a valid student
    /// DocumentId d, the row ordinal is ((d - 2) / 10) * 9 + ((d - 2) % 10) + 1, so an
    /// odd-ordinal (unauthorized) student enrolled by the seed is counted directly.
    /// </summary>
    public static IReadOnlyList<PerfVerificationQuery> VerificationQueries(
        PerfAuthorizationSeedDefinition seed
    )
    {
        string schoolIdText = PerfAuthorizationSeedDefinition.SchoolId.ToString(CultureInfo.InvariantCulture);
        return
        [
            new(
                "ssa-row-count",
                """SELECT COUNT(*) FROM "edfi"."StudentSchoolAssociation";""",
                seed.EnrolledStudentCount
            ),
            new(
                "ssa-document-count",
                $"""SELECT COUNT(*) FROM "dms"."Document" WHERE {SsaResourceKeyFilter};""",
                seed.EnrolledStudentCount
            ),
            new(
                "ssa-document-pairing",
                """
                SELECT COUNT(*)
                FROM "edfi"."StudentSchoolAssociation" ssa
                INNER JOIN "dms"."Document" d ON d."DocumentId" = ssa."DocumentId";
                """,
                seed.EnrolledStudentCount
            ),
            new(
                "ssa-distinct-students",
                """SELECT COUNT(DISTINCT "Student_DocumentId") FROM "edfi"."StudentSchoolAssociation";""",
                seed.EnrolledStudentCount
            ),
            new(
                "ssa-student-document-id-sum",
                """SELECT SUM("Student_DocumentId")::bigint FROM "edfi"."StudentSchoolAssociation";""",
                seed.EnrolledStudentDocumentIdSum()
            ),
            new(
                "ssa-odd-ordinal-enrollments",
                """
                SELECT COUNT(*)
                FROM "edfi"."StudentSchoolAssociation"
                WHERE ((("Student_DocumentId" - 2) / 10) * 9 + (("Student_DocumentId" - 2) % 10) + 1) % 2 = 1;
                """,
                0
            ),
            new(
                "ssa-referential-identity-count",
                $"""SELECT COUNT(*) FROM "dms"."ReferentialIdentity" WHERE {SsaResourceKeyFilter};""",
                seed.EnrolledStudentCount
            ),
            new("school-row-count", """SELECT COUNT(*) FROM "edfi"."School";""", 1),
            new(
                "school-self-auth-edge",
                $"""
                SELECT COUNT(*)
                FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
                WHERE "SourceEducationOrganizationId" = {schoolIdText}
                    AND "TargetEducationOrganizationId" = {schoolIdText};
                """,
                1
            ),
            new(
                "authorized-view-membership",
                $"""
                SELECT COUNT(DISTINCT "Student_DocumentId")
                FROM "auth"."EducationOrganizationIdToStudentDocumentId"
                WHERE "SourceEducationOrganizationId" = {schoolIdText};
                """,
                seed.EnrolledStudentCount
            ),
            new(
                "grade-level-descriptor-count",
                $"""
                SELECT COUNT(*) FROM "dms"."Descriptor"
                WHERE "Discriminator" = '{PerfAuthorizationSeedDefinition.GradeLevelDescriptorResource}';
                """,
                1
            ),
            new(
                "max-document-id",
                """SELECT MAX("DocumentId") FROM "dms"."Document";""",
                seed.ReseedTargetDocumentId
            ),
        ];
    }
}
