// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.PeopleRelationshipGetManyScenarioHelpers;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class PeopleRelationshipGetManyCompositionScenario
{
    public const long ClaimEducationOrganizationId = 950;

    private const long AuthorizedSchoolId = 150;
    private const long UnauthorizedSchoolId = 350;
    private const int SchoolYear = 2026;

    private const string BothAuthorizedStudentUniqueId = "composition-both-001";
    private const string EdOrgOnlyStudentUniqueId = "composition-edorg-only-001";
    private const string PeopleOnlyStudentUniqueId = "composition-people-only-001";
    private const string NeitherAuthorizedStudentUniqueId = "composition-neither-001";

    private const string PageAuthorizedStudentUniqueId1 = "page-auth-001";
    private const string PageUnauthorizedStudentUniqueId = "page-denied-001";
    private const string PageAuthorizedStudentUniqueId2 = "page-auth-002";
    private const string PageAuthorizedStudentUniqueId3 = "page-auth-003";
    private const string PageAuthorizedStudentUniqueId4 = "page-auth-004";

    private const string EdFiProjectName = "Ed-Fi";
    private const string StudentResourceName = "Student";
    private const string StudentAcademicRecordResourceName = "StudentAcademicRecord";

    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StudentSchoolAssociationsEndpoint = "/data/ed-fi/studentSchoolAssociations";
    private const string SchoolYearTypesEndpoint = "/data/ed-fi/schoolYearTypes";
    private const string StudentAcademicRecordsEndpoint = "/data/ed-fi/studentAcademicRecords";

    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string TermDescriptorsEndpoint = "/data/ed-fi/termDescriptors";

    private const string EducationOrganizationCategoryNamespace =
        "uri://ed-fi.org/EducationOrganizationCategoryDescriptor";
    private const string GradeLevelNamespace = "uri://ed-fi.org/GradeLevelDescriptor";
    private const string TermNamespace = "uri://ed-fi.org/TermDescriptor";

    private const string SchoolCategoryDescriptor = $"{EducationOrganizationCategoryNamespace}#School";
    private const string GradeLevelDescriptor = $"{GradeLevelNamespace}#Tenth grade";
    private const string TermDescriptor = $"{TermNamespace}#Fall Semester";

    public static IClaimSetProvider CreateEdOrgAndPeopleReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [new RelationshipReadResource(EdFiProjectName, StudentAcademicRecordResourceName)],
            [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople]
        );

    public static IClaimSetProvider CreateEdOrgOrPeopleReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [new RelationshipReadResource(EdFiProjectName, StudentAcademicRecordResourceName)],
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            ]
        );

    public static IClaimSetProvider CreateStudentsOnlyReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [new RelationshipReadResource(EdFiProjectName, StudentResourceName)],
            [AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly]
        );

    public static IReadOnlyList<long> CreateUniqueClaimEducationOrganizationIds(int uniqueCount)
    {
        if (uniqueCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(uniqueCount));
        }

        return
        [
            ClaimEducationOrganizationId,
            .. Enumerable.Range(0, uniqueCount - 1).Select(static index => 1000L + index),
        ];
    }

    public static IReadOnlyList<long> CreateDuplicateHeavyClaimEducationOrganizationIds()
    {
        var uniqueClaimIds = CreateUniqueClaimEducationOrganizationIds(1999);

        return [.. uniqueClaimIds, .. uniqueClaimIds.Take(50), .. uniqueClaimIds.Take(50)];
    }

    public static async Task It_ands_edorg_and_people_subjects_within_one_strategy(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentAcademicRecordCompositionDataAsync(harness);

        JsonArray records = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{StudentAcademicRecordsEndpoint}?totalCount=true",
            1
        );

        ExtractValues(records, CreateStudentAcademicRecordKey)
            .Should()
            .Equal(CreateStudentAcademicRecordKey(BothAuthorizedStudentUniqueId, AuthorizedSchoolId));
    }

    public static async Task It_ors_edorg_and_people_strategies_without_duplicate_documents(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentAcademicRecordCompositionDataAsync(harness);

        JsonArray records = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{StudentAcademicRecordsEndpoint}?totalCount=true",
            3
        );

        ExtractValues(records, CreateStudentAcademicRecordKey)
            .Should()
            .BeEquivalentTo(
                CreateStudentAcademicRecordKey(BothAuthorizedStudentUniqueId, AuthorizedSchoolId),
                CreateStudentAcademicRecordKey(EdOrgOnlyStudentUniqueId, AuthorizedSchoolId),
                CreateStudentAcademicRecordKey(PeopleOnlyStudentUniqueId, UnauthorizedSchoolId)
            );

        records.Should().HaveCount(3);
    }

    public static async Task It_applies_authorization_before_paging_and_total_count(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentPaginationDataAsync(harness);

        JsonArray students = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{StudentsEndpoint}?offset=1&limit=2&totalCount=true",
            4
        );

        ExtractValues(students, static item => item["studentUniqueId"]!.GetValue<string>())
            .Should()
            .Equal(PageAuthorizedStudentUniqueId2, PageAuthorizedStudentUniqueId3);
    }

    public static async Task It_uses_expanded_scalar_parameters_for_1999_unique_claim_edorg_ids(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentParameterizationDataAsync(harness);

        JsonArray students = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{StudentsEndpoint}?totalCount=true",
            1
        );
        ExtractValues(students, static item => item["studentUniqueId"]!.GetValue<string>())
            .Should()
            .Equal(BothAuthorizedStudentUniqueId);

        PageKeysetSpec.Query keyset = GetRecordedQueryKeyset(harness);
        AssertScalarFilterParameters(keyset, 1999, ClaimEducationOrganizationId, 2997L);
    }

    public static async Task It_uses_a_structured_tvp_for_2000_unique_claim_edorg_ids(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentParameterizationDataAsync(harness);

        JsonArray students = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{StudentsEndpoint}?totalCount=true",
            1
        );
        ExtractValues(students, static item => item["studentUniqueId"]!.GetValue<string>())
            .Should()
            .Equal(BothAuthorizedStudentUniqueId);

        PageKeysetSpec.Query keyset = GetRecordedQueryKeyset(harness);
        AssertStructuredFilterParameter(keyset, 2000, ClaimEducationOrganizationId, 2998L);
    }

    public static async Task It_deduplicates_claim_edorg_ids_before_selecting_threshold(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentParameterizationDataAsync(harness);

        JsonArray students = await GetJsonArrayWithTotalCountAsync(
            harness,
            $"{StudentsEndpoint}?totalCount=true",
            1
        );
        ExtractValues(students, static item => item["studentUniqueId"]!.GetValue<string>())
            .Should()
            .Equal(BothAuthorizedStudentUniqueId);

        PageKeysetSpec.Query keyset = GetRecordedQueryKeyset(harness);
        AssertScalarFilterParameters(keyset, 1999, ClaimEducationOrganizationId, 2997L);
        keyset
            .ParameterValues.ContainsKey(
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
            .Should()
            .BeFalse();
    }

    private static async Task SeedStudentAcademicRecordCompositionDataAsync(ApiIntegrationHarness harness)
    {
        await SeedCommonReferenceDataAsync(harness);
        await CreateSchoolYearTypeAsync(harness, SchoolYearTypesEndpoint, SchoolYear);

        await CreateStudentAsync(harness, StudentsEndpoint, BothAuthorizedStudentUniqueId, "BothAuthorized");
        await CreateStudentAsync(harness, StudentsEndpoint, EdOrgOnlyStudentUniqueId, "EdOrgOnly");
        await CreateStudentAsync(harness, StudentsEndpoint, PeopleOnlyStudentUniqueId, "PeopleOnly");
        await CreateStudentAsync(harness, StudentsEndpoint, NeitherAuthorizedStudentUniqueId, "Neither");

        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            BothAuthorizedStudentUniqueId,
            AuthorizedSchoolId,
            GradeLevelDescriptor
        );
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            EdOrgOnlyStudentUniqueId,
            UnauthorizedSchoolId,
            GradeLevelDescriptor
        );
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            PeopleOnlyStudentUniqueId,
            AuthorizedSchoolId,
            GradeLevelDescriptor
        );
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            NeitherAuthorizedStudentUniqueId,
            UnauthorizedSchoolId,
            GradeLevelDescriptor
        );

        await CreateStudentAcademicRecordAsync(
            harness,
            StudentAcademicRecordsEndpoint,
            BothAuthorizedStudentUniqueId,
            AuthorizedSchoolId,
            SchoolYear,
            TermDescriptor
        );
        await CreateStudentAcademicRecordAsync(
            harness,
            StudentAcademicRecordsEndpoint,
            EdOrgOnlyStudentUniqueId,
            AuthorizedSchoolId,
            SchoolYear,
            TermDescriptor
        );
        await CreateStudentAcademicRecordAsync(
            harness,
            StudentAcademicRecordsEndpoint,
            PeopleOnlyStudentUniqueId,
            UnauthorizedSchoolId,
            SchoolYear,
            TermDescriptor
        );
        await CreateStudentAcademicRecordAsync(
            harness,
            StudentAcademicRecordsEndpoint,
            NeitherAuthorizedStudentUniqueId,
            UnauthorizedSchoolId,
            SchoolYear,
            TermDescriptor
        );
    }

    private static async Task SeedStudentPaginationDataAsync(ApiIntegrationHarness harness)
    {
        await SeedCommonReferenceDataAsync(harness);

        await CreateStudentAsync(harness, StudentsEndpoint, PageAuthorizedStudentUniqueId1, "PageAuthOne");
        await CreateStudentAsync(harness, StudentsEndpoint, PageUnauthorizedStudentUniqueId, "PageDenied");
        await CreateStudentAsync(harness, StudentsEndpoint, PageAuthorizedStudentUniqueId2, "PageAuthTwo");
        await CreateStudentAsync(harness, StudentsEndpoint, PageAuthorizedStudentUniqueId3, "PageAuthThree");
        await CreateStudentAsync(harness, StudentsEndpoint, PageAuthorizedStudentUniqueId4, "PageAuthFour");

        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            PageAuthorizedStudentUniqueId1,
            AuthorizedSchoolId,
            GradeLevelDescriptor
        );
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            PageUnauthorizedStudentUniqueId,
            UnauthorizedSchoolId,
            GradeLevelDescriptor
        );
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            PageAuthorizedStudentUniqueId2,
            AuthorizedSchoolId,
            GradeLevelDescriptor
        );
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            PageAuthorizedStudentUniqueId3,
            AuthorizedSchoolId,
            GradeLevelDescriptor
        );
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            PageAuthorizedStudentUniqueId4,
            AuthorizedSchoolId,
            GradeLevelDescriptor
        );
    }

    private static async Task SeedStudentParameterizationDataAsync(ApiIntegrationHarness harness)
    {
        await SeedCommonReferenceDataAsync(harness);

        await CreateStudentAsync(harness, StudentsEndpoint, BothAuthorizedStudentUniqueId, "ParamAuth");
        await CreateStudentAsync(harness, StudentsEndpoint, NeitherAuthorizedStudentUniqueId, "ParamDenied");
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            BothAuthorizedStudentUniqueId,
            AuthorizedSchoolId,
            GradeLevelDescriptor
        );
        await CreateStudentSchoolAssociationAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            NeitherAuthorizedStudentUniqueId,
            UnauthorizedSchoolId,
            GradeLevelDescriptor
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
        await CreateSchoolAsync(
            harness,
            SchoolsEndpoint,
            AuthorizedSchoolId,
            "Authorized Composition School",
            SchoolCategoryDescriptor,
            GradeLevelDescriptor
        );
        await CreateSchoolAsync(
            harness,
            SchoolsEndpoint,
            UnauthorizedSchoolId,
            "Unauthorized Composition School",
            SchoolCategoryDescriptor,
            GradeLevelDescriptor
        );
        await InsertAuthEdgeAsync(harness, ClaimEducationOrganizationId, AuthorizedSchoolId);
    }

    private static string CreateStudentAcademicRecordKey(JsonObject item) =>
        CreateStudentAcademicRecordKey(
            item["studentReference"]!["studentUniqueId"]!.GetValue<string>(),
            item["educationOrganizationReference"]!["educationOrganizationId"]!.GetValue<long>()
        );

    private static string CreateStudentAcademicRecordKey(
        string studentUniqueId,
        long educationOrganizationId
    ) => $"{studentUniqueId}:{educationOrganizationId}";

    private static PageKeysetSpec.Query GetRecordedQueryKeyset(ApiIntegrationHarness harness)
    {
        harness.QueryRecorder.Should().NotBeNull("this scenario requires CaptureQueryPlans");

        return harness.QueryRecorder!.AssertSingleQueryHydration();
    }

    private static void AssertScalarFilterParameters(
        PageKeysetSpec.Query keyset,
        int expectedCount,
        long expectedFirstValue,
        long expectedLastValue
    )
    {
        var pageFilterParameters = GetPageFilterParameters(keyset);
        pageFilterParameters.Should().HaveCount(expectedCount);
        pageFilterParameters[0].ParameterName.Should().Be("ClaimEducationOrganizationIds_0");
        pageFilterParameters[^1]
            .ParameterName.Should()
            .Be($"ClaimEducationOrganizationIds_{expectedCount - 1}");
        pageFilterParameters
            .Select(static parameter => parameter.Binding.Kind)
            .Should()
            .OnlyContain(static kind => kind == QuerySqlParameterBindingKind.Scalar);

        var totalCountFilterParameters = GetTotalCountFilterParameters(keyset);
        totalCountFilterParameters.Should().HaveCount(expectedCount);
        totalCountFilterParameters
            .Select(static parameter => parameter.Binding.Kind)
            .Should()
            .OnlyContain(static kind => kind == QuerySqlParameterBindingKind.Scalar);

        keyset.ParameterValues["ClaimEducationOrganizationIds_0"].Should().Be(expectedFirstValue);
        keyset
            .ParameterValues[$"ClaimEducationOrganizationIds_{expectedCount - 1}"]
            .Should()
            .Be(expectedLastValue);
    }

    private static void AssertStructuredFilterParameter(
        PageKeysetSpec.Query keyset,
        int expectedCount,
        long expectedFirstValue,
        long expectedLastValue
    )
    {
        var pageFilterParameters = GetPageFilterParameters(keyset);
        pageFilterParameters.Should().ContainSingle();
        pageFilterParameters[0]
            .ParameterName.Should()
            .Be(RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds);
        pageFilterParameters[0].Binding.Kind.Should().Be(QuerySqlParameterBindingKind.MssqlStructured);
        pageFilterParameters[0].Binding.StructuredTypeName.Should().Be("dms.BigIntTable");
        pageFilterParameters[0].Binding.StructuredColumnName.Should().Be("Id");

        var totalCountFilterParameters = GetTotalCountFilterParameters(keyset);
        totalCountFilterParameters.Should().ContainSingle();
        totalCountFilterParameters[0].Binding.Kind.Should().Be(QuerySqlParameterBindingKind.MssqlStructured);

        keyset.Plan.PageDocumentIdSql.Should().Contain("SELECT [Id] FROM @ClaimEducationOrganizationIds");
        keyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .HaveCount(expectedCount)
            .And.StartWith(expectedFirstValue)
            .And.EndWith(expectedLastValue);
    }

    private static IReadOnlyList<QuerySqlParameter> GetPageFilterParameters(PageKeysetSpec.Query keyset) =>
        [
            .. keyset.Plan.PageParametersInOrder.Where(static parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
            ),
        ];

    private static IReadOnlyList<QuerySqlParameter> GetTotalCountFilterParameters(PageKeysetSpec.Query keyset)
    {
        keyset.Plan.TotalCountParametersInOrder.Should().NotBeNull();

        return
        [
            .. keyset.Plan.TotalCountParametersInOrder!.Value.Where(static parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
            ),
        ];
    }
}
