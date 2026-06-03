// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.PeopleRelationshipGetManyScenarioHelpers;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class PeopleRelationshipCrudProblemDetailsScenario
{
    public const long ClaimEducationOrganizationId = 900;

    private const long AuthorizedSchoolId = 100;
    private const long UnauthorizedSchoolId = 300;

    private const string AuthorizedStudentUniqueId = "problem-student-auth-001";
    private const string UnauthorizedStudentUniqueId = "problem-student-denied-001";

    private const string AuthzProjectName = "Authz";
    private const string AuthorizationStudentSchoolResourceName = "AuthorizationStudentSchoolResource";

    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StudentSchoolAssociationsEndpoint = "/data/ed-fi/studentSchoolAssociations";
    private const string AuthorizationStudentSchoolResourcesEndpoint =
        "/data/authz/authorizationStudentSchoolResources";

    private const string EducationOrganizationCategoryNamespace =
        "uri://ed-fi.org/EducationOrganizationCategoryDescriptor";
    private const string GradeLevelNamespace = "uri://ed-fi.org/GradeLevelDescriptor";

    private const string SchoolCategoryDescriptor = $"{EducationOrganizationCategoryNamespace}#School";
    private const string GradeLevelDescriptor = $"{GradeLevelNamespace}#Tenth grade";

    private const string AuthorizationType = "urn:ed-fi:api:security:authorization";
    private const string StoredNullType =
        "urn:ed-fi:api:security:authorization:relationships:invalid-data:element-uninitialized";
    private const string ProposedMissingType =
        "urn:ed-fi:api:security:authorization:relationships:access-denied:element-required";

    private const string StudentSchoolAssociationHint =
        "You may need to create a corresponding 'StudentSchoolAssociation' item.";

    private static readonly string[] _noFurtherAuthorizationRequiredStrategy =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];

    private static readonly string[] _relationshipsWithStudentsOnlyStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
    ];

    private static readonly string[] _relationshipsWithEdOrgsAndPeopleStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
    ];

    public static IClaimSetProvider CreateReadUpdateDeleteStudentsOnlyClaimSetProvider(
        FixtureContext fixture
    ) =>
        CreateClaimSetProvider(
            fixture,
            new AuthzActionStrategies(
                Create: _noFurtherAuthorizationRequiredStrategy,
                Read: _relationshipsWithStudentsOnlyStrategy,
                Update: _relationshipsWithStudentsOnlyStrategy,
                Delete: _relationshipsWithStudentsOnlyStrategy
            )
        );

    public static IClaimSetProvider CreateCreateStudentsOnlyClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            new AuthzActionStrategies(
                Create: _relationshipsWithStudentsOnlyStrategy,
                Read: _noFurtherAuthorizationRequiredStrategy,
                Update: _noFurtherAuthorizationRequiredStrategy,
                Delete: _noFurtherAuthorizationRequiredStrategy
            )
        );

    public static IClaimSetProvider CreateReadEdOrgAndPeopleClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            new AuthzActionStrategies(
                Create: _noFurtherAuthorizationRequiredStrategy,
                Read: _relationshipsWithEdOrgsAndPeopleStrategy,
                Update: _noFurtherAuthorizationRequiredStrategy,
                Delete: _noFurtherAuthorizationRequiredStrategy
            )
        );

    public static IClaimSetProvider CreateUpdateEdOrgAndPeopleClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            new AuthzActionStrategies(
                Create: _noFurtherAuthorizationRequiredStrategy,
                Read: _noFurtherAuthorizationRequiredStrategy,
                Update: _relationshipsWithEdOrgsAndPeopleStrategy,
                Delete: _noFurtherAuthorizationRequiredStrategy
            )
        );

    public static async Task It_returns_people_problem_details_for_unauthorized_get_by_id(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateAuthorizationStudentSchoolResourceAsync(
            harness,
            new AuthorizationStudentSchoolSeed(
                101,
                "get-denied",
                AuthorizedSchoolId,
                UnauthorizedStudentUniqueId
            )
        );

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(locationPath);

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                AuthorizationType,
                $"Access to the requested data could not be authorized. Hint: {StudentSchoolAssociationHint}",
                [
                    "No relationships have been established between the caller's education organization id claim ('900') and the resource item's 'StudentUniqueId' value.",
                ]
            )
        );
    }

    public static async Task It_returns_people_problem_details_for_unauthorized_delete_with_no_claims(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateAuthorizationStudentSchoolResourceAsync(
            harness,
            new AuthorizationStudentSchoolSeed(
                102,
                "delete-empty-claims",
                AuthorizedSchoolId,
                AuthorizedStudentUniqueId
            )
        );

        using HttpResponseMessage response = await harness.HttpClient.DeleteAsync(locationPath);

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                AuthorizationType,
                $"Access to the requested data could not be authorized. Hint: {StudentSchoolAssociationHint}",
                [
                    "No relationships have been established between the caller's education organization id claims (none) and the resource item's 'StudentUniqueId' value.",
                ]
            )
        );
    }

    public static async Task It_returns_people_problem_details_for_unauthorized_post_create(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);

        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            AuthorizationStudentSchoolResourcesEndpoint,
            CreateAuthorizationStudentSchoolBody(
                new AuthorizationStudentSchoolSeed(
                    201,
                    "post-denied",
                    AuthorizedSchoolId,
                    UnauthorizedStudentUniqueId
                ),
                resourceId: null
            )
        );

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                AuthorizationType,
                $"Access to the requested data could not be authorized. Hint: {StudentSchoolAssociationHint}",
                [
                    "No relationships have been established between the caller's education organization id claim ('900') and the resource item's 'StudentUniqueId' value.",
                ]
            )
        );
    }

    public static async Task It_returns_people_proposed_data_problem_details_for_missing_post_create_student(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);

        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            AuthorizationStudentSchoolResourcesEndpoint,
            CreateAuthorizationStudentSchoolBody(
                new AuthorizationStudentSchoolSeed(202, "post-missing-student", AuthorizedSchoolId, null),
                resourceId: null
            )
        );

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                ProposedMissingType,
                $"Access to the requested data could not be authorized. The 'StudentUniqueId' value is required for authorization purposes. Hint: {StudentSchoolAssociationHint}",
                []
            )
        );
    }

    public static async Task It_returns_people_stored_data_problem_details_before_put_proposed_values(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateAuthorizationStudentSchoolResourceAsync(
            harness,
            new AuthorizationStudentSchoolSeed(301, "put-stored-missing", AuthorizedSchoolId, null)
        );
        string resourceId = GetResourceId(locationPath);

        using HttpResponseMessage response = await PutJsonAsync(
            harness,
            locationPath,
            CreateAuthorizationStudentSchoolBody(
                new AuthorizationStudentSchoolSeed(
                    301,
                    "put-stored-missing-change",
                    AuthorizedSchoolId,
                    AuthorizedStudentUniqueId
                ),
                resourceId
            )
        );

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                StoredNullType,
                $"Access to the requested data could not be authorized. The existing 'StudentUniqueId' value is required for authorization purposes. Hint: {StudentSchoolAssociationHint}",
                [
                    "The existing resource item is inaccessible to clients using the 'RelationshipsWithStudentsOnly' authorization strategy.",
                ]
            )
        );
    }

    public static async Task It_prioritizes_people_proposed_data_problem_details_over_mixed_no_relationship(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateAuthorizationStudentSchoolResourceAsync(
            harness,
            new AuthorizationStudentSchoolSeed(
                302,
                "put-mixed-existing",
                AuthorizedSchoolId,
                AuthorizedStudentUniqueId
            )
        );
        string resourceId = GetResourceId(locationPath);

        using HttpResponseMessage response = await PutJsonAsync(
            harness,
            locationPath,
            CreateAuthorizationStudentSchoolBody(
                new AuthorizationStudentSchoolSeed(302, "put-mixed-proposed", UnauthorizedSchoolId, null),
                resourceId
            )
        );

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                ProposedMissingType,
                $"Access to the requested data could not be authorized. The 'StudentUniqueId' value is required for authorization purposes. Hint: {StudentSchoolAssociationHint}",
                []
            )
        );
    }

    public static async Task It_renders_plural_securable_names_for_mixed_people_no_relationship(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateAuthorizationStudentSchoolResourceAsync(
            harness,
            new AuthorizationStudentSchoolSeed(
                401,
                "get-mixed-denied",
                UnauthorizedSchoolId,
                UnauthorizedStudentUniqueId
            )
        );

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(locationPath);

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                AuthorizationType,
                $"Access to the requested data could not be authorized. Hint: {StudentSchoolAssociationHint}",
                [
                    "No relationships have been established between the caller's education organization id claim ('900') and one or more of the following properties of the resource item: 'SchoolId', 'StudentUniqueId'.",
                ]
            )
        );
    }

    private static IClaimSetProvider CreateClaimSetProvider(
        FixtureContext fixture,
        AuthzActionStrategies authzStrategies
    ) =>
        new ConfigurableClaimSetProvider(
            fixture,
            (resource, action) =>
            {
                if (
                    !string.Equals(resource.ProjectName, AuthzProjectName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        resource.ResourceName,
                        AuthorizationStudentSchoolResourceName,
                        StringComparison.Ordinal
                    )
                )
                {
                    return _noFurtherAuthorizationRequiredStrategy;
                }

                return action switch
                {
                    "Create" => authzStrategies.Create,
                    "Read" => authzStrategies.Read,
                    "Update" => authzStrategies.Update,
                    "Delete" => authzStrategies.Delete,
                    _ => _noFurtherAuthorizationRequiredStrategy,
                };
            }
        );

    private static async Task SeedReferenceDataAsync(ApiIntegrationHarness harness)
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
        await CreateStudentAsync(harness, StudentsEndpoint, AuthorizedStudentUniqueId, "ProblemAuth");
        await CreateStudentAsync(harness, StudentsEndpoint, UnauthorizedStudentUniqueId, "ProblemDenied");
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
        await InsertAuthEdgeAsync(harness, ClaimEducationOrganizationId, AuthorizedSchoolId);
        await DeleteAuthEdgeAsync(harness, ClaimEducationOrganizationId, UnauthorizedSchoolId);
        await DeleteAuthEdgeAsync(harness, ClaimEducationOrganizationId, ClaimEducationOrganizationId);
    }

    private static async Task<string> CreateAuthorizationStudentSchoolResourceAsync(
        ApiIntegrationHarness harness,
        AuthorizationStudentSchoolSeed seed
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            AuthorizationStudentSchoolResourcesEndpoint,
            CreateAuthorizationStudentSchoolBody(seed, resourceId: null)
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return GetLocationPath(response);
    }

    private static JsonObject CreateAuthorizationStudentSchoolBody(
        AuthorizationStudentSchoolSeed seed,
        string? resourceId
    )
    {
        JsonObject body = new()
        {
            ["authorizationStudentSchoolId"] = seed.AuthorizationStudentSchoolId,
            ["name"] = seed.Name,
            ["schoolReference"] = new JsonObject { ["schoolId"] = seed.SchoolId },
        };

        if (resourceId is not null)
        {
            body["id"] = resourceId;
        }

        if (seed.StudentUniqueId is not null)
        {
            body["studentReference"] = new JsonObject { ["studentUniqueId"] = seed.StudentUniqueId };
        }

        return body;
    }

    private static async Task<HttpResponseMessage> PutJsonAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body
    ) => await SendJsonAsync(harness, HttpMethod.Put, endpoint, body);

    private static async Task<HttpResponseMessage> SendJsonAsync(
        ApiIntegrationHarness harness,
        HttpMethod method,
        string endpoint,
        JsonObject body
    )
    {
        using var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task AssertProblemDetailsAsync(
        HttpResponseMessage response,
        RelationshipProblemDetailsExpectation expectation
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be(expectation.Type);
        problem["title"]!.GetValue<string>().Should().Be("Authorization Denied");
        problem["status"]!.GetValue<int>().Should().Be(403);
        problem["detail"]!.GetValue<string>().Should().Be(expectation.Detail);
        problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        problem["validationErrors"]!.AsObject().Count.Should().Be(0);
        problem["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(expectation.Errors);
    }

    private static string GetLocationPath(HttpResponseMessage response)
    {
        response.Headers.Location.Should().NotBeNull();

        return response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
    }

    private static string GetResourceId(string locationPath) =>
        locationPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    private sealed record AuthzActionStrategies(
        IReadOnlyList<string> Create,
        IReadOnlyList<string> Read,
        IReadOnlyList<string> Update,
        IReadOnlyList<string> Delete
    );

    private sealed record AuthorizationStudentSchoolSeed(
        int AuthorizationStudentSchoolId,
        string Name,
        long SchoolId,
        string? StudentUniqueId
    );

    private sealed record RelationshipProblemDetailsExpectation(
        string Type,
        string Detail,
        IReadOnlyList<string> Errors
    );
}
