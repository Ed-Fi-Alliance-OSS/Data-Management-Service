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

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Public-boundary coverage for NamespaceBased authorization when the namespace is carried by a reference
/// whose target does not exist. The proposed namespace must still be read from the request body so that a
/// disallowed namespace is denied (403) before the missing reference is reported (409), instead of failing
/// as an unassigned namespace or as a database type-inference error.
/// </summary>
internal static class NamespaceAuthorizationMissingReferenceScenario
{
    public const string AllowedPrefix = "uri://allowed.example.org";
    public const string EdFiPrefix = "uri://ed-fi.org";

    /// <summary>Client prefixes in the order the claim carries them.</summary>
    public static IReadOnlyList<string> ClientPrefixes { get; } = [EdFiPrefix, AllowedPrefix];

    private const string AllowedNamespace = AllowedPrefix + "/Assessment";
    private const string DisallowedNamespace = "uri://other.example.org/Assessment";

    private const int SchoolId = 255901001;
    private const int SchoolYear = 2026;

    private const string StudentAssessmentsEndpoint = "/data/ed-fi/studentAssessments";
    private const string SurveyQuestionsEndpoint = "/data/ed-fi/surveyQuestions";

    private const string MismatchType =
        "urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch";
    private const string UnresolvedReferenceType = "urn:ed-fi:api:data-conflict:unresolved-reference";
    private const string KeyChangeNotSupportedType =
        "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported";

    /// <summary>
    /// The rendered prefix list is deduplicated and ordinal-sorted, so it is independent of claim order.
    /// </summary>
    private const string MismatchDetail =
        "Access to the requested data could not be authorized. The 'Namespace' value of the data does not "
        + "start with any of the caller's associated namespace prefixes ('uri://allowed.example.org', "
        + "'uri://ed-fi.org').";

    private static readonly string[] _namespaceBasedStrategy =
    [
        AuthorizationStrategyNameConstants.NamespaceBased,
    ];

    private static readonly string[] _noFurtherAuthorizationRequiredStrategy =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];

    /// <summary>
    /// NamespaceBased is granted only on the two resources under test, so every seed request is unrestricted.
    /// </summary>
    public static IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (resource, _) =>
                resource.ResourceName is "StudentAssessment" or "SurveyQuestion"
                    ? _namespaceBasedStrategy
                    : _noFurtherAuthorizationRequiredStrategy
        );

    public static async Task It_denies_an_existing_assessment_in_a_disallowed_namespace(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentAssessmentDependenciesAsync(harness);
        await CreateAssessmentAsync(harness, "A-existing-disallowed", DisallowedNamespace);

        using var response = await PostJsonAsync(
            harness,
            StudentAssessmentsEndpoint,
            StudentAssessmentBody("SA-existing-disallowed", "A-existing-disallowed", DisallowedNamespace)
        );

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Forbidden,
            MismatchType,
            "Authorization Denied",
            MismatchDetail,
            validationErrors: []
        );
    }

    public static async Task It_denies_a_missing_assessment_in_a_disallowed_namespace(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentAssessmentDependenciesAsync(harness);

        using var response = await PostJsonAsync(
            harness,
            StudentAssessmentsEndpoint,
            StudentAssessmentBody("SA-missing-disallowed", "A-missing-disallowed", DisallowedNamespace)
        );

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Forbidden,
            MismatchType,
            "Authorization Denied",
            MismatchDetail,
            validationErrors: []
        );
    }

    public static async Task It_reports_the_unresolved_reference_for_a_missing_assessment_in_an_allowed_namespace(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentAssessmentDependenciesAsync(harness);

        using var response = await PostJsonAsync(
            harness,
            StudentAssessmentsEndpoint,
            StudentAssessmentBody("SA-missing-allowed", "A-missing-allowed", AllowedNamespace)
        );

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Conflict,
            UnresolvedReferenceType,
            "Unresolved Reference",
            "One or more references could not be resolved. See 'validationErrors' for details.",
            validationErrors: new()
            {
                ["$.assessmentReference"] = ["The referenced Assessment item does not exist."],
            }
        );
    }

    public static async Task It_creates_a_student_assessment_for_an_existing_assessment_in_an_allowed_namespace(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentAssessmentDependenciesAsync(harness);
        await CreateAssessmentAsync(harness, "A-existing-allowed", AllowedNamespace);

        using var response = await PostJsonAsync(
            harness,
            StudentAssessmentsEndpoint,
            StudentAssessmentBody("SA-existing-allowed", "A-existing-allowed", AllowedNamespace)
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    public static async Task It_denies_key_unified_missing_references_in_a_disallowed_namespace(
        ApiIntegrationHarness harness
    )
    {
        await SeedSurveyQuestionDependenciesAsync(harness);

        using var response = await PostJsonAsync(
            harness,
            SurveyQuestionsEndpoint,
            SurveyQuestionBody("Q-disallowed", "SV-disallowed", "uri://other.example.org/Survey")
        );

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Forbidden,
            MismatchType,
            "Authorization Denied",
            MismatchDetail,
            validationErrors: []
        );
    }

    public static async Task It_reports_both_unresolved_key_unified_references_in_an_allowed_namespace(
        ApiIntegrationHarness harness
    )
    {
        await SeedSurveyQuestionDependenciesAsync(harness);

        using var response = await PostJsonAsync(
            harness,
            SurveyQuestionsEndpoint,
            SurveyQuestionBody("Q-allowed", "SV-allowed", AllowedPrefix + "/Survey")
        );

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Conflict,
            UnresolvedReferenceType,
            "Unresolved Reference",
            "One or more references could not be resolved. See 'validationErrors' for details.",
            validationErrors: new()
            {
                ["$.surveyReference"] = ["The referenced Survey item does not exist."],
                ["$.surveySectionReference"] = ["The referenced SurveySection item does not exist."],
            }
        );
    }

    /// <summary>
    /// The assessment reference is part of a student assessment's identity, so a PUT that repoints it is
    /// rejected as a key change before proposed namespace authorization runs, even when the new assessment
    /// is missing and its namespace is disallowed.
    /// </summary>
    public static async Task It_rejects_a_put_that_repoints_the_assessment_at_a_missing_one_in_a_disallowed_namespace(
        ApiIntegrationHarness harness
    )
    {
        await SeedStudentAssessmentDependenciesAsync(harness);
        await CreateAssessmentAsync(harness, "A-put", AllowedNamespace);
        var path = await CreateAsync(
            harness,
            StudentAssessmentsEndpoint,
            StudentAssessmentBody("SA-put", "A-put", AllowedNamespace)
        );

        using var response = await PutJsonAsync(
            harness,
            path,
            StudentAssessmentBody("SA-put", "A-put-missing", DisallowedNamespace)
        );

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.BadRequest,
            KeyChangeNotSupportedType,
            "Key Change Not Supported",
            "Identifying values for the StudentAssessment resource cannot be changed. Delete and recreate the "
                + "resource item instead.",
            validationErrors: []
        );
    }

    /// <summary>
    /// The survey section reference is not part of a survey question's identity, so a PUT can add one that
    /// is missing. Its namespace shares the canonical column with the resolved survey reference, so the
    /// stored namespace is authorized and the missing section is reported.
    /// </summary>
    public static async Task It_reports_the_unresolved_survey_section_on_a_put_in_an_allowed_namespace(
        ApiIntegrationHarness harness
    )
    {
        const string SurveyNamespace = AllowedPrefix + "/Survey";

        await SeedSurveyQuestionDependenciesAsync(harness);
        await CreateSeedAsync(
            harness,
            "/data/ed-fi/schoolYearTypes",
            new JsonObject
            {
                ["schoolYear"] = SchoolYear,
                ["schoolYearDescription"] = "2026",
                ["currentSchoolYear"] = true,
            }
        );
        await CreateSeedAsync(
            harness,
            "/data/ed-fi/surveys",
            new JsonObject
            {
                ["surveyIdentifier"] = "SV-put",
                ["namespace"] = SurveyNamespace,
                ["surveyTitle"] = "S",
                ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
            }
        );
        var surveyQuestion = SurveyQuestionBody("Q-put", "SV-put", SurveyNamespace);
        surveyQuestion.Remove("surveySectionReference");
        var path = await CreateAsync(harness, SurveyQuestionsEndpoint, surveyQuestion);

        using var response = await PutJsonAsync(
            harness,
            path,
            SurveyQuestionBody("Q-put", "SV-put", SurveyNamespace)
        );

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Conflict,
            UnresolvedReferenceType,
            "Unresolved Reference",
            "One or more references could not be resolved. See 'validationErrors' for details.",
            validationErrors: new()
            {
                ["$.surveySectionReference"] = ["The referenced SurveySection item does not exist."],
            }
        );
    }

    private static async Task SeedStudentAssessmentDependenciesAsync(ApiIntegrationHarness harness)
    {
        await CreateDescriptorAsync(
            harness,
            "/data/ed-fi/academicSubjectDescriptors",
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English Language Arts"
        );
        await CreateDescriptorAsync(
            harness,
            "/data/ed-fi/educationOrganizationCategoryDescriptors",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School"
        );
        await CreateDescriptorAsync(
            harness,
            "/data/ed-fi/gradeLevelDescriptors",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade"
        );
        await CreateSeedAsync(
            harness,
            "/data/ed-fi/schools",
            new JsonObject
            {
                ["schoolId"] = SchoolId,
                ["nameOfInstitution"] = "North School",
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
        await CreateSeedAsync(
            harness,
            "/data/ed-fi/students",
            new JsonObject
            {
                ["studentUniqueId"] = "S1",
                ["firstName"] = "Ada",
                ["lastSurname"] = "L",
                ["birthDate"] = "2010-01-01",
            }
        );
    }

    private static async Task SeedSurveyQuestionDependenciesAsync(ApiIntegrationHarness harness) =>
        await CreateDescriptorAsync(
            harness,
            "/data/ed-fi/questionFormDescriptors",
            "uri://ed-fi.org/QuestionFormDescriptor",
            "Text"
        );

    private static async Task CreateAssessmentAsync(
        ApiIntegrationHarness harness,
        string assessmentIdentifier,
        string @namespace
    ) =>
        await CreateSeedAsync(
            harness,
            "/data/ed-fi/assessments",
            new JsonObject
            {
                ["assessmentIdentifier"] = assessmentIdentifier,
                ["namespace"] = @namespace,
                ["assessmentTitle"] = "T",
                ["academicSubjects"] = new JsonArray(
                    new JsonObject
                    {
                        ["academicSubjectDescriptor"] =
                            "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts",
                    }
                ),
            }
        );

    private static JsonObject StudentAssessmentBody(
        string studentAssessmentIdentifier,
        string assessmentIdentifier,
        string assessmentNamespace
    ) =>
        new()
        {
            ["studentAssessmentIdentifier"] = studentAssessmentIdentifier,
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = "S1" },
            ["assessmentReference"] = new JsonObject
            {
                ["assessmentIdentifier"] = assessmentIdentifier,
                ["namespace"] = assessmentNamespace,
            },
            ["reportedSchoolReference"] = new JsonObject { ["schoolId"] = SchoolId },
            ["eventDescription"] = "event",
        };

    /// <summary>
    /// Neither the survey nor the survey section exists, and both references carry the same namespace, which
    /// key-unifies the survey question's namespace onto one canonical column.
    /// </summary>
    private static JsonObject SurveyQuestionBody(
        string questionCode,
        string surveyIdentifier,
        string surveyNamespace
    ) =>
        new()
        {
            ["questionCode"] = questionCode,
            ["surveyReference"] = new JsonObject
            {
                ["surveyIdentifier"] = surveyIdentifier,
                ["namespace"] = surveyNamespace,
            },
            ["surveySectionReference"] = new JsonObject
            {
                ["surveyIdentifier"] = surveyIdentifier,
                ["namespace"] = surveyNamespace,
                ["surveySectionTitle"] = "SS1",
            },
            ["questionFormDescriptor"] = "uri://ed-fi.org/QuestionFormDescriptor#Text",
            ["questionText"] = "Q?",
        };

    private static Task CreateDescriptorAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string @namespace,
        string codeValue
    ) =>
        CreateSeedAsync(
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

    /// <summary>
    /// Each test seeds the shared reference data it needs; re-posting an existing seed is an idempotent upsert.
    /// </summary>
    private static async Task CreateSeedAsync(ApiIntegrationHarness harness, string endpoint, JsonObject body)
    {
        using var response = await PostJsonAsync(harness, endpoint, body);

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Created, HttpStatusCode.OK], await response.Content.ReadAsStringAsync());
    }

    /// <summary>Creates a document and returns its resource path from the Location header.</summary>
    private static async Task<string> CreateAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body
    )
    {
        using var response = await PostJsonAsync(harness, endpoint, body);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
    }

    private static async Task<HttpResponseMessage> PutJsonAsync(
        ApiIntegrationHarness harness,
        string path,
        JsonObject body
    )
    {
        body["id"] = path.Split('/')[^1];

        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        return await harness.HttpClient.SendAsync(request);
    }

    /// <summary>
    /// Authorization failures are served as problem+json while data conflicts and key-change rejections are
    /// served as plain JSON.
    /// </summary>
    private static string ExpectedMediaType(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.Forbidden ? "application/problem+json" : "application/json";

    private static async Task AssertProblemDetailsAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode,
        string expectedType,
        string expectedTitle,
        string expectedDetail,
        Dictionary<string, string[]> validationErrors
    )
    {
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatusCode, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be(ExpectedMediaType(expectedStatusCode));

        var problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be(expectedType);
        problem["title"]!.GetValue<string>().Should().Be(expectedTitle);
        problem["status"]!.GetValue<int>().Should().Be((int)expectedStatusCode);
        problem["detail"]!.GetValue<string>().Should().Be(expectedDetail);
        problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        problem["errors"]!.AsArray().Should().BeEmpty();

        var actualValidationErrors = problem["validationErrors"]!
            .AsObject()
            .ToDictionary(
                static property => property.Key,
                static property =>
                    property.Value!.AsArray().Select(static error => error!.GetValue<string>()).ToArray()
            );
        actualValidationErrors.Should().BeEquivalentTo(validationErrors);
    }
}
