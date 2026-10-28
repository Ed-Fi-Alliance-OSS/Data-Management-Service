// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Public HTTP contract for natural-key conflicts on a write: a 409 whose problem type is
/// <c>urn:ed-fi:api:data-conflict:non-unique-identity</c> and whose single error names every duplicate
/// identity value by its full property path, so two references ending in the same property stay
/// distinguishable.
///
/// Both conflicts are deterministic, with no concurrent requests:
/// <list type="bullet">
/// <item>POST: a StudentSpecialEducationProgramAssociation with the same identity as an existing
/// StudentProgramAssociation. Both are GeneralStudentProgramAssociation subclasses, so the POST creates a
/// new document that collides on the shared abstract identity. The association and its program belong to
/// different schools, so the two <c>educationOrganizationId</c> values differ.</item>
/// <item>PUT: a ClassPeriod renamed onto a sibling ClassPeriod's natural key, which ClassPeriod's
/// identity-update support lets reach the root natural-key constraint.</item>
/// </list>
///
/// Bound to <c>FixtureKey.AuthoritativeDs52</c> for the production association, program and ClassPeriod
/// shapes. Every test starts from a freshly leased database, so fixed identity values give one exact
/// expected message per test.
/// </summary>
internal static class IdentityConflictScenario
{
    private const string StandardJsonContentType = "application/json";
    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string ProgramTypeDescriptorsEndpoint = "/data/ed-fi/programTypeDescriptors";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string ProgramsEndpoint = "/data/ed-fi/programs";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StudentProgramAssociationsEndpoint = "/data/ed-fi/studentProgramAssociations";
    private const string StudentSpecialEducationProgramAssociationsEndpoint =
        "/data/ed-fi/studentSpecialEducationProgramAssociations";
    private const string ClassPeriodsEndpoint = "/data/ed-fi/classPeriods";

    private const string DescriptorNamespace = "uri://ed-fi.org/IdentityConflict";
    private const string ProgramTypeDescriptorUri =
        $"{DescriptorNamespace}/ProgramTypeDescriptor#Gifted and Talented";
    private const int AssociationSchoolId = 255901001;
    private const int ProgramSchoolId = 255901002;
    private const string ProgramName = "Gifted and Talented";
    private const string StudentUniqueId = "ic-001";
    private const string BeginDate = "2025-08-01";

    private const string ExpectedType = "urn:ed-fi:api:data-conflict:non-unique-identity";
    private const string ExpectedTitle = "Identifying Values Are Not Unique";
    private const string ExpectedDetail =
        "The identifying value(s) of the item are the same as another item that already exists.";

    public static async Task It_reports_non_unique_identity_with_qualified_names_for_a_post_on_a_shared_abstract_identity(
        ApiIntegrationHarness harness
    )
    {
        await SeedSchoolsAsync(harness);
        await SeedDescriptorAsync(
            harness,
            ProgramTypeDescriptorsEndpoint,
            $"{DescriptorNamespace}/ProgramTypeDescriptor",
            ProgramName
        );
        await PostExpectingCreatedAsync(
            harness,
            ProgramsEndpoint,
            new JsonObject
            {
                ["programName"] = ProgramName,
                ["programTypeDescriptor"] = ProgramTypeDescriptorUri,
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["educationOrganizationId"] = ProgramSchoolId,
                },
            }
        );
        await PostExpectingCreatedAsync(
            harness,
            StudentsEndpoint,
            new JsonObject
            {
                ["studentUniqueId"] = StudentUniqueId,
                ["firstName"] = "Identity",
                ["lastSurname"] = "Conflict",
                ["birthDate"] = "2010-01-01",
            }
        );
        await PostExpectingCreatedAsync(
            harness,
            StudentProgramAssociationsEndpoint,
            GeneralStudentProgramAssociationBody()
        );

        using HttpResponseMessage response = await PostAsync(
            harness,
            StudentSpecialEducationProgramAssociationsEndpoint,
            GeneralStudentProgramAssociationBody()
        );

        await AssertNonUniqueIdentityProblemAsync(
            response,
            "A natural key conflict occurred when attempting to create a new resource StudentSpecialEducationProgramAssociation with a duplicate key. "
                + $"The duplicate keys and values are (beginDate = {BeginDate}),"
                + $"(educationOrganizationReference.educationOrganizationId = {AssociationSchoolId}),"
                + $"(programReference.educationOrganizationId = {ProgramSchoolId}),"
                + $"(programReference.programName = {ProgramName}),"
                + $"(programReference.programTypeDescriptor = {ProgramTypeDescriptorUri}),"
                + $"(studentReference.studentUniqueId = {StudentUniqueId})"
        );
    }

    public static async Task It_reports_non_unique_identity_with_qualified_names_for_a_put_that_collides_on_a_natural_key(
        ApiIntegrationHarness harness
    )
    {
        await SeedSchoolsAsync(harness);
        await PostExpectingCreatedAsync(harness, ClassPeriodsEndpoint, ClassPeriodBody("First"));
        string secondClassPeriodPath = await PostExpectingCreatedAsync(
            harness,
            ClassPeriodsEndpoint,
            ClassPeriodBody("Second")
        );
        string secondClassPeriodId = secondClassPeriodPath.Split('/')[^1];

        JsonObject renamedBody = ClassPeriodBody("First");
        renamedBody["id"] = secondClassPeriodId;
        using var content = new StringContent(
            renamedBody.ToJsonString(),
            Encoding.UTF8,
            StandardJsonContentType
        );
        using HttpResponseMessage response = await harness.HttpClient.PutAsync(
            secondClassPeriodPath,
            content
        );

        await AssertNonUniqueIdentityProblemAsync(
            response,
            "A natural key conflict occurred when attempting to update a resource ClassPeriod with a duplicate key. "
                + $"The duplicate keys and values are (classPeriodName = First),(schoolReference.schoolId = {AssociationSchoolId})"
        );
    }

    private static async Task AssertNonUniqueIdentityProblemAsync(
        HttpResponseMessage response,
        string expectedError
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be(ExpectedType, body);
        problem["title"]!.GetValue<string>().Should().Be(ExpectedTitle, body);
        problem["detail"]!.GetValue<string>().Should().Be(ExpectedDetail, body);
        problem["status"]!.GetValue<int>().Should().Be(409, body);
        problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace(body);
        problem["validationErrors"]!.AsObject().Should().BeEmpty(body);
        problem["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal([expectedError], body);
    }

    private static JsonObject GeneralStudentProgramAssociationBody() =>
        new()
        {
            ["beginDate"] = BeginDate,
            ["educationOrganizationReference"] = new JsonObject
            {
                ["educationOrganizationId"] = AssociationSchoolId,
            },
            ["programReference"] = new JsonObject
            {
                ["educationOrganizationId"] = ProgramSchoolId,
                ["programName"] = ProgramName,
                ["programTypeDescriptor"] = ProgramTypeDescriptorUri,
            },
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = StudentUniqueId },
        };

    private static JsonObject ClassPeriodBody(string classPeriodName) =>
        new()
        {
            ["classPeriodName"] = classPeriodName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = AssociationSchoolId },
        };

    private static async Task SeedSchoolsAsync(ApiIntegrationHarness harness)
    {
        await SeedDescriptorAsync(
            harness,
            EducationOrganizationCategoryDescriptorsEndpoint,
            $"{DescriptorNamespace}/EducationOrganizationCategoryDescriptor",
            "School"
        );
        await SeedDescriptorAsync(
            harness,
            GradeLevelDescriptorsEndpoint,
            $"{DescriptorNamespace}/GradeLevelDescriptor",
            "Tenth grade"
        );

        foreach (int schoolId in new[] { AssociationSchoolId, ProgramSchoolId })
        {
            await PostExpectingCreatedAsync(
                harness,
                SchoolsEndpoint,
                new JsonObject
                {
                    ["schoolId"] = schoolId,
                    ["nameOfInstitution"] = $"Identity Conflict School {schoolId}",
                    ["educationOrganizationCategories"] = new JsonArray(
                        new JsonObject
                        {
                            ["educationOrganizationCategoryDescriptor"] =
                                $"{DescriptorNamespace}/EducationOrganizationCategoryDescriptor#School",
                        }
                    ),
                    ["gradeLevels"] = new JsonArray(
                        new JsonObject
                        {
                            ["gradeLevelDescriptor"] =
                                $"{DescriptorNamespace}/GradeLevelDescriptor#Tenth grade",
                        }
                    ),
                }
            );
        }
    }

    private static Task SeedDescriptorAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string namespaceUri,
        string codeValue
    ) =>
        PostExpectingCreatedAsync(
            harness,
            endpoint,
            new JsonObject
            {
                ["namespace"] = namespaceUri,
                ["codeValue"] = codeValue,
                ["shortDescription"] = codeValue,
            }
        );

    private static async Task<HttpResponseMessage> PostAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        return await harness.HttpClient.PostAsync(endpoint, content);
    }

    /// <summary>
    /// Posts a seed payload, requires 201, and returns the created resource's path for later requests.
    /// </summary>
    private static async Task<string> PostExpectingCreatedAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using HttpResponseMessage response = await PostAsync(harness, endpoint, payload);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {endpoint} body: {body}");

        return response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
    }
}
