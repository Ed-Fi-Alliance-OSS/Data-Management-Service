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

internal static class PeopleRelationshipGetManyContactStaffScenario
{
    public const long ClaimEducationOrganizationId = 910;

    private const long AuthorizedSchoolId = 110;
    private const long UnauthorizedSchoolId = 310;

    private const string AuthorizedStudentUniqueId = "student-contact-auth-001";
    private const string UnauthorizedStudentUniqueId = "student-contact-denied-001";
    private const string AuthorizedContactUniqueId = "contact-auth-001";
    private const string UnauthorizedContactUniqueId = "contact-denied-001";
    private const string AuthorizedAssignmentStaffUniqueId = "staff-assignment-auth-001";
    private const string AuthorizedEmploymentStaffUniqueId = "staff-employment-auth-001";
    private const string UnauthorizedStaffUniqueId = "staff-denied-001";

    private const string EdFiProjectName = "Ed-Fi";
    private const string ContactResourceName = "Contact";
    private const string StaffResourceName = "Staff";
    private const string StudentContactAssociationResourceName = "StudentContactAssociation";
    private const string StaffEducationOrganizationAssignmentAssociationResourceName =
        "StaffEducationOrganizationAssignmentAssociation";
    private const string StaffEducationOrganizationEmploymentAssociationResourceName =
        "StaffEducationOrganizationEmploymentAssociation";

    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string ContactsEndpoint = "/data/ed-fi/contacts";
    private const string StaffsEndpoint = "/data/ed-fi/staffs";
    private const string StudentSchoolAssociationsEndpoint = "/data/ed-fi/studentSchoolAssociations";
    private const string StudentContactAssociationsEndpoint = "/data/ed-fi/studentContactAssociations";
    private const string StaffEducationOrganizationAssignmentAssociationsEndpoint =
        "/data/ed-fi/staffEducationOrganizationAssignmentAssociations";
    private const string StaffEducationOrganizationEmploymentAssociationsEndpoint =
        "/data/ed-fi/staffEducationOrganizationEmploymentAssociations";

    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string StaffClassificationDescriptorsEndpoint =
        "/data/ed-fi/staffClassificationDescriptors";
    private const string EmploymentStatusDescriptorsEndpoint = "/data/ed-fi/employmentStatusDescriptors";

    private const string EducationOrganizationCategoryNamespace =
        "uri://ed-fi.org/EducationOrganizationCategoryDescriptor";
    private const string GradeLevelNamespace = "uri://ed-fi.org/GradeLevelDescriptor";
    private const string StaffClassificationNamespace = "uri://ed-fi.org/StaffClassificationDescriptor";
    private const string EmploymentStatusNamespace = "uri://ed-fi.org/EmploymentStatusDescriptor";

    private const string SchoolCategoryDescriptor = $"{EducationOrganizationCategoryNamespace}#School";
    private const string GradeLevelDescriptor = $"{GradeLevelNamespace}#Tenth grade";
    private const string StaffClassificationDescriptor = $"{StaffClassificationNamespace}#Teacher";
    private const string EmploymentStatusDescriptor = $"{EmploymentStatusNamespace}#Teacher";

    private static readonly string[] _noFurtherAuthorizationRequiredStrategy =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];

    public static IClaimSetProvider CreatePeopleOnlyReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [
                new RelationshipReadResource(EdFiProjectName, ContactResourceName),
                new RelationshipReadResource(EdFiProjectName, StudentContactAssociationResourceName),
                new RelationshipReadResource(EdFiProjectName, StaffResourceName),
                new RelationshipReadResource(
                    EdFiProjectName,
                    StaffEducationOrganizationAssignmentAssociationResourceName
                ),
                new RelationshipReadResource(
                    EdFiProjectName,
                    StaffEducationOrganizationEmploymentAssociationResourceName
                ),
            ],
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly
        );

    public static async Task It_filters_contact_get_many(ApiIntegrationHarness harness)
    {
        await SeedContactDataAsync(harness);

        JsonArray contacts = await GetJsonArrayAsync(harness, $"{ContactsEndpoint}?totalCount=true", 1);
        ExtractValues(contacts, static item => item["contactUniqueId"]!.GetValue<string>())
            .Should()
            .Equal(AuthorizedContactUniqueId);

        JsonArray studentContactAssociations = await GetJsonArrayAsync(
            harness,
            $"{StudentContactAssociationsEndpoint}?totalCount=true",
            1
        );
        ExtractValues(
                studentContactAssociations,
                static item => item["contactReference"]!["contactUniqueId"]!.GetValue<string>()
            )
            .Should()
            .Equal(AuthorizedContactUniqueId);
    }

    public static async Task It_filters_staff_get_many(ApiIntegrationHarness harness)
    {
        await SeedStaffDataAsync(harness);

        JsonArray staffs = await GetJsonArrayAsync(harness, $"{StaffsEndpoint}?totalCount=true", 2);
        ExtractValues(staffs, static item => item["staffUniqueId"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(AuthorizedAssignmentStaffUniqueId, AuthorizedEmploymentStaffUniqueId);

        JsonArray assignmentAssociations = await GetJsonArrayAsync(
            harness,
            $"{StaffEducationOrganizationAssignmentAssociationsEndpoint}?totalCount=true",
            1
        );
        ExtractValues(
                assignmentAssociations,
                static item => item["staffReference"]!["staffUniqueId"]!.GetValue<string>()
            )
            .Should()
            .Equal(AuthorizedAssignmentStaffUniqueId);

        JsonArray employmentAssociations = await GetJsonArrayAsync(
            harness,
            $"{StaffEducationOrganizationEmploymentAssociationsEndpoint}?totalCount=true",
            1
        );
        ExtractValues(
                employmentAssociations,
                static item => item["staffReference"]!["staffUniqueId"]!.GetValue<string>()
            )
            .Should()
            .Equal(AuthorizedEmploymentStaffUniqueId);
    }

    private static IClaimSetProvider CreateClaimSetProvider(
        FixtureContext fixture,
        IReadOnlyList<RelationshipReadResource> readRelationshipResources,
        string readRelationshipStrategy
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
                    return [readRelationshipStrategy];
                }

                return _noFurtherAuthorizationRequiredStrategy;
            }
        );
    }

    private static async Task SeedContactDataAsync(ApiIntegrationHarness harness)
    {
        await SeedCommonReferenceDataAsync(harness);
        await CreateStudentAsync(harness, AuthorizedStudentUniqueId, "ContactAuth");
        await CreateStudentAsync(harness, UnauthorizedStudentUniqueId, "ContactDenied");
        await CreateContactAsync(harness, AuthorizedContactUniqueId, "ContactAuth");
        await CreateContactAsync(harness, UnauthorizedContactUniqueId, "ContactDenied");
        await CreateStudentSchoolAssociationAsync(harness, AuthorizedStudentUniqueId, AuthorizedSchoolId);
        await CreateStudentSchoolAssociationAsync(harness, UnauthorizedStudentUniqueId, UnauthorizedSchoolId);
        await CreateStudentContactAssociationAsync(
            harness,
            AuthorizedStudentUniqueId,
            AuthorizedContactUniqueId
        );
        await CreateStudentContactAssociationAsync(
            harness,
            UnauthorizedStudentUniqueId,
            UnauthorizedContactUniqueId
        );
    }

    private static async Task SeedStaffDataAsync(ApiIntegrationHarness harness)
    {
        await SeedCommonReferenceDataAsync(harness);
        await CreateStaffAsync(harness, AuthorizedAssignmentStaffUniqueId, "AssignmentAuth");
        await CreateStaffAsync(harness, AuthorizedEmploymentStaffUniqueId, "EmploymentAuth");
        await CreateStaffAsync(harness, UnauthorizedStaffUniqueId, "StaffDenied");
        await CreateStaffEducationOrganizationAssignmentAssociationAsync(
            harness,
            AuthorizedAssignmentStaffUniqueId,
            AuthorizedSchoolId
        );
        await CreateStaffEducationOrganizationEmploymentAssociationAsync(
            harness,
            AuthorizedEmploymentStaffUniqueId,
            AuthorizedSchoolId
        );
        await CreateStaffEducationOrganizationAssignmentAssociationAsync(
            harness,
            UnauthorizedStaffUniqueId,
            UnauthorizedSchoolId
        );
        await CreateStaffEducationOrganizationEmploymentAssociationAsync(
            harness,
            UnauthorizedStaffUniqueId,
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
        await CreateDescriptorAsync(
            harness,
            StaffClassificationDescriptorsEndpoint,
            StaffClassificationNamespace,
            "Teacher"
        );
        await CreateDescriptorAsync(
            harness,
            EmploymentStatusDescriptorsEndpoint,
            EmploymentStatusNamespace,
            "Teacher"
        );
        await CreateSchoolAsync(harness, AuthorizedSchoolId, "Authorized School");
        await CreateSchoolAsync(harness, UnauthorizedSchoolId, "Unauthorized School");
        await InsertAuthEdgeAsync(harness, ClaimEducationOrganizationId, AuthorizedSchoolId);
        await DeleteAuthEdgeAsync(harness, ClaimEducationOrganizationId, UnauthorizedSchoolId);
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

    private static async Task CreateSchoolAsync(ApiIntegrationHarness harness, long schoolId, string name)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            SchoolsEndpoint,
            new JsonObject
            {
                ["schoolId"] = schoolId,
                ["nameOfInstitution"] = name,
                ["educationOrganizationCategories"] = new JsonArray(
                    new JsonObject { ["educationOrganizationCategoryDescriptor"] = SchoolCategoryDescriptor }
                ),
                ["gradeLevels"] = new JsonArray(
                    new JsonObject { ["gradeLevelDescriptor"] = GradeLevelDescriptor }
                ),
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateStudentAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        string nameSuffix
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            StudentsEndpoint,
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

    private static async Task CreateContactAsync(
        ApiIntegrationHarness harness,
        string contactUniqueId,
        string nameSuffix
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            ContactsEndpoint,
            new JsonObject
            {
                ["contactUniqueId"] = contactUniqueId,
                ["firstName"] = $"Contact-{nameSuffix}",
                ["lastSurname"] = "Relationship",
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateStaffAsync(
        ApiIntegrationHarness harness,
        string staffUniqueId,
        string nameSuffix
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            StaffsEndpoint,
            new JsonObject
            {
                ["staffUniqueId"] = staffUniqueId,
                ["firstName"] = $"Staff-{nameSuffix}",
                ["lastSurname"] = "Relationship",
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateStudentSchoolAssociationAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        long schoolId
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            new JsonObject
            {
                ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
                ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
                ["entryDate"] = "2025-08-01",
                ["entryGradeLevelDescriptor"] = GradeLevelDescriptor,
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateStudentContactAssociationAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        string contactUniqueId
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            StudentContactAssociationsEndpoint,
            new JsonObject
            {
                ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
                ["contactReference"] = new JsonObject { ["contactUniqueId"] = contactUniqueId },
                ["emergencyContactStatus"] = true,
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateStaffEducationOrganizationAssignmentAssociationAsync(
        ApiIntegrationHarness harness,
        string staffUniqueId,
        long educationOrganizationId
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            StaffEducationOrganizationAssignmentAssociationsEndpoint,
            new JsonObject
            {
                ["staffReference"] = new JsonObject { ["staffUniqueId"] = staffUniqueId },
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["educationOrganizationId"] = educationOrganizationId,
                },
                ["staffClassificationDescriptor"] = StaffClassificationDescriptor,
                ["beginDate"] = "2025-08-01",
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateStaffEducationOrganizationEmploymentAssociationAsync(
        ApiIntegrationHarness harness,
        string staffUniqueId,
        long educationOrganizationId
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            StaffEducationOrganizationEmploymentAssociationsEndpoint,
            new JsonObject
            {
                ["staffReference"] = new JsonObject { ["staffUniqueId"] = staffUniqueId },
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["educationOrganizationId"] = educationOrganizationId,
                },
                ["employmentStatusDescriptor"] = EmploymentStatusDescriptor,
                ["hireDate"] = "2025-08-01",
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task<JsonArray> GetJsonArrayAsync(
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

    private static string[] ExtractValues(JsonArray array, Func<JsonObject, string> selector) =>
        [.. array.Select(node => selector(node!.AsObject()))];

    private static async Task<HttpResponseMessage> PostJsonAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body
    ) => await SendJsonAsync(harness, HttpMethod.Post, endpoint, body);

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
        AddParameters(command, parameters);

        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameters(DbCommand command, params (string Name, object Value)[] parameters)
    {
        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private static bool IsMssql(DbConnection connection)
    {
        string? fullName = connection.GetType().FullName;
        return fullName is not null && fullName.Contains("SqlClient", StringComparison.Ordinal);
    }

    private static string CreateRelationshipReadResourceKey(string projectName, string resourceName) =>
        $"{projectName}:{resourceName}";

    private sealed record RelationshipReadResource(string ProjectName, string ResourceName);
}
