// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class PeopleRelationshipGetManyScenarioHelpers
{
    private static readonly string[] _noFurtherAuthorizationRequiredStrategy =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];

    public static IClaimSetProvider CreateClaimSetProvider(
        FixtureContext fixture,
        IReadOnlyList<RelationshipReadResource> readRelationshipResources,
        string readRelationshipStrategy
    ) => CreateClaimSetProvider(fixture, readRelationshipResources, [readRelationshipStrategy]);

    public static IClaimSetProvider CreateClaimSetProvider(
        FixtureContext fixture,
        IReadOnlyList<RelationshipReadResource> readRelationshipResources,
        IReadOnlyList<string> readRelationshipStrategies
    )
    {
        var relationshipReadResourceKeys = new HashSet<string>(
            readRelationshipResources.Select(static resource =>
                CreateRelationshipReadResourceKey(resource.ProjectName, resource.ResourceName)
            ),
            StringComparer.OrdinalIgnoreCase
        );

        return new ConfigurableClaimSetProvider(
            fixture,
            (resource, action) =>
            {
                if (
                    string.Equals(action, "Read", StringComparison.Ordinal)
                    && relationshipReadResourceKeys.Contains(
                        CreateRelationshipReadResourceKey(resource.ProjectName, resource.ResourceName)
                    )
                )
                {
                    return readRelationshipStrategies;
                }

                return _noFurtherAuthorizationRequiredStrategy;
            }
        );
    }

    public static async Task CreateDescriptorAsync(
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

    public static async Task CreateSchoolAsync(
        ApiIntegrationHarness harness,
        string schoolsEndpoint,
        long schoolId,
        string name,
        string schoolCategoryDescriptor,
        string gradeLevelDescriptor
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            schoolsEndpoint,
            new JsonObject
            {
                ["schoolId"] = schoolId,
                ["nameOfInstitution"] = name,
                ["educationOrganizationCategories"] = new JsonArray(
                    new JsonObject { ["educationOrganizationCategoryDescriptor"] = schoolCategoryDescriptor }
                ),
                ["gradeLevels"] = new JsonArray(
                    new JsonObject { ["gradeLevelDescriptor"] = gradeLevelDescriptor }
                ),
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    public static async Task CreateSchoolYearTypeAsync(
        ApiIntegrationHarness harness,
        string schoolYearTypesEndpoint,
        int schoolYear
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            schoolYearTypesEndpoint,
            new JsonObject
            {
                ["schoolYear"] = schoolYear,
                ["schoolYearDescription"] = schoolYear.ToString(CultureInfo.InvariantCulture),
                ["currentSchoolYear"] = true,
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    public static async Task CreateStudentAsync(
        ApiIntegrationHarness harness,
        string studentsEndpoint,
        string studentUniqueId,
        string nameSuffix
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            studentsEndpoint,
            new JsonObject
            {
                ["studentUniqueId"] = studentUniqueId,
                ["firstName"] = $"Student-{nameSuffix}",
                ["lastSurname"] = "Relationship",
                ["birthDate"] = "2010-01-01",
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    public static async Task CreateStudentSchoolAssociationAsync(
        ApiIntegrationHarness harness,
        string studentSchoolAssociationsEndpoint,
        string studentUniqueId,
        long schoolId,
        string gradeLevelDescriptor
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            studentSchoolAssociationsEndpoint,
            new JsonObject
            {
                ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
                ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
                ["entryDate"] = "2025-08-01",
                ["entryGradeLevelDescriptor"] = gradeLevelDescriptor,
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    public static async Task CreateStudentAcademicRecordAsync(
        ApiIntegrationHarness harness,
        string studentAcademicRecordsEndpoint,
        string studentUniqueId,
        long educationOrganizationId,
        int schoolYear,
        string termDescriptor
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            studentAcademicRecordsEndpoint,
            new JsonObject
            {
                ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["educationOrganizationId"] = educationOrganizationId,
                },
                ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = schoolYear },
                ["termDescriptor"] = termDescriptor,
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    public static async Task<JsonArray> GetJsonArrayWithTotalCountAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        int expectedTotalCount
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response
            .Headers.TryGetValues("Total-Count", out IEnumerable<string>? totalCountHeader)
            .Should()
            .BeTrue("totalCount=true must emit the Total-Count response header");
        totalCountHeader!.Single().Should().Be(expectedTotalCount.ToString(CultureInfo.InvariantCulture));

        return JsonNode.Parse(body)!.AsArray();
    }

    public static string[] ExtractValues(JsonArray array, Func<JsonObject, string> selector) =>
        [.. array.Select(node => selector(node!.AsObject()))];

    public static async Task<HttpResponseMessage> PostJsonAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body
    ) => await SendJsonAsync(harness, HttpMethod.Post, endpoint, body);

    public static async Task<HttpResponseMessage> SendJsonAsync(
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

    public static async Task InsertAuthEdgeAsync(
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

    public static async Task DeleteAuthEdgeAsync(
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

    public static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> ReadInt64Async(
        DbConnection connection,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);

        object? result = await command.ExecuteScalarAsync();
        result.Should().NotBeNull($"query must return a scalar value: {sql}");

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static void AddParameters(DbCommand command, params (string Name, object Value)[] parameters)
    {
        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    public static bool IsMssql(DbConnection connection)
    {
        string? fullName = connection.GetType().FullName;
        return fullName is not null && fullName.Contains("SqlClient", StringComparison.Ordinal);
    }

    public static string CreateRelationshipReadResourceKey(string projectName, string resourceName) =>
        $"{projectName}:{resourceName}";
}

internal sealed record RelationshipReadResource(string ProjectName, string ResourceName);
