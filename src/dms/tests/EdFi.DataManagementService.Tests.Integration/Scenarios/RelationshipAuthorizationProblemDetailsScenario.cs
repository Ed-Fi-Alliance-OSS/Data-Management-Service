// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class RelationshipAuthorizationProblemDetailsScenario
{
    public const long ClaimEducationOrganizationId = 900;

    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string RootChildEndpoint = "/data/authz/authorizationRootChildResources";
    private const string NullableEndpoint = "/data/authz/authorizationNullableResources";

    private const string AuthorizationType = "urn:ed-fi:api:security:authorization";
    private const string StoredNullType =
        "urn:ed-fi:api:security:authorization:relationships:invalid-data:element-uninitialized";
    private const string ProposedMissingType =
        "urn:ed-fi:api:security:authorization:relationships:access-denied:element-required";

    private static readonly string[] _noFurtherAuthorizationRequiredStrategy =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];

    private static readonly string[] _relationshipWithEdOrgsOnlyStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
    ];

    public static IClaimSetProvider CreateReadDeleteUpdateClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            new AuthzActionStrategies(
                Create: _noFurtherAuthorizationRequiredStrategy,
                Read: _relationshipWithEdOrgsOnlyStrategy,
                Update: _relationshipWithEdOrgsOnlyStrategy,
                Delete: _relationshipWithEdOrgsOnlyStrategy
            )
        );

    public static IClaimSetProvider CreateCreateClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            new AuthzActionStrategies(
                Create: _relationshipWithEdOrgsOnlyStrategy,
                Read: _noFurtherAuthorizationRequiredStrategy,
                Update: _noFurtherAuthorizationRequiredStrategy,
                Delete: _noFurtherAuthorizationRequiredStrategy
            )
        );

    public static async Task It_returns_relationship_problem_details_for_unauthorized_get_by_id(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateRootChildAsync(harness, new RootChildSeed(101, "get-denied", 300));

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(locationPath);

        await AssertNoRelationshipProblemDetailsAsync(response, "'900'");
    }

    public static async Task It_returns_relationship_problem_details_for_unauthorized_delete(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateRootChildAsync(
            harness,
            new RootChildSeed(102, "delete-denied", 300)
        );

        using HttpResponseMessage response = await harness.HttpClient.DeleteAsync(locationPath);

        await AssertNoRelationshipProblemDetailsAsync(response, "'900'");
    }

    public static async Task It_returns_existing_data_problem_details_for_stored_value_put(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateNullableAsync(
            harness,
            new NullableSeed(201, "stored-null-existing", null)
        );
        string resourceId = GetResourceId(locationPath);

        using HttpResponseMessage response = await PutJsonAsync(
            harness,
            locationPath,
            CreateNullableBody(new NullableSeed(201, "stored-null-change", 100), resourceId)
        );

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                StoredNullType,
                "Access to the requested data could not be authorized. The existing 'NullableSchoolId' value is required for authorization purposes.",
                [
                    "The existing resource item is inaccessible to clients using the 'RelationshipsWithEdOrgsOnly' authorization strategy.",
                ]
            )
        );
    }

    public static async Task It_returns_proposed_data_problem_details_for_proposed_value_put(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateNullableAsync(
            harness,
            new NullableSeed(202, "proposed-missing-existing", 100)
        );
        string resourceId = GetResourceId(locationPath);

        using HttpResponseMessage response = await PutJsonAsync(
            harness,
            locationPath,
            CreateNullableBody(new NullableSeed(202, "proposed-missing-change", null), resourceId)
        );

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                ProposedMissingType,
                "Access to the requested data could not be authorized. The 'NullableSchoolId' value is required for authorization purposes.",
                []
            )
        );
    }

    public static async Task It_returns_relationship_problem_details_for_unauthorized_post_create(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);

        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            RootChildEndpoint,
            CreateRootChildBody(new RootChildSeed(301, "post-denied", 300))
        );

        await AssertNoRelationshipProblemDetailsAsync(response, "'900'");
    }

    public static async Task It_returns_proposed_data_problem_details_for_missing_create_securable_element(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);

        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            NullableEndpoint,
            CreateNullableBody(new NullableSeed(302, "post-missing", null), resourceId: null)
        );

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                ProposedMissingType,
                "Access to the requested data could not be authorized. The 'NullableSchoolId' value is required for authorization purposes.",
                []
            )
        );
    }

    public static async Task It_renders_empty_edorg_claims_as_none(ApiIntegrationHarness harness)
    {
        await SeedReferenceDataAsync(harness);
        string locationPath = await CreateRootChildAsync(
            harness,
            new RootChildSeed(401, "empty-claims", 100)
        );

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(locationPath);

        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                AuthorizationType,
                "Access to the requested data could not be authorized.",
                [
                    "No relationships have been established between the caller's education organization id claims (none) and the resource item's 'SchoolId' value.",
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
                if (!string.Equals(resource.ProjectName, "Authz", StringComparison.OrdinalIgnoreCase))
                {
                    return _noFurtherAuthorizationRequiredStrategy;
                }

                if (!IsAuthorizationProblemDetailsResource(resource.ResourceName))
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

    private static bool IsAuthorizationProblemDetailsResource(string resourceName) =>
        string.Equals(resourceName, "AuthorizationRootChildResource", StringComparison.Ordinal)
        || string.Equals(resourceName, "AuthorizationNullableResource", StringComparison.Ordinal);

    private static async Task SeedReferenceDataAsync(ApiIntegrationHarness harness)
    {
        await CreateDescriptorAsync(
            harness,
            EducationOrganizationCategoryDescriptorsEndpoint,
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School"
        );
        await CreateDescriptorAsync(
            harness,
            GradeLevelDescriptorsEndpoint,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade"
        );
        await CreateSchoolAsync(harness, 100, "North School");
        await CreateSchoolAsync(harness, 300, "West School");
        await InsertAuthEdgeAsync(harness, ClaimEducationOrganizationId, 100);
        await DeleteAuthEdgeAsync(harness, ClaimEducationOrganizationId, 300);
        await DeleteAuthEdgeAsync(harness, ClaimEducationOrganizationId, ClaimEducationOrganizationId);
    }

    private static async Task CreateDescriptorAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string @namespace,
        string codeValue
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            endpoint,
            new JsonObject
            {
                ["codeValue"] = codeValue,
                ["description"] = codeValue,
                ["namespace"] = @namespace,
                ["shortDescription"] = codeValue,
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateSchoolAsync(ApiIntegrationHarness harness, int schoolId, string name)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            SchoolsEndpoint,
            new JsonObject
            {
                ["schoolId"] = schoolId,
                ["nameOfInstitution"] = name,
                ["educationOrganizationCategories"] = new JsonArray(
                    new JsonObject
                    {
                        ["educationOrganizationCategoryDescriptor"] =
                            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
                    }
                ),
                ["gradeLevels"] = new JsonArray(
                    new JsonObject
                    {
                        ["gradeLevelDescriptor"] = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
                    }
                ),
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task<string> CreateRootChildAsync(ApiIntegrationHarness harness, RootChildSeed seed)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            RootChildEndpoint,
            CreateRootChildBody(seed)
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return GetLocationPath(response);
    }

    private static async Task<string> CreateNullableAsync(ApiIntegrationHarness harness, NullableSeed seed)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            NullableEndpoint,
            CreateNullableBody(seed, resourceId: null)
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return GetLocationPath(response);
    }

    private static JsonObject CreateRootChildBody(RootChildSeed seed) =>
        new()
        {
            ["authorizationRootChildId"] = seed.AuthorizationRootChildId,
            ["name"] = seed.Name,
            ["schoolReference"] = new JsonObject { ["schoolId"] = seed.SchoolId },
            ["classPeriods"] = new JsonArray(),
        };

    private static JsonObject CreateNullableBody(NullableSeed seed, string? resourceId)
    {
        JsonObject body = new()
        {
            ["authorizationNullableId"] = seed.AuthorizationNullableId,
            ["name"] = seed.Name,
        };

        if (resourceId is not null)
        {
            body["id"] = resourceId;
        }

        if (seed.NullableSchoolId is not null)
        {
            body["nullableSchoolId"] = seed.NullableSchoolId.Value;
        }

        return body;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body
    ) => await SendJsonAsync(harness, HttpMethod.Post, endpoint, body);

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

    private static async Task AssertNoRelationshipProblemDetailsAsync(
        HttpResponseMessage response,
        string expectedClaimDisplay
    ) =>
        await AssertProblemDetailsAsync(
            response,
            new RelationshipProblemDetailsExpectation(
                AuthorizationType,
                "Access to the requested data could not be authorized.",
                [
                    $"No relationships have been established between the caller's education organization id claim ({expectedClaimDisplay}) and the resource item's 'SchoolId' value.",
                ]
            )
        );

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

    private static async Task InsertAuthEdgeAsync(
        ApiIntegrationHarness harness,
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        string sql = IsMssql(harness.DbConnection)
            ? """
                IF NOT EXISTS (
                    SELECT 1
                    FROM [auth].[EducationOrganizationIdToEducationOrganizationId]
                    WHERE [SourceEducationOrganizationId] = @sourceEducationOrganizationId
                      AND [TargetEducationOrganizationId] = @targetEducationOrganizationId
                )
                BEGIN
                    INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] (
                        [SourceEducationOrganizationId],
                        [TargetEducationOrganizationId]
                    )
                    VALUES (@sourceEducationOrganizationId, @targetEducationOrganizationId);
                END
                """
            : """
                INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" (
                    "SourceEducationOrganizationId",
                    "TargetEducationOrganizationId"
                )
                VALUES (@sourceEducationOrganizationId, @targetEducationOrganizationId)
                ON CONFLICT DO NOTHING;
                """;

        await ExecuteNonQueryAsync(
            harness.DbConnection,
            sql,
            ("@sourceEducationOrganizationId", sourceEducationOrganizationId),
            ("@targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    private static async Task DeleteAuthEdgeAsync(
        ApiIntegrationHarness harness,
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        string sql = IsMssql(harness.DbConnection)
            ? """
                DELETE FROM [auth].[EducationOrganizationIdToEducationOrganizationId]
                WHERE [SourceEducationOrganizationId] = @sourceEducationOrganizationId
                  AND [TargetEducationOrganizationId] = @targetEducationOrganizationId;
                """
            : """
                DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
                WHERE "SourceEducationOrganizationId" = @sourceEducationOrganizationId
                  AND "TargetEducationOrganizationId" = @targetEducationOrganizationId;
                """;

        await ExecuteNonQueryAsync(
            harness.DbConnection,
            sql,
            ("@sourceEducationOrganizationId", sourceEducationOrganizationId),
            ("@targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static bool IsMssql(DbConnection connection)
    {
        string? fullName = connection.GetType().FullName;
        return fullName is not null && fullName.Contains("SqlClient", StringComparison.Ordinal);
    }

    private sealed record AuthzActionStrategies(
        IReadOnlyList<string> Create,
        IReadOnlyList<string> Read,
        IReadOnlyList<string> Update,
        IReadOnlyList<string> Delete
    );

    private sealed record RootChildSeed(int AuthorizationRootChildId, string Name, int SchoolId);

    private sealed record NullableSeed(int AuthorizationNullableId, string Name, int? NullableSchoolId);

    private sealed record RelationshipProblemDetailsExpectation(
        string Type,
        string Detail,
        IReadOnlyList<string> Errors
    );
}
