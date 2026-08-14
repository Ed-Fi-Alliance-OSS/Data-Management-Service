// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The SQL Server twin of <c>PostgresqlRelationalQueryAuthorizationVolumeGenerator</c> (DMS-1331). Same
/// inventory, same counts, same two-school reachability split, same generator traps — only the set-based INSERT
/// text differs: a numbers CTE and <c>OUTPUT ... INTO</c> in place of <c>generate_series</c> and
/// <c>RETURNING</c>, bracket quoting, and <see cref="SqlParameter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Rows are generated with direct set-based SQL, bypassing the document write pipeline. The page and totalCount
/// SQL under measurement read only the root table, the person path's intermediate tables and the authorization
/// view, so nothing those queries touch needs a valid Ed-Fi payload. Generated rows carry no JSON payload and so
/// cannot be hydrated; that is the one thing missing, not the referential identities.
/// </para>
/// <para>
/// What this does NOT bypass: every generated <c>edfi</c> table carries a <c>TR_&lt;Table&gt;_ReferentialIdentity</c>
/// trigger that hashes the natural key into <c>dms.ReferentialIdentity</c>, whose <c>ReferentialId</c> is the
/// primary key, so two generated rows whose hashed columns coincide fail on a primary-key violation. Every root
/// table in this inventory hashes the student unique id (directly, or through the StudentSectionAssociation /
/// StudentAcademicRecord it points at), so varying Student per row satisfies all of them. SQL Server's triggers
/// are set-based over the <c>inserted</c> pseudo-table, so one statement per table still writes one identity row
/// per generated row. A second trigger stamps <c>dms.Document.[ContentVersion]</c> and writes
/// <c>tracked_changes_edfi.*</c>, which is why this is not the pure bulk insert one statement per table suggests.
/// Unlike the hand-written seed helpers on this context, the generator leaves every trigger enabled — the point
/// is to measure the schema the product actually runs.
/// </para>
/// <para>
/// Referential foreign keys stay enabled throughout — no dropped constraints. Three constraints follow from that:
/// </para>
/// <list type="number">
/// <item>
/// Unified columns such as <c>Section.[CourseOffering_SchoolReferenceSchoolId]</c> and
/// <c>Grade.[StudentSectionAssociation_SchoolId]</c> are computed <c>PERSISTED</c> columns and are never named in
/// an INSERT column list. Hanging the whole Section/Course/GradingPeriod chain off one designated school and one
/// school year makes every derived value match its parent's reference key by construction.
/// </item>
/// <item>
/// Grade's foreign key to StudentSectionAssociation is the full reference key, not just DocumentId, so Grade is
/// built as <c>INSERT ... SELECT ... FROM [edfi].[StudentSectionAssociation]</c>. CourseTranscript relates to
/// StudentAcademicRecord the same way.
/// </item>
/// <item>
/// Authorization is per school: the student auth view joins the EdOrg closure to
/// <c>StudentSchoolAssociation.[SchoolId_Unified]</c>, so reachability belongs to the enrollment, not the student.
/// Two schools are seeded — a reachable one carrying the chain and the authorized students' enrollments, and an
/// unreachable one carrying only the unauthorized students' enrollments. With one school the claim would
/// authorize every generated student or none, and the split would collapse silently.
/// </item>
/// </list>
/// <para>
/// No statistics barrier is issued here. AC3 plan verification is PostgreSQL-only by existing precedent, so SQL
/// Server carries row-set equivalence rather than plan measurement.
/// </para>
/// </remarks>
internal static class MssqlRelationalQueryAuthorizationVolumeGenerator
{
    public static async Task<RelationshipAuthorizationVolumeGenerationResult> GenerateAsync(
        MssqlRelationalQueryAuthorizationTestContext context,
        RelationshipAuthorizationVolumeCounts counts
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(counts);

        await SeedBaselinePrerequisitesAsync(context);
        await SeedSingletonParentsAsync(context.Database);
        await context.InsertAuthEdgeAsync(
            RelationshipAuthorizationVolumeIdentifiers.ClaimEducationOrganizationId,
            RelationshipAuthorizationVolumeIdentifiers.ReachableSchoolId
        );
        await GenerateVolumeRowsAsync(context.Database, counts);

        return await ReadGenerationResultAsync(context.Database, counts);
    }

    /// <summary>
    /// The generator owns its whole inventory. Each context leases its own restored database, so nothing another
    /// fixture seeded is available here — "School / SchoolYearType / TermDescriptor / GradeLevelDescriptor are
    /// already seeded" holds only inside the fixtures that seed them.
    /// </summary>
    private static async Task SeedBaselinePrerequisitesAsync(
        MssqlRelationalQueryAuthorizationTestContext context
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
    /// Six singleton rows close the parent inventory over the five root tables: Course, Session, CourseOffering,
    /// Section, GradingPeriod and GradebookEntry. Volume lives in Student, the two path intermediates and the
    /// root tables; the chain above them stays at one row each, which every <c>UX_*_NK</c> admits.
    /// </summary>
    private static async Task SeedSingletonParentsAsync(MssqlGeneratedDdlTestDatabase database)
    {
        // Course -> School.
        await database.ExecuteNonQueryAsync(
            $"""
            {SingletonDocumentSql("d0000010-0000-4000-8000-000000000001", "Course")}

            INSERT INTO [edfi].[Course] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [CourseCode],
                [CourseTitle],
                [NumberOfParts]
            )
            SELECT
                docs.[DocumentId],
                {SchoolDocumentIdSql("@reachableSchoolId")},
                @reachableSchoolId,
                @courseCode,
                @courseTitle,
                1
            FROM @docs docs;
            """,
            ReachableSchoolIdParameter(),
            new SqlParameter("@courseCode", RelationshipAuthorizationVolumeIdentifiers.CourseCode),
            new SqlParameter("@courseTitle", RelationshipAuthorizationVolumeIdentifiers.CourseTitle)
        );

        // Session -> School, SchoolYearType, TermDescriptor. [School_SchoolId] is a plain column here, not a
        // computed one, so it is named explicitly.
        await database.ExecuteNonQueryAsync(
            $"""
            {SingletonDocumentSql("d0000010-0000-4000-8000-000000000002", "Session")}

            INSERT INTO [edfi].[Session] (
                [DocumentId],
                [SchoolYear_DocumentId],
                [SchoolYear_SchoolYear],
                [School_DocumentId],
                [School_SchoolId],
                [TermDescriptor_DescriptorId],
                [BeginDate],
                [EndDate],
                [SessionName],
                [TotalInstructionalDays]
            )
            SELECT
                docs.[DocumentId],
                {SchoolYearDocumentIdSql()},
                @schoolYear,
                {SchoolDocumentIdSql("@reachableSchoolId")},
                @reachableSchoolId,
                {DescriptorDocumentIdSql("TermDescriptor", "@termDescriptorUri")},
                CAST('2024-08-01' AS date),
                CAST('2024-12-20' AS date),
                @sessionName,
                90
            FROM @docs docs;
            """,
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new SqlParameter(
                "@termDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.TermDescriptorUri
            ),
            new SqlParameter("@sessionName", RelationshipAuthorizationVolumeIdentifiers.SessionName)
        );

        // CourseOffering -> Course, School, Session. [School_SchoolId] and [Session_SchoolId] are computed from
        // [SchoolId_Unified] and must not appear in the column list.
        await database.ExecuteNonQueryAsync(
            $"""
            {SingletonDocumentSql("d0000010-0000-4000-8000-000000000003", "CourseOffering")}

            INSERT INTO [edfi].[CourseOffering] (
                [DocumentId],
                [SchoolId_Unified],
                [Course_DocumentId],
                [Course_CourseCode],
                [Course_EducationOrganizationId],
                [School_DocumentId],
                [Session_DocumentId],
                [Session_SchoolYear],
                [Session_SessionName],
                [LocalCourseCode]
            )
            SELECT
                docs.[DocumentId],
                @reachableSchoolId,
                course.[DocumentId],
                @courseCode,
                @reachableSchoolId,
                {SchoolDocumentIdSql("@reachableSchoolId")},
                sessionRow.[DocumentId],
                @schoolYear,
                @sessionName,
                @localCourseCode
            FROM @docs docs
            CROSS JOIN [edfi].[Course] course
            CROSS JOIN [edfi].[Session] sessionRow;
            """,
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new SqlParameter("@courseCode", RelationshipAuthorizationVolumeIdentifiers.CourseCode),
            new SqlParameter("@sessionName", RelationshipAuthorizationVolumeIdentifiers.SessionName),
            new SqlParameter("@localCourseCode", RelationshipAuthorizationVolumeIdentifiers.LocalCourseCode)
        );

        // Section -> CourseOffering. The two [CourseOffering_*ReferenceSchoolId] columns are computed from
        // [SchoolId_Unified]; [SchoolId_U35501e03_Unified] belongs to the nullable Location groups and stays
        // unset.
        await database.ExecuteNonQueryAsync(
            $"""
            {SingletonDocumentSql("d0000010-0000-4000-8000-000000000004", "Section")}

            INSERT INTO [edfi].[Section] (
                [DocumentId],
                [SchoolId_Unified],
                [CourseOffering_DocumentId],
                [CourseOffering_LocalCourseCode],
                [CourseOffering_SchoolYear],
                [CourseOffering_SessionName],
                [SectionIdentifier]
            )
            SELECT
                docs.[DocumentId],
                @reachableSchoolId,
                courseOffering.[DocumentId],
                @localCourseCode,
                @schoolYear,
                @sessionName,
                @sectionIdentifier
            FROM @docs docs
            CROSS JOIN [edfi].[CourseOffering] courseOffering;
            """,
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new SqlParameter("@localCourseCode", RelationshipAuthorizationVolumeIdentifiers.LocalCourseCode),
            new SqlParameter("@sessionName", RelationshipAuthorizationVolumeIdentifiers.SessionName),
            new SqlParameter(
                "@sectionIdentifier",
                RelationshipAuthorizationVolumeIdentifiers.SectionIdentifier
            )
        );

        // GradingPeriod -> School, SchoolYearType, GradingPeriodDescriptor.
        await database.ExecuteNonQueryAsync(
            $"""
            {SingletonDocumentSql("d0000010-0000-4000-8000-000000000005", "GradingPeriod")}

            INSERT INTO [edfi].[GradingPeriod] (
                [DocumentId],
                [SchoolYear_DocumentId],
                [SchoolYear_SchoolYear],
                [School_DocumentId],
                [School_SchoolId],
                [GradingPeriodDescriptor_DescriptorId],
                [BeginDate],
                [EndDate],
                [GradingPeriodName],
                [TotalInstructionalDays]
            )
            SELECT
                docs.[DocumentId],
                {SchoolYearDocumentIdSql()},
                @schoolYear,
                {SchoolDocumentIdSql("@reachableSchoolId")},
                @reachableSchoolId,
                {DescriptorDocumentIdSql("GradingPeriodDescriptor", "@gradingPeriodDescriptorUri")},
                CAST('2024-08-01' AS date),
                CAST('2024-09-13' AS date),
                @gradingPeriodName,
                30
            FROM @docs docs;
            """,
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new SqlParameter(
                "@gradingPeriodDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.GradingPeriodDescriptorUri
            ),
            new SqlParameter(
                "@gradingPeriodName",
                RelationshipAuthorizationVolumeIdentifiers.GradingPeriodName
            )
        );

        // GradebookEntry requires no parent: its GradingPeriod and Section reference groups are entirely
        // nullable, so they stay unset.
        await database.ExecuteNonQueryAsync(
            $"""
            {SingletonDocumentSql("d0000010-0000-4000-8000-000000000006", "GradebookEntry")}

            INSERT INTO [edfi].[GradebookEntry] (
                [DocumentId],
                [DateAssigned],
                [GradebookEntryIdentifier],
                [Namespace],
                [SourceSectionIdentifier],
                [Title]
            )
            SELECT
                docs.[DocumentId],
                CAST('2024-09-03' AS date),
                @gradebookEntryIdentifier,
                @gradebookEntryNamespace,
                @sectionIdentifier,
                'Volume Gradebook Entry'
            FROM @docs docs;
            """,
            new SqlParameter(
                "@gradebookEntryIdentifier",
                RelationshipAuthorizationVolumeIdentifiers.GradebookEntryIdentifier
            ),
            new SqlParameter(
                "@gradebookEntryNamespace",
                RelationshipAuthorizationVolumeIdentifiers.GradebookEntryNamespace
            ),
            new SqlParameter(
                "@sectionIdentifier",
                RelationshipAuthorizationVolumeIdentifiers.SectionIdentifier
            )
        );
    }

    private static async Task GenerateVolumeRowsAsync(
        MssqlGeneratedDdlTestDatabase database,
        RelationshipAuthorizationVolumeCounts counts
    )
    {
        // Students. One shared population feeds every root table: each root's UX_*_NK admits one row per
        // student, so five roots reuse the same students rather than needing five populations.
        await database.ExecuteNonQueryAsync(
            $"""
            {VolumeDocumentsSql("a0000000", "Student")}

            INSERT INTO [edfi].[Student] (
                [DocumentId],
                [BirthDate],
                [FirstName],
                [LastSurname],
                [StudentUniqueId]
            )
            SELECT
                numbered.[DocumentId],
                CAST('2010-05-14' AS date),
                'Volume',
                'Student',
                @studentUniqueIdPrefix + RIGHT('00000000' + CAST(numbered.ordinal AS varchar(8)), 8)
            FROM ({NumberedDocumentsSql()}) numbered;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter()
        );

        // StudentSchoolAssociation feeds auth.EducationOrganizationIdToStudentDocumentId, which is a view over
        // this table joined on [SchoolId_Unified]. Which of the two schools a student is enrolled at is the only
        // thing that decides whether the claim authorizes that student's root rows. [School_SchoolId] is
        // computed from [SchoolId_Unified] and is not named here.
        await database.ExecuteNonQueryAsync(
            $"""
            {VolumeDocumentsSql("a1000000", "StudentSchoolAssociation")}

            WITH students AS ({StudentsWithOrdinalSql()}),
            numbered AS ({NumberedDocumentsSql()})
            INSERT INTO [edfi].[StudentSchoolAssociation] (
                [DocumentId],
                [SchoolId_Unified],
                [School_DocumentId],
                [Student_DocumentId],
                [Student_StudentUniqueId],
                [EntryGradeLevelDescriptor_DescriptorId],
                [EntryDate]
            )
            SELECT
                numbered.[DocumentId],
                CASE
                    WHEN {IsUnauthorizedSql("students.ordinal")} THEN @unreachableSchoolId
                    ELSE @reachableSchoolId
                END,
                CASE
                    WHEN {IsUnauthorizedSql("students.ordinal")}
                        THEN {SchoolDocumentIdSql("@unreachableSchoolId")}
                    ELSE {SchoolDocumentIdSql("@reachableSchoolId")}
                END,
                students.[DocumentId],
                students.[StudentUniqueId],
                {DescriptorDocumentIdSql("GradeLevelDescriptor", "@gradeLevelDescriptorUri")},
                CAST('2024-08-15' AS date)
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal;
            """,
            TotalRowsParameter(counts),
            StrideParameter(counts),
            StudentUniqueIdPrefixParameter(),
            ReachableSchoolIdParameter(),
            UnreachableSchoolIdParameter(),
            new SqlParameter(
                "@gradeLevelDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.GradeLevelDescriptorUri
            )
        );

        // StudentAcademicRecord: the CourseTranscript path's intermediate hop.
        await database.ExecuteNonQueryAsync(
            $"""
            {VolumeDocumentsSql("a2000000", "StudentAcademicRecord")}

            WITH students AS ({StudentsWithOrdinalSql()}),
            numbered AS ({NumberedDocumentsSql()})
            INSERT INTO [edfi].[StudentAcademicRecord] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [SchoolYear_DocumentId],
                [SchoolYear_SchoolYear],
                [Student_DocumentId],
                [Student_StudentUniqueId],
                [TermDescriptor_DescriptorId]
            )
            SELECT
                numbered.[DocumentId],
                {SchoolDocumentIdSql("@reachableSchoolId")},
                @reachableSchoolId,
                {SchoolYearDocumentIdSql()},
                @schoolYear,
                students.[DocumentId],
                students.[StudentUniqueId],
                {DescriptorDocumentIdSql("TermDescriptor", "@termDescriptorUri")}
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter(),
            ReachableSchoolIdParameter(),
            SchoolYearParameter(),
            new SqlParameter(
                "@termDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.TermDescriptorUri
            )
        );

        // StudentSectionAssociation: both a target root table and Grade's parent. Root rows for unauthorized
        // students still reference the single Section on the reachable school — authorization is decided by
        // [Student_DocumentId] against the view, and no constraint ties the two.
        await database.ExecuteNonQueryAsync(
            $"""
            {VolumeDocumentsSql("a3000000", "StudentSectionAssociation")}

            WITH students AS ({StudentsWithOrdinalSql()}),
            numbered AS ({NumberedDocumentsSql()})
            INSERT INTO [edfi].[StudentSectionAssociation] (
                [DocumentId],
                [Section_DocumentId],
                [Section_LocalCourseCode],
                [Section_SchoolId],
                [Section_SchoolYear],
                [Section_SessionName],
                [Section_SectionIdentifier],
                [Student_DocumentId],
                [Student_StudentUniqueId],
                [BeginDate]
            )
            SELECT
                numbered.[DocumentId],
                section.[DocumentId],
                section.[CourseOffering_LocalCourseCode],
                section.[SchoolId_Unified],
                section.[CourseOffering_SchoolYear],
                section.[CourseOffering_SessionName],
                section.[SectionIdentifier],
                students.[DocumentId],
                students.[StudentUniqueId],
                CAST('2024-08-20' AS date)
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal
            CROSS JOIN [edfi].[Section] section;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter()
        );

        // StudentSectionAttendanceEvent.
        await database.ExecuteNonQueryAsync(
            $"""
            {VolumeDocumentsSql("a4000000", "StudentSectionAttendanceEvent")}

            WITH students AS ({StudentsWithOrdinalSql()}),
            numbered AS ({NumberedDocumentsSql()})
            INSERT INTO [edfi].[StudentSectionAttendanceEvent] (
                [DocumentId],
                [Section_DocumentId],
                [Section_LocalCourseCode],
                [Section_SchoolId],
                [Section_SchoolYear],
                [Section_SessionName],
                [Section_SectionIdentifier],
                [Student_DocumentId],
                [Student_StudentUniqueId],
                [AttendanceEventCategoryDescriptor_DescriptorId],
                [EventDate]
            )
            SELECT
                numbered.[DocumentId],
                section.[DocumentId],
                section.[CourseOffering_LocalCourseCode],
                section.[SchoolId_Unified],
                section.[CourseOffering_SchoolYear],
                section.[CourseOffering_SessionName],
                section.[SectionIdentifier],
                students.[DocumentId],
                students.[StudentUniqueId],
                {DescriptorDocumentIdSql(
                "AttendanceEventCategoryDescriptor",
                "@attendanceEventCategoryDescriptorUri"
            )},
                CAST('2024-09-05' AS date)
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal
            CROSS JOIN [edfi].[Section] section;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter(),
            new SqlParameter(
                "@attendanceEventCategoryDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.AttendanceEventCategoryDescriptorUri
            )
        );

        // StudentGradebookEntry.
        await database.ExecuteNonQueryAsync(
            $"""
            {VolumeDocumentsSql("a5000000", "StudentGradebookEntry")}

            WITH students AS ({StudentsWithOrdinalSql()}),
            numbered AS ({NumberedDocumentsSql()})
            INSERT INTO [edfi].[StudentGradebookEntry] (
                [DocumentId],
                [GradebookEntry_DocumentId],
                [GradebookEntry_GradebookEntryIdentifier],
                [GradebookEntry_Namespace],
                [Student_DocumentId],
                [Student_StudentUniqueId]
            )
            SELECT
                numbered.[DocumentId],
                gradebookEntry.[DocumentId],
                gradebookEntry.[GradebookEntryIdentifier],
                gradebookEntry.[Namespace],
                students.[DocumentId],
                students.[StudentUniqueId]
            FROM numbered
            INNER JOIN students ON students.ordinal = numbered.ordinal
            CROSS JOIN [edfi].[GradebookEntry] gradebookEntry;
            """,
            TotalRowsParameter(counts),
            StudentUniqueIdPrefixParameter()
        );

        // Grade is built from StudentSectionAssociation rather than from a numbers CTE alone: its foreign key to
        // StudentSectionAssociation is the full reference key, so BeginDate, LocalCourseCode, SectionIdentifier,
        // SessionName and StudentUniqueId are copied from the row it points at.
        // [StudentSectionAssociation_SchoolId]/[_SchoolYear] and [GradingPeriodGradingPeriod_SchoolId]/
        // [_SchoolYear] are computed from [SchoolId_Unified]/[SchoolYear_Unified], which is why both parents must
        // sit on the same school and school year.
        await database.ExecuteNonQueryAsync(
            $"""
            {VolumeDocumentsSql("a6000000", "Grade")}

            WITH numbered AS ({NumberedDocumentsSql()}),
            associations AS (
                SELECT
                    ssa.*,
                    ROW_NUMBER() OVER (ORDER BY ssa.[DocumentId]) AS ordinal
                FROM [edfi].[StudentSectionAssociation] ssa
            )
            INSERT INTO [edfi].[Grade] (
                [DocumentId],
                [SchoolId_Unified],
                [SchoolYear_Unified],
                [GradingPeriodGradingPeriod_DocumentId],
                [GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId],
                [GradingPeriodGradingPeriod_GradingPeriodName],
                [StudentSectionAssociation_DocumentId],
                [StudentSectionAssociation_BeginDate],
                [StudentSectionAssociation_LocalCourseCode],
                [StudentSectionAssociation_SectionIdentifier],
                [StudentSectionAssociation_SessionName],
                [StudentSectionAssociation_StudentUniqueId],
                [GradeTypeDescriptor_DescriptorId]
            )
            SELECT
                numbered.[DocumentId],
                associations.[Section_SchoolId],
                associations.[Section_SchoolYear],
                gradingPeriod.[DocumentId],
                gradingPeriod.[GradingPeriodDescriptor_DescriptorId],
                gradingPeriod.[GradingPeriodName],
                associations.[DocumentId],
                associations.[BeginDate],
                associations.[Section_LocalCourseCode],
                associations.[Section_SectionIdentifier],
                associations.[Section_SessionName],
                associations.[Student_StudentUniqueId],
                {DescriptorDocumentIdSql("GradeTypeDescriptor", "@gradeTypeDescriptorUri")}
            FROM numbered
            INNER JOIN associations ON associations.ordinal = numbered.ordinal
            CROSS JOIN [edfi].[GradingPeriod] gradingPeriod;
            """,
            TotalRowsParameter(counts),
            new SqlParameter(
                "@gradeTypeDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.GradeTypeDescriptorUri
            )
        );

        // CourseTranscript relates to StudentAcademicRecord by its full reference key, so it is built from that
        // table for the same reason Grade is built from StudentSectionAssociation.
        await database.ExecuteNonQueryAsync(
            $"""
            {VolumeDocumentsSql("a7000000", "CourseTranscript")}

            WITH numbered AS ({NumberedDocumentsSql()}),
            records AS (
                SELECT
                    sar.*,
                    ROW_NUMBER() OVER (ORDER BY sar.[DocumentId]) AS ordinal
                FROM [edfi].[StudentAcademicRecord] sar
            )
            INSERT INTO [edfi].[CourseTranscript] (
                [DocumentId],
                [CourseCourse_DocumentId],
                [CourseCourse_CourseCode],
                [CourseCourse_EducationOrganizationId],
                [StudentAcademicRecord_DocumentId],
                [StudentAcademicRecord_EducationOrganizationId],
                [StudentAcademicRecord_SchoolYear],
                [StudentAcademicRecord_StudentUniqueId],
                [StudentAcademicRecord_TermDescriptor_DescriptorId],
                [CourseAttemptResultDescriptor_DescriptorId]
            )
            SELECT
                numbered.[DocumentId],
                course.[DocumentId],
                course.[CourseCode],
                course.[EducationOrganization_EducationOrganizationId],
                records.[DocumentId],
                records.[EducationOrganization_EducationOrganizationId],
                records.[SchoolYear_SchoolYear],
                records.[Student_StudentUniqueId],
                records.[TermDescriptor_DescriptorId],
                {DescriptorDocumentIdSql(
                "CourseAttemptResultDescriptor",
                "@courseAttemptResultDescriptorUri"
            )}
            FROM numbered
            INNER JOIN records ON records.ordinal = numbered.ordinal
            CROSS JOIN [edfi].[Course] course;
            """,
            TotalRowsParameter(counts),
            new SqlParameter(
                "@courseAttemptResultDescriptorUri",
                RelationshipAuthorizationVolumeIdentifiers.CourseAttemptResultDescriptorUri
            )
        );
    }

    private static async Task<RelationshipAuthorizationVolumeGenerationResult> ReadGenerationResultAsync(
        MssqlGeneratedDdlTestDatabase database,
        RelationshipAuthorizationVolumeCounts counts
    )
    {
        var authorizedStudentCount = await database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT_BIG(DISTINCT authView.[Student_DocumentId])
            FROM [auth].[EducationOrganizationIdToStudentDocumentId] authView
            WHERE authView.[SourceEducationOrganizationId] = @claimEducationOrganizationId;
            """,
            ClaimEducationOrganizationIdParameter()
        );

        var unauthorizedStudentCount = await database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM [edfi].[Student] student
            WHERE student.[StudentUniqueId] LIKE @studentUniqueIdPattern
              AND NOT EXISTS (
                  SELECT 1
                  FROM [auth].[EducationOrganizationIdToStudentDocumentId] authView
                  WHERE authView.[Student_DocumentId] = student.[DocumentId]
                    AND authView.[SourceEducationOrganizationId] = @claimEducationOrganizationId
              );
            """,
            ClaimEducationOrganizationIdParameter(),
            new SqlParameter(
                "@studentUniqueIdPattern",
                RelationshipAuthorizationVolumeIdentifiers.StudentUniqueIdPrefix + "%"
            )
        );

        return new RelationshipAuthorizationVolumeGenerationResult(
            counts,
            authorizedStudentCount,
            unauthorizedStudentCount
        );
    }

    /// <summary>
    /// SQL Server has no data-modifying CTE, so the documents land in a table variable first and the resource
    /// row set is built from it in a second statement of the same batch. <c>OUTPUT ... INTO</c> is what makes
    /// this legal on a table carrying triggers.
    /// </summary>
    private static string SingletonDocumentSql(string documentUuid, string resourceName) =>
        $"""
            DECLARE @docs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);

            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @docs ([DocumentId])
            VALUES (CONVERT(uniqueidentifier, '{documentUuid}'), {ResourceKeyIdSql(resourceName)});
            """;

    private static string VolumeDocumentsSql(string documentUuidPrefix, string resourceName) =>
        $"""
            DECLARE @docs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);

            WITH numbers AS (
                SELECT TOP (@totalRows)
                    ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [Number]
                FROM sys.all_objects AS firstSource
                CROSS JOIN sys.all_objects AS secondSource
            )
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @docs ([DocumentId])
            SELECT
                CONVERT(
                    uniqueidentifier,
                    '{documentUuidPrefix}-0000-4000-8000-'
                        + RIGHT('000000000000' + CAST(numbers.[Number] AS varchar(12)), 12)
                ),
                {ResourceKeyIdSql(resourceName)}
            FROM numbers;
            """;

    private static string NumberedDocumentsSql() =>
        "SELECT [DocumentId], ROW_NUMBER() OVER (ORDER BY [DocumentId]) AS ordinal FROM @docs";

    private static string StudentsWithOrdinalSql() =>
        $"SELECT student.[DocumentId], student.[StudentUniqueId], "
        + $"CAST(SUBSTRING(student.[StudentUniqueId], {RelationshipAuthorizationVolumeIdentifiers.StudentUniqueIdOrdinalOffset}, 8) AS bigint) AS ordinal "
        + "FROM [edfi].[Student] student "
        + "WHERE student.[StudentUniqueId] LIKE @studentUniqueIdPrefix + '%'";

    private static string IsUnauthorizedSql(string ordinalExpression) => $"{ordinalExpression} % @stride = 0";

    private static string ResourceKeyIdSql(string resourceName) =>
        $"(SELECT [ResourceKeyId] FROM [dms].[ResourceKey] WHERE [ProjectName] = 'Ed-Fi' AND [ResourceName] = '{resourceName}')";

    private static string SchoolDocumentIdSql(string schoolIdExpression) =>
        $"(SELECT school.[DocumentId] FROM [edfi].[School] school WHERE school.[SchoolId] = {schoolIdExpression})";

    private static string SchoolYearDocumentIdSql() =>
        "(SELECT schoolYear.[DocumentId] FROM [edfi].[SchoolYearType] schoolYear WHERE schoolYear.[SchoolYear] = @schoolYear)";

    private static string DescriptorDocumentIdSql(string resourceName, string uriParameter) =>
        $"(SELECT descriptor.[DocumentId] FROM [dms].[Descriptor] descriptor "
        + "INNER JOIN [dms].[Document] document ON document.[DocumentId] = descriptor.[DocumentId] "
        + $"WHERE document.[ResourceKeyId] = {ResourceKeyIdSql(resourceName)} AND descriptor.[Uri] = {uriParameter})";

    private static SqlParameter TotalRowsParameter(RelationshipAuthorizationVolumeCounts counts) =>
        new("@totalRows", counts.TotalRowsPerRoot);

    private static SqlParameter StrideParameter(RelationshipAuthorizationVolumeCounts counts) =>
        new("@stride", (long)counts.Stride);

    private static SqlParameter StudentUniqueIdPrefixParameter() =>
        new("@studentUniqueIdPrefix", RelationshipAuthorizationVolumeIdentifiers.StudentUniqueIdPrefix);

    private static SqlParameter ReachableSchoolIdParameter() =>
        new("@reachableSchoolId", (long)RelationshipAuthorizationVolumeIdentifiers.ReachableSchoolId);

    private static SqlParameter UnreachableSchoolIdParameter() =>
        new("@unreachableSchoolId", (long)RelationshipAuthorizationVolumeIdentifiers.UnreachableSchoolId);

    private static SqlParameter SchoolYearParameter() =>
        new("@schoolYear", RelationshipAuthorizationVolumeIdentifiers.SchoolYear);

    private static SqlParameter ClaimEducationOrganizationIdParameter() =>
        new(
            "@claimEducationOrganizationId",
            RelationshipAuthorizationVolumeIdentifiers.ClaimEducationOrganizationId
        );
}
