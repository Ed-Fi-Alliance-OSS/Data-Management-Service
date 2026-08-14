// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Generates relationship-authorization rows at volume with direct set-based SQL, bypassing the document write
/// pipeline (DMS-1331). The page and totalCount SQL under measurement read only the root table, the person
/// path's intermediate tables and the authorization view, so nothing those queries touch needs a valid Ed-Fi
/// payload.
/// </summary>
/// <remarks>
/// <para>
/// What this does NOT bypass: every generated <c>edfi</c> table carries a FOR EACH ROW
/// <c>TR_&lt;Table&gt;_ReferentialIdentity</c> trigger that hashes the natural key into
/// <c>dms.ReferentialIdentity</c>, whose <c>ReferentialId</c> is the primary key. Two generated rows whose
/// hashed columns coincide therefore fail on a primary-key violation. Every root table in this inventory
/// hashes the student unique id (directly, or through the StudentSectionAssociation / StudentAcademicRecord it
/// points at), so varying Student per row satisfies all of them. A second FOR EACH ROW trigger stamps
/// <c>dms.Document."ContentVersion"</c> and writes <c>tracked_changes_edfi.*</c>, which is why this is not the
/// pure bulk insert one statement per table suggests, and why the 150k lane is [Explicit].
/// </para>
/// <para>
/// Generated rows carry no JSON payload, so they cannot be hydrated. That is the one thing missing, not the
/// referential identities.
/// </para>
/// <para>
/// Referential foreign keys stay enabled throughout — no dropped constraints, no disabled triggers. An
/// unconstrained schema would measure a shape the product never runs. Three constraints that follow from that:
/// </para>
/// <list type="number">
/// <item>
/// Unified columns such as <c>Section."CourseOffering_SchoolReferenceSchoolId"</c> and
/// <c>Grade."StudentSectionAssociation_SchoolId"</c> are <c>GENERATED ALWAYS AS ... STORED</c> and are never
/// named in an INSERT column list. Hanging the whole Section/Course/GradingPeriod chain off one designated
/// school and one school year makes every derived value match its parent's reference key by construction.
/// </item>
/// <item>
/// Grade's foreign key to StudentSectionAssociation is the full reference key, not just DocumentId, so Grade is
/// built as <c>INSERT ... SELECT ... FROM edfi."StudentSectionAssociation"</c>. CourseTranscript relates to
/// StudentAcademicRecord the same way.
/// </item>
/// <item>
/// Authorization is per school: the student auth view joins the EdOrg closure to
/// <c>StudentSchoolAssociation."SchoolId_Unified"</c>, so reachability belongs to the enrollment, not the
/// student. Two schools are seeded — a reachable one carrying the chain and the authorized students'
/// enrollments, and an unreachable one carrying only the unauthorized students' enrollments. With one school
/// the claim would authorize every generated student or none, and the split would collapse silently.
/// </item>
/// </list>
/// </remarks>
internal static class PostgresqlRelationalQueryAuthorizationVolumeGenerator
{
    private const int StudentUniqueIdOrdinalOffset = 5;

    /// <summary>
    /// Generation runs with a longer statement timeout than the harness default. One set-based INSERT per table
    /// is still one statement, but it fires the referential-identity and stamp triggers once per row, so at the
    /// [Explicit] 150,000-row scale a single statement runs well past the shared 300-second default. The
    /// timeout is restored afterwards, so every other statement against this database keeps the shorter one
    /// that makes a genuinely hung statement fail fast.
    /// </summary>
    private const int VolumeGenerationCommandTimeoutSeconds = 3600;

    public static async Task<RelationshipAuthorizationVolumeGenerationResult> GenerateAsync(
        PostgresqlRelationalQueryAuthorizationTestContext context,
        RelationshipAuthorizationVolumeCounts counts
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(counts);

        var previousCommandTimeoutSeconds = context.Database.CommandTimeoutSeconds;
        context.Database.CommandTimeoutSeconds = VolumeGenerationCommandTimeoutSeconds;

        try
        {
            await SeedBaselinePrerequisitesAsync(context);
            await SeedSingletonParentsAsync(context.Database);
            await context.InsertAuthEdgeAsync(
                RelationshipAuthorizationVolumeIdentifiers.ClaimEducationOrganizationId,
                RelationshipAuthorizationVolumeIdentifiers.ReachableSchoolId
            );
            await GenerateVolumeRowsAsync(context.Database, counts);
            await AnalyzeGeneratedTablesAsync(context.Database);

            return await ReadGenerationResultAsync(context.Database, counts);
        }
        finally
        {
            context.Database.CommandTimeoutSeconds = previousCommandTimeoutSeconds;
        }
    }

    /// <summary>
    /// The generator owns its whole inventory. Each context provisions a fresh database, so nothing another
    /// fixture seeded is available here — "School / SchoolYearType / TermDescriptor / GradeLevelDescriptor are
    /// already seeded" holds only inside Given_A_Postgresql_RelationalQueryAuthorization.
    /// </summary>
    private static async Task SeedBaselinePrerequisitesAsync(
        PostgresqlRelationalQueryAuthorizationTestContext context
    )
    {
        await context.SeedSchoolDescriptorDataAsync();

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await context.CreateSchoolAsync(
                new QuerySchoolSeed(
                    new DocumentUuid(Guid.Parse("d0000001-0000-4000-8000-000000000001")),
                    RelationshipAuthorizationVolumeIdentifiers.ReachableSchoolId,
                    "Volume Reachable School"
                )
            )
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await context.CreateSchoolAsync(
                new QuerySchoolSeed(
                    new DocumentUuid(Guid.Parse("d0000001-0000-4000-8000-000000000002")),
                    RelationshipAuthorizationVolumeIdentifiers.UnreachableSchoolId,
                    "Volume Unreachable School"
                )
            )
        );

        await context.SeedSchoolYearTypeAsync(
            new SchoolYearTypeSeed(
                new DocumentUuid(Guid.Parse("d0000002-0000-4000-8000-000000000001")),
                RelationshipAuthorizationVolumeIdentifiers.SchoolYear,
                true,
                "Volume School Year"
            )
        );

        await context.SeedTermDescriptorAsync(
            Guid.Parse("d0000003-0000-4000-8000-000000000001"),
            RelationshipAuthorizationVolumeIdentifiers.TermDescriptorUri
        );
        await context.SeedGradingPeriodDescriptorAsync(
            Guid.Parse("d0000003-0000-4000-8000-000000000002"),
            RelationshipAuthorizationVolumeIdentifiers.GradingPeriodDescriptorUri
        );
        await context.SeedGradeTypeDescriptorAsync(
            Guid.Parse("d0000003-0000-4000-8000-000000000003"),
            RelationshipAuthorizationVolumeIdentifiers.GradeTypeDescriptorUri
        );
        await context.SeedCourseAttemptResultDescriptorAsync(
            Guid.Parse("d0000003-0000-4000-8000-000000000004"),
            RelationshipAuthorizationVolumeIdentifiers.CourseAttemptResultDescriptorUri
        );
        await context.SeedAttendanceEventCategoryDescriptorAsync(
            Guid.Parse("d0000003-0000-4000-8000-000000000005"),
            RelationshipAuthorizationVolumeIdentifiers.AttendanceEventCategoryDescriptorUri
        );
    }

    /// <summary>
    /// Six singleton rows close the parent inventory over the five root tables: Course, Session,
    /// CourseOffering, Section, GradingPeriod and GradebookEntry. Volume lives in Student, the two path
    /// intermediates and the root tables; the chain above them stays at one row each, which every
    /// <c>UX_*_NK</c> admits.
    /// </summary>
    private static async Task SeedSingletonParentsAsync(PostgresqlGeneratedDdlTestDatabase database)
    {
        // Course -> School.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT 'd0000010-0000-4000-8000-000000000001'::uuid, {ResourceKeyIdSql("Course")}
                RETURNING "DocumentId"
            )
            INSERT INTO "edfi"."Course" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "CourseCode",
                "CourseTitle",
                "NumberOfParts"
            )
            SELECT
                docs."DocumentId",
                {SchoolDocumentIdSql("@reachableSchoolId")},
                @reachableSchoolId,
                @courseCode,
                @courseTitle,
                1
            FROM docs;
            """,
            ReachableSchoolIdParameter(),
            new NpgsqlParameter("courseCode", RelationshipAuthorizationVolumeIdentifiers.CourseCode),
            new NpgsqlParameter("courseTitle", RelationshipAuthorizationVolumeIdentifiers.CourseTitle)
        );

        // Session -> School, SchoolYearType, TermDescriptor. "School_SchoolId" is a plain column here, not a
        // generated one, so it is named explicitly.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT 'd0000010-0000-4000-8000-000000000002'::uuid, {ResourceKeyIdSql("Session")}
                RETURNING "DocumentId"
            )
            INSERT INTO "edfi"."Session" (
                "DocumentId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "TermDescriptor_DescriptorId",
                "BeginDate",
                "EndDate",
                "SessionName",
                "TotalInstructionalDays"
            )
            SELECT
                docs."DocumentId",
                {SchoolYearDocumentIdSql()},
                @schoolYear,
                {SchoolDocumentIdSql("@reachableSchoolId")},
                @reachableSchoolId,
                {DescriptorDocumentIdSql("TermDescriptor", "@termDescriptorUri")},
                DATE '2024-08-01',
                DATE '2024-12-20',
                @sessionName,
                90
            FROM docs;
            """,
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new NpgsqlParameter(
                "termDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.TermDescriptorUri
            ),
            new NpgsqlParameter("sessionName", RelationshipAuthorizationVolumeIdentifiers.SessionName)
        );

        // CourseOffering -> Course, School, Session. "School_SchoolId" and "Session_SchoolId" are GENERATED
        // ALWAYS from "SchoolId_Unified" and must not appear in the column list.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT 'd0000010-0000-4000-8000-000000000003'::uuid, {ResourceKeyIdSql("CourseOffering")}
                RETURNING "DocumentId"
            )
            INSERT INTO "edfi"."CourseOffering" (
                "DocumentId",
                "SchoolId_Unified",
                "Course_DocumentId",
                "Course_CourseCode",
                "Course_EducationOrganizationId",
                "School_DocumentId",
                "Session_DocumentId",
                "Session_SchoolYear",
                "Session_SessionName",
                "LocalCourseCode"
            )
            SELECT
                docs."DocumentId",
                @reachableSchoolId,
                course."DocumentId",
                @courseCode,
                @reachableSchoolId,
                {SchoolDocumentIdSql("@reachableSchoolId")},
                session."DocumentId",
                @schoolYear,
                @sessionName,
                @localCourseCode
            FROM docs
            CROSS JOIN "edfi"."Course" course
            CROSS JOIN "edfi"."Session" session;
            """,
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new NpgsqlParameter("courseCode", RelationshipAuthorizationVolumeIdentifiers.CourseCode),
            new NpgsqlParameter("sessionName", RelationshipAuthorizationVolumeIdentifiers.SessionName),
            new NpgsqlParameter("localCourseCode", RelationshipAuthorizationVolumeIdentifiers.LocalCourseCode)
        );

        // Section -> CourseOffering. The two "CourseOffering_*ReferenceSchoolId" columns are GENERATED ALWAYS
        // from "SchoolId_Unified"; "SchoolId_U35501e03_Unified" belongs to the nullable Location groups and
        // stays unset.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT 'd0000010-0000-4000-8000-000000000004'::uuid, {ResourceKeyIdSql("Section")}
                RETURNING "DocumentId"
            )
            INSERT INTO "edfi"."Section" (
                "DocumentId",
                "SchoolId_Unified",
                "CourseOffering_DocumentId",
                "CourseOffering_LocalCourseCode",
                "CourseOffering_SchoolYear",
                "CourseOffering_SessionName",
                "SectionIdentifier"
            )
            SELECT
                docs."DocumentId",
                @reachableSchoolId,
                courseOffering."DocumentId",
                @localCourseCode,
                @schoolYear,
                @sessionName,
                @sectionIdentifier
            FROM docs
            CROSS JOIN "edfi"."CourseOffering" courseOffering;
            """,
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new NpgsqlParameter(
                "localCourseCode",
                RelationshipAuthorizationVolumeIdentifiers.LocalCourseCode
            ),
            new NpgsqlParameter("sessionName", RelationshipAuthorizationVolumeIdentifiers.SessionName),
            new NpgsqlParameter(
                "sectionIdentifier",
                RelationshipAuthorizationVolumeIdentifiers.SectionIdentifier
            )
        );

        // GradingPeriod -> School, SchoolYearType, GradingPeriodDescriptor.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT 'd0000010-0000-4000-8000-000000000005'::uuid, {ResourceKeyIdSql("GradingPeriod")}
                RETURNING "DocumentId"
            )
            INSERT INTO "edfi"."GradingPeriod" (
                "DocumentId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "GradingPeriodDescriptor_DescriptorId",
                "BeginDate",
                "EndDate",
                "GradingPeriodName",
                "TotalInstructionalDays"
            )
            SELECT
                docs."DocumentId",
                {SchoolYearDocumentIdSql()},
                @schoolYear,
                {SchoolDocumentIdSql("@reachableSchoolId")},
                @reachableSchoolId,
                {DescriptorDocumentIdSql("GradingPeriodDescriptor", "@gradingPeriodDescriptorUri")},
                DATE '2024-08-01',
                DATE '2024-09-13',
                @gradingPeriodName,
                30
            FROM docs;
            """,
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new NpgsqlParameter(
                "gradingPeriodDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.GradingPeriodDescriptorUri
            ),
            new NpgsqlParameter(
                "gradingPeriodName",
                RelationshipAuthorizationVolumeIdentifiers.GradingPeriodName
            )
        );

        // GradebookEntry requires no parent: its GradingPeriod and Section reference groups are entirely
        // nullable, so they stay unset.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT 'd0000010-0000-4000-8000-000000000006'::uuid, {ResourceKeyIdSql("GradebookEntry")}
                RETURNING "DocumentId"
            )
            INSERT INTO "edfi"."GradebookEntry" (
                "DocumentId",
                "DateAssigned",
                "GradebookEntryIdentifier",
                "Namespace",
                "SourceSectionIdentifier",
                "Title"
            )
            SELECT
                docs."DocumentId",
                DATE '2024-09-03',
                @gradebookEntryIdentifier,
                @gradebookEntryNamespace,
                @sectionIdentifier,
                'Volume Gradebook Entry'
            FROM docs;
            """,
            new NpgsqlParameter(
                "gradebookEntryIdentifier",
                RelationshipAuthorizationVolumeIdentifiers.GradebookEntryIdentifier
            ),
            new NpgsqlParameter(
                "gradebookEntryNamespace",
                RelationshipAuthorizationVolumeIdentifiers.GradebookEntryNamespace
            ),
            new NpgsqlParameter(
                "sectionIdentifier",
                RelationshipAuthorizationVolumeIdentifiers.SectionIdentifier
            )
        );
    }

    private static async Task GenerateVolumeRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        RelationshipAuthorizationVolumeCounts counts
    )
    {
        // Students. One shared population feeds every root table: each root's UX_*_NK admits one row per
        // student, so five roots reuse the same students rather than needing five populations.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT
                    ('a0000000-0000-4000-8000-' || lpad(series::text, 12, '0'))::uuid,
                    {ResourceKeyIdSql("Student")}
                FROM generate_series(1, @totalRows) AS series
                RETURNING "DocumentId"
            ),
            numbered AS (
                SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS ordinal
                FROM docs
            )
            INSERT INTO "edfi"."Student" (
                "DocumentId",
                "BirthDate",
                "FirstName",
                "LastSurname",
                "StudentUniqueId"
            )
            SELECT
                numbered."DocumentId",
                DATE '2010-05-14',
                'Volume',
                'Student',
                @studentUniqueIdPrefix || lpad(numbered.ordinal::text, 8, '0')
            FROM numbered;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter()
        );

        // StudentSchoolAssociation feeds auth.EducationOrganizationIdToStudentDocumentId, which is a view over
        // this table joined on "SchoolId_Unified". Which of the two schools a student is enrolled at is the
        // only thing that decides whether the claim authorizes that student's root rows.
        // "School_SchoolId" is GENERATED ALWAYS from "SchoolId_Unified" and is not named here.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH students AS ({StudentsWithOrdinalSql()}),
            docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT
                    ('a1000000-0000-4000-8000-' || lpad(series::text, 12, '0'))::uuid,
                    {ResourceKeyIdSql("StudentSchoolAssociation")}
                FROM generate_series(1, @totalRows) AS series
                RETURNING "DocumentId"
            ),
            numbered AS (
                SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS ordinal
                FROM docs
            )
            INSERT INTO "edfi"."StudentSchoolAssociation" (
                "DocumentId",
                "SchoolId_Unified",
                "School_DocumentId",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "EntryGradeLevelDescriptor_DescriptorId",
                "EntryDate"
            )
            SELECT
                numbered."DocumentId",
                CASE
                    WHEN {IsUnauthorizedSql("students.ordinal")} THEN @unreachableSchoolId
                    ELSE @reachableSchoolId
                END,
                CASE
                    WHEN {IsUnauthorizedSql("students.ordinal")}
                        THEN {SchoolDocumentIdSql("@unreachableSchoolId")}
                    ELSE {SchoolDocumentIdSql("@reachableSchoolId")}
                END,
                students."DocumentId",
                students."StudentUniqueId",
                {DescriptorDocumentIdSql("GradeLevelDescriptor", "@gradeLevelDescriptorUri")},
                DATE '2024-08-15'
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal;
            """,
            TotalRowsParameter(counts),
            StrideParameter(counts),
            StudentUniqueIdPrefixParameter(),
            ReachableSchoolIdParameter(),
            UnreachableSchoolIdParameter(),
            new NpgsqlParameter(
                "gradeLevelDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.GradeLevelDescriptorUri
            )
        );

        // StudentAcademicRecord: the CourseTranscript path's intermediate hop.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH students AS ({StudentsWithOrdinalSql()}),
            docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT
                    ('a2000000-0000-4000-8000-' || lpad(series::text, 12, '0'))::uuid,
                    {ResourceKeyIdSql("StudentAcademicRecord")}
                FROM generate_series(1, @totalRows) AS series
                RETURNING "DocumentId"
            ),
            numbered AS (
                SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS ordinal
                FROM docs
            )
            INSERT INTO "edfi"."StudentAcademicRecord" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "TermDescriptor_DescriptorId"
            )
            SELECT
                numbered."DocumentId",
                {SchoolDocumentIdSql("@reachableSchoolId")},
                @reachableSchoolId,
                {SchoolYearDocumentIdSql()},
                @schoolYear,
                students."DocumentId",
                students."StudentUniqueId",
                {DescriptorDocumentIdSql("TermDescriptor", "@termDescriptorUri")}
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter(),
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new NpgsqlParameter(
                "termDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.TermDescriptorUri
            )
        );

        // StudentSectionAssociation: both a target root table and Grade's parent. Root rows for unauthorized
        // students still reference the single Section on the reachable school — authorization is decided by
        // "Student_DocumentId" against the view, and no constraint ties the two.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH students AS ({StudentsWithOrdinalSql()}),
            docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT
                    ('a3000000-0000-4000-8000-' || lpad(series::text, 12, '0'))::uuid,
                    {ResourceKeyIdSql("StudentSectionAssociation")}
                FROM generate_series(1, @totalRows) AS series
                RETURNING "DocumentId"
            ),
            numbered AS (
                SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS ordinal
                FROM docs
            )
            INSERT INTO "edfi"."StudentSectionAssociation" (
                "DocumentId",
                "Section_DocumentId",
                "Section_LocalCourseCode",
                "Section_SchoolId",
                "Section_SchoolYear",
                "Section_SessionName",
                "Section_SectionIdentifier",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "BeginDate"
            )
            SELECT
                numbered."DocumentId",
                section."DocumentId",
                section."CourseOffering_LocalCourseCode",
                section."SchoolId_Unified",
                section."CourseOffering_SchoolYear",
                section."CourseOffering_SessionName",
                section."SectionIdentifier",
                students."DocumentId",
                students."StudentUniqueId",
                DATE '2024-08-20'
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal
            CROSS JOIN "edfi"."Section" section;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter()
        );

        // StudentSectionAttendanceEvent.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH students AS ({StudentsWithOrdinalSql()}),
            docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT
                    ('a4000000-0000-4000-8000-' || lpad(series::text, 12, '0'))::uuid,
                    {ResourceKeyIdSql("StudentSectionAttendanceEvent")}
                FROM generate_series(1, @totalRows) AS series
                RETURNING "DocumentId"
            ),
            numbered AS (
                SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS ordinal
                FROM docs
            )
            INSERT INTO "edfi"."StudentSectionAttendanceEvent" (
                "DocumentId",
                "Section_DocumentId",
                "Section_LocalCourseCode",
                "Section_SchoolId",
                "Section_SchoolYear",
                "Section_SessionName",
                "Section_SectionIdentifier",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "AttendanceEventCategoryDescriptor_DescriptorId",
                "EventDate"
            )
            SELECT
                numbered."DocumentId",
                section."DocumentId",
                section."CourseOffering_LocalCourseCode",
                section."SchoolId_Unified",
                section."CourseOffering_SchoolYear",
                section."CourseOffering_SessionName",
                section."SectionIdentifier",
                students."DocumentId",
                students."StudentUniqueId",
                {DescriptorDocumentIdSql(
                "AttendanceEventCategoryDescriptor",
                "@attendanceEventCategoryDescriptorUri"
            )},
                DATE '2024-09-05'
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal
            CROSS JOIN "edfi"."Section" section;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter(),
            new NpgsqlParameter(
                "attendanceEventCategoryDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.AttendanceEventCategoryDescriptorUri
            )
        );

        // StudentGradebookEntry.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH students AS ({StudentsWithOrdinalSql()}),
            docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT
                    ('a5000000-0000-4000-8000-' || lpad(series::text, 12, '0'))::uuid,
                    {ResourceKeyIdSql("StudentGradebookEntry")}
                FROM generate_series(1, @totalRows) AS series
                RETURNING "DocumentId"
            ),
            numbered AS (
                SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS ordinal
                FROM docs
            )
            INSERT INTO "edfi"."StudentGradebookEntry" (
                "DocumentId",
                "GradebookEntry_DocumentId",
                "GradebookEntry_GradebookEntryIdentifier",
                "GradebookEntry_Namespace",
                "Student_DocumentId",
                "Student_StudentUniqueId"
            )
            SELECT
                numbered."DocumentId",
                gradebookEntry."DocumentId",
                gradebookEntry."GradebookEntryIdentifier",
                gradebookEntry."Namespace",
                students."DocumentId",
                students."StudentUniqueId"
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal
            CROSS JOIN "edfi"."GradebookEntry" gradebookEntry;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter()
        );

        // Grade is built from StudentSectionAssociation rather than from generate_series alone: its foreign key
        // to StudentSectionAssociation is the full reference key, so BeginDate, LocalCourseCode,
        // SectionIdentifier, SessionName and StudentUniqueId are copied from the row it points at.
        // "StudentSectionAssociation_SchoolId"/"_SchoolYear" and "GradingPeriodGradingPeriod_SchoolId"/
        // "_SchoolYear" are GENERATED ALWAYS from "SchoolId_Unified"/"SchoolYear_Unified", which is why both
        // parents must sit on the same school and school year.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT
                    ('a6000000-0000-4000-8000-' || lpad(series::text, 12, '0'))::uuid,
                    {ResourceKeyIdSql("Grade")}
                FROM generate_series(1, @totalRows) AS series
                RETURNING "DocumentId"
            ),
            numbered AS (
                SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS ordinal
                FROM docs
            ),
            associations AS (
                SELECT
                    ssa.*,
                    row_number() OVER (ORDER BY ssa."DocumentId") AS ordinal
                FROM "edfi"."StudentSectionAssociation" ssa
            )
            INSERT INTO "edfi"."Grade" (
                "DocumentId",
                "SchoolId_Unified",
                "SchoolYear_Unified",
                "GradingPeriodGradingPeriod_DocumentId",
                "GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId",
                "GradingPeriodGradingPeriod_GradingPeriodName",
                "StudentSectionAssociation_DocumentId",
                "StudentSectionAssociation_BeginDate",
                "StudentSectionAssociation_LocalCourseCode",
                "StudentSectionAssociation_SectionIdentifier",
                "StudentSectionAssociation_SessionName",
                "StudentSectionAssociation_StudentUniqueId",
                "GradeTypeDescriptor_DescriptorId"
            )
            SELECT
                numbered."DocumentId",
                associations."Section_SchoolId",
                associations."Section_SchoolYear",
                gradingPeriod."DocumentId",
                gradingPeriod."GradingPeriodDescriptor_DescriptorId",
                gradingPeriod."GradingPeriodName",
                associations."DocumentId",
                associations."BeginDate",
                associations."Section_LocalCourseCode",
                associations."Section_SectionIdentifier",
                associations."Section_SessionName",
                associations."Student_StudentUniqueId",
                {DescriptorDocumentIdSql("GradeTypeDescriptor", "@gradeTypeDescriptorUri")}
            FROM numbered
            INNER JOIN associations ON associations.ordinal = numbered.ordinal
            CROSS JOIN "edfi"."GradingPeriod" gradingPeriod;
            """,
            TotalRowsParameter(counts),
            new NpgsqlParameter(
                "gradeTypeDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.GradeTypeDescriptorUri
            )
        );

        // CourseTranscript relates to StudentAcademicRecord by its full reference key, so it is built from that
        // table for the same reason Grade is built from StudentSectionAssociation.
        await database.ExecuteNonQueryAsync(
            $"""
            WITH docs AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT
                    ('a7000000-0000-4000-8000-' || lpad(series::text, 12, '0'))::uuid,
                    {ResourceKeyIdSql("CourseTranscript")}
                FROM generate_series(1, @totalRows) AS series
                RETURNING "DocumentId"
            ),
            numbered AS (
                SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS ordinal
                FROM docs
            ),
            records AS (
                SELECT
                    sar.*,
                    row_number() OVER (ORDER BY sar."DocumentId") AS ordinal
                FROM "edfi"."StudentAcademicRecord" sar
            )
            INSERT INTO "edfi"."CourseTranscript" (
                "DocumentId",
                "CourseCourse_DocumentId",
                "CourseCourse_CourseCode",
                "CourseCourse_EducationOrganizationId",
                "StudentAcademicRecord_DocumentId",
                "StudentAcademicRecord_EducationOrganizationId",
                "StudentAcademicRecord_SchoolYear",
                "StudentAcademicRecord_StudentUniqueId",
                "StudentAcademicRecord_TermDescriptor_DescriptorId",
                "CourseAttemptResultDescriptor_DescriptorId"
            )
            SELECT
                numbered."DocumentId",
                course."DocumentId",
                course."CourseCode",
                course."EducationOrganization_EducationOrganizationId",
                records."DocumentId",
                records."EducationOrganization_EducationOrganizationId",
                records."SchoolYear_SchoolYear",
                records."Student_StudentUniqueId",
                records."TermDescriptor_DescriptorId",
                {DescriptorDocumentIdSql(
                "CourseAttemptResultDescriptor",
                "@courseAttemptResultDescriptorUri"
            )}
            FROM numbered
            INNER JOIN records ON records.ordinal = numbered.ordinal
            CROSS JOIN "edfi"."Course" course;
            """,
            TotalRowsParameter(counts),
            new NpgsqlParameter(
                "courseAttemptResultDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.CourseAttemptResultDescriptorUri
            )
        );
    }

    /// <summary>
    /// Autovacuum's analyze pass is asynchronous, so an EXPLAIN issued straight after a bulk load can plan
    /// against default estimates. Every table the generator wrote is analyzed here, before any measurement.
    /// <c>auth.EducationOrganizationIdToStudentDocumentId</c> is a view and cannot be analyzed — its base
    /// tables are what matter.
    /// </summary>
    private static async Task AnalyzeGeneratedTablesAsync(PostgresqlGeneratedDdlTestDatabase database)
    {
        await database.ExecuteNonQueryAsync(
            """
            ANALYZE "dms"."Document";
            ANALYZE "dms"."Descriptor";
            ANALYZE "dms"."ReferentialIdentity";
            ANALYZE "auth"."EducationOrganizationIdToEducationOrganizationId";
            ANALYZE "edfi"."School";
            ANALYZE "edfi"."SchoolYearType";
            ANALYZE "edfi"."Course";
            ANALYZE "edfi"."Session";
            ANALYZE "edfi"."CourseOffering";
            ANALYZE "edfi"."Section";
            ANALYZE "edfi"."GradingPeriod";
            ANALYZE "edfi"."GradebookEntry";
            ANALYZE "edfi"."Student";
            ANALYZE "edfi"."StudentSchoolAssociation";
            ANALYZE "edfi"."StudentAcademicRecord";
            ANALYZE "edfi"."StudentSectionAssociation";
            ANALYZE "edfi"."StudentSectionAttendanceEvent";
            ANALYZE "edfi"."StudentGradebookEntry";
            ANALYZE "edfi"."Grade";
            ANALYZE "edfi"."CourseTranscript";
            """
        );
    }

    private static async Task<RelationshipAuthorizationVolumeGenerationResult> ReadGenerationResultAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        RelationshipAuthorizationVolumeCounts counts
    )
    {
        var authorizedStudentCount = await database.ExecuteScalarAsync<long>(
            """
            SELECT count(DISTINCT authView."Student_DocumentId")
            FROM "auth"."EducationOrganizationIdToStudentDocumentId" authView
            WHERE authView."SourceEducationOrganizationId" = @claimEducationOrganizationId;
            """,
            ClaimEducationOrganizationIdParameter()
        );

        var unauthorizedStudentCount = await database.ExecuteScalarAsync<long>(
            """
            SELECT count(*)
            FROM "edfi"."Student" student
            WHERE student."StudentUniqueId" LIKE @studentUniqueIdPattern
              AND NOT EXISTS (
                  SELECT 1
                  FROM "auth"."EducationOrganizationIdToStudentDocumentId" authView
                  WHERE authView."Student_DocumentId" = student."DocumentId"
                    AND authView."SourceEducationOrganizationId" = @claimEducationOrganizationId
              );
            """,
            ClaimEducationOrganizationIdParameter(),
            new NpgsqlParameter(
                "studentUniqueIdPattern",
                RelationshipAuthorizationVolumeIdentifiers.StudentUniqueIdPrefix + "%"
            )
        );

        return new RelationshipAuthorizationVolumeGenerationResult(
            counts,
            authorizedStudentCount,
            unauthorizedStudentCount
        );
    }

    private static string StudentsWithOrdinalSql() =>
        $"""
            SELECT
                        student."DocumentId",
                        student."StudentUniqueId",
                        substring(student."StudentUniqueId" from {StudentUniqueIdOrdinalOffset})::bigint AS ordinal
                    FROM "edfi"."Student" student
                    WHERE student."StudentUniqueId" LIKE @studentUniqueIdPrefix || '%'
            """;

    private static string IsUnauthorizedSql(string ordinalExpression) => $"{ordinalExpression} % @stride = 0";

    private static string ResourceKeyIdSql(string resourceName) =>
        $"""(SELECT "ResourceKeyId" FROM "dms"."ResourceKey" WHERE "ProjectName" = 'Ed-Fi' AND "ResourceName" = '{resourceName}')""";

    private static string SchoolDocumentIdSql(string schoolIdExpression) =>
        $"""(SELECT school."DocumentId" FROM "edfi"."School" school WHERE school."SchoolId" = {schoolIdExpression})""";

    private static string SchoolYearDocumentIdSql() =>
        """(SELECT schoolYear."DocumentId" FROM "edfi"."SchoolYearType" schoolYear WHERE schoolYear."SchoolYear" = @schoolYear)""";

    private static string DescriptorDocumentIdSql(string resourceName, string uriParameter) =>
        $"""
            (SELECT descriptor."DocumentId"
                         FROM "dms"."Descriptor" descriptor
                         INNER JOIN "dms"."Document" document ON document."DocumentId" = descriptor."DocumentId"
                         WHERE document."ResourceKeyId" = {ResourceKeyIdSql(resourceName)}
                           AND descriptor."Uri" = {uriParameter})
            """;

    private static NpgsqlParameter TotalRowsParameter(RelationshipAuthorizationVolumeCounts counts) =>
        new("totalRows", counts.TotalRowsPerRoot);

    private static NpgsqlParameter StrideParameter(RelationshipAuthorizationVolumeCounts counts) =>
        new("stride", (long)counts.Stride);

    private static NpgsqlParameter StudentUniqueIdPrefixParameter() =>
        new("studentUniqueIdPrefix", RelationshipAuthorizationVolumeIdentifiers.StudentUniqueIdPrefix);

    private static NpgsqlParameter ReachableSchoolIdParameter() =>
        new("reachableSchoolId", (long)RelationshipAuthorizationVolumeIdentifiers.ReachableSchoolId);

    private static NpgsqlParameter UnreachableSchoolIdParameter() =>
        new("unreachableSchoolId", (long)RelationshipAuthorizationVolumeIdentifiers.UnreachableSchoolId);

    private static NpgsqlParameter SchoolYearParameter() =>
        new("schoolYear", RelationshipAuthorizationVolumeIdentifiers.SchoolYear);

    private static NpgsqlParameter ClaimEducationOrganizationIdParameter() =>
        new(
            "claimEducationOrganizationId",
            RelationshipAuthorizationVolumeIdentifiers.ClaimEducationOrganizationId
        );
}
