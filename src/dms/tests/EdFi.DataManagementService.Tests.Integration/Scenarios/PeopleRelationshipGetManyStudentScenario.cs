// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.PeopleRelationshipGetManyScenarioHelpers;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class PeopleRelationshipGetManyStudentScenario
{
    public const long ClaimEducationOrganizationId = 900;

    private const long AuthorizedSchoolId = 100;
    private const long UnauthorizedSchoolId = 300;
    private const int SchoolYear = 2026;

    private const string AuthorizedStudentUniqueId = "student-auth-001";
    private const string UnauthorizedStudentUniqueId = "student-denied-001";
    private const string BrokenStudentUniqueId = "student-broken-001";

    private const string EdFiProjectName = "Ed-Fi";
    private const string AuthzProjectName = "Authz";
    private const string StudentResourceName = "Student";
    private const string StudentSchoolAssociationResourceName = "StudentSchoolAssociation";
    private const string AuthorizationStudentAcademicRecordResourceName =
        "AuthorizationStudentAcademicRecordResource";

    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StudentSchoolAssociationsEndpoint = "/data/ed-fi/studentSchoolAssociations";
    private const string StudentEducationOrganizationResponsibilityAssociationsEndpoint =
        "/data/ed-fi/studentEducationOrganizationResponsibilityAssociations";
    private const string SchoolYearTypesEndpoint = "/data/ed-fi/schoolYearTypes";
    private const string StudentAcademicRecordsEndpoint = "/data/ed-fi/studentAcademicRecords";
    private const string AuthorizationStudentAcademicRecordResourcesEndpoint =
        "/data/authz/authorizationStudentAcademicRecordResources";

    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string TermDescriptorsEndpoint = "/data/ed-fi/termDescriptors";
    private const string ResponsibilityDescriptorsEndpoint = "/data/ed-fi/responsibilityDescriptors";

    private const string EducationOrganizationCategoryNamespace =
        "uri://ed-fi.org/EducationOrganizationCategoryDescriptor";
    private const string GradeLevelNamespace = "uri://ed-fi.org/GradeLevelDescriptor";
    private const string TermNamespace = "uri://ed-fi.org/TermDescriptor";
    private const string ResponsibilityNamespace = "uri://ed-fi.org/ResponsibilityDescriptor";

    private const string SchoolCategoryDescriptor = $"{EducationOrganizationCategoryNamespace}#School";
    private const string GradeLevelDescriptor = $"{GradeLevelNamespace}#Tenth grade";
    private const string TermDescriptor = $"{TermNamespace}#Fall Semester";
    private const string ResponsibilityDescriptor = $"{ResponsibilityNamespace}#Accountability";

    public static IClaimSetProvider CreateStudentsOnlyReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [
                new RelationshipReadResource(EdFiProjectName, StudentResourceName),
                new RelationshipReadResource(EdFiProjectName, StudentSchoolAssociationResourceName),
            ],
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

    public static IClaimSetProvider CreateStudentsOnlyThroughResponsibilityReadClaimSetProvider(
        FixtureContext fixture
    ) =>
        CreateClaimSetProvider(
            fixture,
            [new RelationshipReadResource(EdFiProjectName, StudentResourceName)],
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility
        );

    public static IClaimSetProvider CreateTransitiveStudentReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [new RelationshipReadResource(AuthzProjectName, AuthorizationStudentAcademicRecordResourceName)],
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

    public static async Task It_filters_student_self_and_direct_student_get_many(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentSchoolAssociationDataAsync(harness);

        JsonArray students = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{StudentsEndpoint}?totalCount=true",
            1
        );
        ExtractValues(students, static item => item["studentUniqueId"]!.GetValue<string>())
            .Should()
            .Equal(AuthorizedStudentUniqueId);

        JsonArray studentSchoolAssociations = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{StudentSchoolAssociationsEndpoint}?totalCount=true",
            1
        );
        ExtractValues(
                studentSchoolAssociations,
                static item => item["studentReference"]!["studentUniqueId"]!.GetValue<string>()
            )
            .Should()
            .Equal(AuthorizedStudentUniqueId);
    }

    public static async Task It_filters_students_only_through_responsibility_get_many(
        ApiIntegrationHarness harness
    )
    {
        await SeedThroughResponsibilityDataAsync(harness);

        JsonArray students = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{StudentsEndpoint}?totalCount=true",
            1
        );
        ExtractValues(students, static item => item["studentUniqueId"]!.GetValue<string>())
            .Should()
            .Equal(AuthorizedStudentUniqueId);
    }

    public static async Task It_filters_transitive_student_academic_record_get_many(
        ApiIntegrationHarness harness
    )
    {
        await SeedTransitiveStudentAcademicRecordDataAsync(harness);
        await InsertBrokenStudentAcademicRecordPathAsync(harness);

        JsonArray resources = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{AuthorizationStudentAcademicRecordResourcesEndpoint}?totalCount=true",
            1
        );
        ExtractValues(
                resources,
                static item => item["studentAcademicRecordReference"]!["studentUniqueId"]!.GetValue<string>()
            )
            .Should()
            .Equal(AuthorizedStudentUniqueId);
    }

    private static async Task SeedStudentSchoolAssociationDataAsync(ApiIntegrationHarness harness)
    {
        await SeedCommonReferenceDataAsync(harness);
        await CreateStudentAsync(harness, StudentsEndpoint, AuthorizedStudentUniqueId, "Auth");
        await CreateStudentAsync(harness, StudentsEndpoint, UnauthorizedStudentUniqueId, "Denied");
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            AuthorizedStudentUniqueId,
            AuthorizedSchoolId,
            GradeLevelDescriptor
        );
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            UnauthorizedStudentUniqueId,
            UnauthorizedSchoolId,
            GradeLevelDescriptor
        );
    }

    private static async Task SeedThroughResponsibilityDataAsync(ApiIntegrationHarness harness)
    {
        await SeedCommonReferenceDataAsync(harness);
        await CreateStudentAsync(harness, StudentsEndpoint, AuthorizedStudentUniqueId, "Auth");
        await CreateStudentAsync(harness, StudentsEndpoint, UnauthorizedStudentUniqueId, "Denied");
        await CreateStudentEducationOrganizationResponsibilityAssociationAsync(
            harness,
            AuthorizedStudentUniqueId,
            AuthorizedSchoolId
        );
        await CreateStudentEducationOrganizationResponsibilityAssociationAsync(
            harness,
            UnauthorizedStudentUniqueId,
            UnauthorizedSchoolId
        );
    }

    private static async Task SeedTransitiveStudentAcademicRecordDataAsync(ApiIntegrationHarness harness)
    {
        await SeedStudentSchoolAssociationDataAsync(harness);
        await CreateSchoolYearTypeAsync(harness, SchoolYearTypesEndpoint, SchoolYear);
        await CreateStudentAcademicRecordAsync(
            harness,
            StudentAcademicRecordsEndpoint,
            AuthorizedStudentUniqueId,
            AuthorizedSchoolId,
            SchoolYear,
            TermDescriptor
        );
        await CreateStudentAcademicRecordAsync(
            harness,
            StudentAcademicRecordsEndpoint,
            UnauthorizedStudentUniqueId,
            UnauthorizedSchoolId,
            SchoolYear,
            TermDescriptor
        );
        await InsertAuthorizationStudentAcademicRecordResourceAsync(
            harness,
            1,
            "authorized transitive resource",
            AuthorizedStudentUniqueId,
            AuthorizedSchoolId
        );
        await InsertAuthorizationStudentAcademicRecordResourceAsync(
            harness,
            2,
            "unauthorized transitive resource",
            UnauthorizedStudentUniqueId,
            UnauthorizedSchoolId
        );
    }

    private static async Task SeedCommonReferenceDataAsync(ApiIntegrationHarness harness)
    {
        await CreateDescriptorAsync(
            harness,
            EducationOrganizationCategoryDescriptorsEndpoint,
            EducationOrganizationCategoryNamespace,
            "School"
        );
        await CreateDescriptorAsync(
            harness,
            GradeLevelDescriptorsEndpoint,
            GradeLevelNamespace,
            "Tenth grade"
        );
        await CreateDescriptorAsync(harness, TermDescriptorsEndpoint, TermNamespace, "Fall Semester");
        await CreateDescriptorAsync(
            harness,
            ResponsibilityDescriptorsEndpoint,
            ResponsibilityNamespace,
            "Accountability"
        );
        await CreateSchoolAsync(
            harness,
            SchoolsEndpoint,
            AuthorizedSchoolId,
            "Authorized School",
            SchoolCategoryDescriptor,
            GradeLevelDescriptor
        );
        await CreateSchoolAsync(
            harness,
            SchoolsEndpoint,
            UnauthorizedSchoolId,
            "Unauthorized School",
            SchoolCategoryDescriptor,
            GradeLevelDescriptor
        );
        await InsertAuthEdgeAsync(harness, ClaimEducationOrganizationId, AuthorizedSchoolId);
        await DeleteAuthEdgeAsync(harness, ClaimEducationOrganizationId, UnauthorizedSchoolId);
        await DeleteAuthEdgeAsync(harness, ClaimEducationOrganizationId, ClaimEducationOrganizationId);
    }

    private static async Task CreateStudentEducationOrganizationResponsibilityAssociationAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        long educationOrganizationId
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            StudentEducationOrganizationResponsibilityAssociationsEndpoint,
            new JsonObject
            {
                ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["educationOrganizationId"] = educationOrganizationId,
                },
                ["responsibilityDescriptor"] = ResponsibilityDescriptor,
                ["beginDate"] = "2025-08-01",
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task InsertAuthorizationStudentAcademicRecordResourceAsync(
        ApiIntegrationHarness harness,
        int authorizationStudentAcademicRecordId,
        string name,
        string studentUniqueId,
        long educationOrganizationId
    )
    {
        long studentAcademicRecordDocumentId = await ReadStudentAcademicRecordDocumentIdAsync(
            harness,
            studentUniqueId,
            educationOrganizationId
        );

        await InsertAuthorizationStudentAcademicRecordResourceRowAsync(
            harness,
            authorizationStudentAcademicRecordId,
            name,
            studentUniqueId,
            educationOrganizationId,
            studentAcademicRecordDocumentId
        );
    }

    private static async Task InsertBrokenStudentAcademicRecordPathAsync(ApiIntegrationHarness harness)
    {
        await InsertAuthorizationStudentAcademicRecordResourceRowAsync(
            harness,
            3,
            "broken transitive resource",
            BrokenStudentUniqueId,
            AuthorizedSchoolId,
            studentAcademicRecordDocumentId: null
        );
    }

    private static async Task InsertAuthorizationStudentAcademicRecordResourceRowAsync(
        ApiIntegrationHarness harness,
        int authorizationStudentAcademicRecordId,
        string name,
        string studentUniqueId,
        long studentAcademicRecordEducationOrganizationId,
        long? studentAcademicRecordDocumentId
    )
    {
        long termDescriptorId = await ReadDescriptorIdAsync(harness, TermDescriptor);
        short resourceKeyId = Convert.ToInt16(
            await ReadInt64Async(
                harness.DbConnection,
                """
                SELECT "ResourceKeyId"
                FROM "dms"."ResourceKey"
                WHERE "ProjectName" = @projectName
                  AND "ResourceName" = @resourceName
                """,
                ("@projectName", AuthzProjectName),
                ("@resourceName", AuthorizationStudentAcademicRecordResourceName)
            ),
            CultureInfo.InvariantCulture
        );

        long documentId = await InsertDocumentAsync(harness.DbConnection, resourceKeyId);
        long resolvedStudentAcademicRecordDocumentId =
            studentAcademicRecordDocumentId ?? documentId + 100_000;

        if (studentAcademicRecordDocumentId is null)
        {
            await DisableAuthorizationStudentAcademicRecordForeignKeysAsync(harness.DbConnection);
        }

        if (IsMssql(harness.DbConnection))
        {
            await ExecuteNonQueryAsync(
                harness.DbConnection,
                """
                INSERT INTO [authz].[AuthorizationStudentAcademicRecordResource] (
                    [DocumentId],
                    [StudentAcademicRecord_DocumentId],
                    [StudentAcademicRecord_EducationOrganizationId],
                    [StudentAcademicRecord_SchoolYear],
                    [StudentAcademicRecord_StudentUniqueId],
                    [StudentAcademicRecord_TermDescriptor_DescriptorId],
                    [AuthorizationStudentAcademicRecordId],
                    [Name]
                )
                VALUES (
                    @documentId,
                    @studentAcademicRecordDocumentId,
                    @studentAcademicRecordEducationOrganizationId,
                    @schoolYear,
                    @studentUniqueId,
                    @termDescriptorId,
                    @authorizationStudentAcademicRecordId,
                    @name
                );
                """,
                ("@documentId", documentId),
                ("@studentAcademicRecordDocumentId", resolvedStudentAcademicRecordDocumentId),
                (
                    "@studentAcademicRecordEducationOrganizationId",
                    studentAcademicRecordEducationOrganizationId
                ),
                ("@schoolYear", SchoolYear),
                ("@studentUniqueId", studentUniqueId),
                ("@termDescriptorId", termDescriptorId),
                ("@authorizationStudentAcademicRecordId", authorizationStudentAcademicRecordId),
                ("@name", name)
            );
            return;
        }

        await ExecuteNonQueryAsync(
            harness.DbConnection,
            """
            INSERT INTO "authz"."AuthorizationStudentAcademicRecordResource" (
                "DocumentId",
                "StudentAcademicRecord_DocumentId",
                "StudentAcademicRecord_EducationOrganizationId",
                "StudentAcademicRecord_SchoolYear",
                "StudentAcademicRecord_StudentUniqueId",
                "StudentAcademicRecord_TermDescriptor_DescriptorId",
                "AuthorizationStudentAcademicRecordId",
                "Name"
            )
            VALUES (
                @documentId,
                @studentAcademicRecordDocumentId,
                @studentAcademicRecordEducationOrganizationId,
                @schoolYear,
                @studentUniqueId,
                @termDescriptorId,
                @authorizationStudentAcademicRecordId,
                @name
            );
            """,
            ("@documentId", documentId),
            ("@studentAcademicRecordDocumentId", resolvedStudentAcademicRecordDocumentId),
            ("@studentAcademicRecordEducationOrganizationId", studentAcademicRecordEducationOrganizationId),
            ("@schoolYear", SchoolYear),
            ("@studentUniqueId", studentUniqueId),
            ("@termDescriptorId", termDescriptorId),
            ("@authorizationStudentAcademicRecordId", authorizationStudentAcademicRecordId),
            ("@name", name)
        );
    }

    private static async Task DisableAuthorizationStudentAcademicRecordForeignKeysAsync(
        DbConnection connection
    )
    {
        string sql = IsMssql(connection)
            ? """
                DECLARE @sql nvarchar(max) = N'';

                SELECT @sql = @sql + N'ALTER TABLE [authz].[AuthorizationStudentAcademicRecordResource] NOCHECK CONSTRAINT '
                    + QUOTENAME([name]) + N';'
                FROM [sys].[foreign_keys]
                WHERE [parent_object_id] = OBJECT_ID(N'[authz].[AuthorizationStudentAcademicRecordResource]');

                EXEC sp_executesql @sql;
                """
            : """
                DO $$
                DECLARE fk record;
                BEGIN
                    FOR fk IN
                        SELECT conname
                        FROM pg_constraint
                        WHERE conrelid = to_regclass('"authz"."AuthorizationStudentAcademicRecordResource"')
                          AND contype = 'f'
                    LOOP
                        EXECUTE format(
                            'ALTER TABLE "authz"."AuthorizationStudentAcademicRecordResource" DROP CONSTRAINT %I',
                            fk.conname
                        );
                    END LOOP;
                END $$;
                """;

        await ExecuteNonQueryAsync(connection, sql);
    }

    private static async Task<long> ReadStudentAcademicRecordDocumentIdAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        long educationOrganizationId
    ) =>
        await ReadInt64Async(
            harness.DbConnection,
            """
            SELECT "DocumentId"
            FROM "edfi"."StudentAcademicRecord"
            WHERE "Student_StudentUniqueId" = @studentUniqueId
              AND "EducationOrganization_EducationOrganizationId" = @educationOrganizationId
            """,
            ("@studentUniqueId", studentUniqueId),
            ("@educationOrganizationId", educationOrganizationId)
        );

    private static async Task<long> ReadDescriptorIdAsync(ApiIntegrationHarness harness, string uri) =>
        await ReadInt64Async(
            harness.DbConnection,
            """
            SELECT "DocumentId"
            FROM "dms"."Descriptor"
            WHERE "Uri" = @uri
            """,
            ("@uri", uri)
        );

    private static async Task<long> InsertDocumentAsync(DbConnection connection, short resourceKeyId)
    {
        string sql = IsMssql(connection)
            ? """
                INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
                OUTPUT INSERTED.[DocumentId]
                VALUES (@documentUuid, @resourceKeyId);
                """
            : """
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                VALUES (@documentUuid, @resourceKeyId)
                RETURNING "DocumentId";
                """;

        return await ReadInt64Async(
            connection,
            sql,
            ("@documentUuid", Guid.NewGuid()),
            ("@resourceKeyId", resourceKeyId)
        );
    }
}
