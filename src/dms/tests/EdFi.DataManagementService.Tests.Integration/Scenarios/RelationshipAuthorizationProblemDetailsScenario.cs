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
    private const string StudentAssessmentsEndpoint = "/data/ed-fi/studentAssessments";
    private const string CourseOfferingsEndpoint = "/data/ed-fi/courseOfferings";

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

    /// <summary>
    /// RelationshipsWithEdOrgsOnly on create and update of the root-child resource, StudentAssessment and CourseOffering.
    /// Every seed resource (schools, students, assessments, descriptors) stays unrestricted.
    /// </summary>
    public static IClaimSetProvider CreateMissingReferenceClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (resource, action) =>
                action is "Create" or "Update"
                && resource.ResourceName
                    is "AuthorizationRootChildResource"
                        or "StudentAssessment"
                        or "CourseOffering"
                    ? _relationshipWithEdOrgsOnlyStrategy
                    : _noFurtherAuthorizationRequiredStrategy
        );

    /// <summary>
    /// DMS-1659 AC1-AC3 and AC6. A supplied reference to a School that does not exist answers with the
    /// existing no-relationship 403, identical to an existing School the caller has no relationship with.
    /// Genuine absence of an optional reference keeps element-required, and an invalid required identity leaf
    /// inside a present reference stays a validation 400. Every assertion lives in this one test so that
    /// reverting the classification correction fails it.
    /// </summary>
    public static async Task It_distinguishes_unresolved_references_from_absence_without_disclosing_existence(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        await SeedStudentAssessmentDependenciesAsync(harness);
        await SeedCourseOfferingDependenciesAsync(harness);

        // AC1/AC3 on POST-create: the root-child SchoolId subject.
        AssertMissingAndUnrelatedAreIndistinguishable(
            await PostForProblemAsync(
                harness,
                RootChildEndpoint,
                CreateRootChildBody(new(501, "missing", 999))
            ),
            await PostForProblemAsync(
                harness,
                RootChildEndpoint,
                CreateRootChildBody(new(502, "unrelated", 300))
            ),
            "SchoolId"
        );

        // AC4 at the API boundary: PUT with an authorized stored School 100 and an unchanged natural key.
        string locationPath = await CreateRootChildAsync(
            harness,
            new RootChildSeed(503, "put-existing", 100)
        );
        string resourceId = GetResourceId(locationPath);
        AssertMissingAndUnrelatedAreIndistinguishable(
            await SendForProblemAsync(
                harness,
                HttpMethod.Put,
                locationPath,
                WithId(CreateRootChildBody(new(503, "put-missing", 999)), resourceId)
            ),
            await SendForProblemAsync(
                harness,
                HttpMethod.Put,
                locationPath,
                WithId(CreateRootChildBody(new(503, "put-unrelated", 300)), resourceId)
            ),
            "SchoolId"
        );

        // POST resolves the same natural key to the existing document, so this covers actual upsert-as-update.
        AssertMissingAndUnrelatedAreIndistinguishable(
            await PostForProblemAsync(
                harness,
                RootChildEndpoint,
                CreateRootChildBody(new(503, "upsert-missing", 999))
            ),
            await PostForProblemAsync(
                harness,
                RootChildEndpoint,
                CreateRootChildBody(new(503, "upsert-unrelated", 300))
            ),
            "SchoolId"
        );

        // AC1/AC3 on the optional, nonidentity StudentAssessment.reportedSchoolReference.
        AssertMissingAndUnrelatedAreIndistinguishable(
            await PostForProblemAsync(
                harness,
                StudentAssessmentsEndpoint,
                StudentAssessmentBody("SA-missing", 999)
            ),
            await PostForProblemAsync(
                harness,
                StudentAssessmentsEndpoint,
                StudentAssessmentBody("SA-unrelated", 300)
            ),
            "ReportedSchool"
        );

        // DS 5.2 unifies SchoolId from schoolReference and sessionReference. With School 999,
        // both contributors are unresolved; School 300 supplies a resolved, unrelated contributor.
        AssertMissingAndUnrelatedAreIndistinguishable(
            await PostForProblemAsync(harness, CourseOfferingsEndpoint, CourseOfferingBody(999)),
            await PostForProblemAsync(harness, CourseOfferingsEndpoint, CourseOfferingBody(300)),
            "SchoolId"
        );

        // A missing School equal to the claim must pass authorization and expose the deferred
        // reference conflict. Reclassifying a null denial alone cannot satisfy this assertion.
        using (
            HttpResponseMessage response = await PostJsonAsync(
                harness,
                CourseOfferingsEndpoint,
                CourseOfferingBody((int)ClaimEducationOrganizationId)
            )
        )
        {
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        }

        // AC2: an omitted or whole-null optional reference is genuine absence.
        AssertReportedSchoolRequired(
            await PostForProblemAsync(
                harness,
                StudentAssessmentsEndpoint,
                StudentAssessmentBody("SA-omitted", null)
            )
        );
        JsonObject explicitNullBody = StudentAssessmentBody("SA-null", null);
        explicitNullBody["reportedSchoolReference"] = null;
        AssertReportedSchoolRequired(
            await PostForProblemAsync(harness, StudentAssessmentsEndpoint, explicitNullBody)
        );

        // A present reference with a missing or null required identity leaf never reaches authorization.
        JsonObject emptyReferenceBody = StudentAssessmentBody("SA-empty-reference", null);
        emptyReferenceBody["reportedSchoolReference"] = new JsonObject();
        JsonObject nullLeafBody = StudentAssessmentBody("SA-null-leaf", null);
        nullLeafBody["reportedSchoolReference"] = new JsonObject { ["schoolId"] = null };
        foreach (JsonObject invalidBody in new[] { emptyReferenceBody, nullLeafBody })
        {
            using HttpResponseMessage response = await PostJsonAsync(
                harness,
                StudentAssessmentsEndpoint,
                invalidBody
            );
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        }

        // Earlier descriptor validation must not publish a separate missing document reference.
        List<JsonObject> descriptorProblems = [];
        foreach (int schoolId in new[] { 999, 300 })
        {
            JsonObject body = StudentAssessmentBody($"SA-descriptor-{schoolId}", schoolId);
            body["administrationLanguageDescriptor"] = "uri://ed-fi.org/LanguageDescriptor#missing-dms-1659";
            using HttpResponseMessage response = await PostJsonAsync(
                harness,
                StudentAssessmentsEndpoint,
                body
            );
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonObject problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            problem["validationErrors"]!
                .AsObject()
                .Select(entry => entry.Key)
                .Should()
                .Equal("$.administrationLanguageDescriptor");
            problem.Remove("correlationId");
            descriptorProblems.Add(problem);
        }
        JsonNode.DeepEquals(descriptorProblems[0], descriptorProblems[1]).Should().BeTrue();

        // Request isolation: alternate unresolved and absent requests that share one mapping and shape.
        List<string> observedTypes = [];
        int?[] alternation = [999, null, 999, null, 999, null];
        for (int index = 0; index < alternation.Length; index++)
        {
            JsonObject problem = await PostForProblemAsync(
                harness,
                StudentAssessmentsEndpoint,
                StudentAssessmentBody($"SA-alternate-{index}", alternation[index])
            );
            observedTypes.Add(problem["type"]!.GetValue<string>());
        }

        observedTypes
            .Should()
            .Equal(
                AuthorizationType,
                ProposedMissingType,
                AuthorizationType,
                ProposedMissingType,
                AuthorizationType,
                ProposedMissingType
            );
    }

    private static void AssertMissingAndUnrelatedAreIndistinguishable(
        JsonObject missingProblem,
        JsonObject unrelatedProblem,
        string readableName
    )
    {
        missingProblem["type"]!.GetValue<string>().Should().Be(AuthorizationType);
        missingProblem["title"]!.GetValue<string>().Should().Be("Authorization Denied");
        missingProblem["status"]!.GetValue<int>().Should().Be(403);
        missingProblem["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Access to the requested data could not be authorized.");
        missingProblem["validationErrors"]!.AsObject().Count.Should().Be(0);
        missingProblem["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(
                $"No relationships have been established between the caller's education organization id claim ('900') and the resource item's '{readableName}' value."
            );

        // Correlation ids are per request, so each is validated on its own and removed before comparing.
        foreach (JsonObject problem in new[] { missingProblem, unrelatedProblem })
        {
            problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            problem.Remove("correlationId");
        }

        JsonNode
            .DeepEquals(missingProblem, unrelatedProblem)
            .Should()
            .BeTrue(
                $"missing {missingProblem.ToJsonString()} and unrelated {unrelatedProblem.ToJsonString()} responses must match"
            );
    }

    private static void AssertReportedSchoolRequired(JsonObject problem)
    {
        problem["type"]!.GetValue<string>().Should().Be(ProposedMissingType);
        problem["detail"]!
            .GetValue<string>()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The 'ReportedSchool' value is required for authorization purposes."
            );
        problem["errors"]!.AsArray().Should().BeEmpty();
        problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    private static Task<JsonObject> PostForProblemAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body
    ) => SendForProblemAsync(harness, HttpMethod.Post, endpoint, body);

    /// <summary>Sends the request and returns its 403 problem+json body.</summary>
    private static async Task<JsonObject> SendForProblemAsync(
        ApiIntegrationHarness harness,
        HttpMethod method,
        string endpoint,
        JsonObject body
    )
    {
        using HttpResponseMessage response = await SendJsonAsync(harness, method, endpoint, body);
        string responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, responseBody);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        return JsonNode.Parse(responseBody)!.AsObject();
    }

    private static JsonObject WithId(JsonObject body, string resourceId)
    {
        body["id"] = resourceId;
        return body;
    }

    private static async Task SeedCourseOfferingDependenciesAsync(ApiIntegrationHarness harness)
    {
        await CreateDescriptorAsync(
            harness,
            "/data/ed-fi/courseIdentificationSystemDescriptors",
            "uri://ed-fi.org/CourseIdentificationSystemDescriptor",
            "LEA course code"
        );
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            "/data/ed-fi/courses",
            new JsonObject
            {
                ["courseCode"] = "DMS-1659",
                ["courseTitle"] = "Authorization regression",
                ["numberOfParts"] = 1,
                ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = 100 },
                ["identificationCodes"] = new JsonArray(
                    new JsonObject
                    {
                        ["identificationCode"] = "DMS-1659",
                        ["courseIdentificationSystemDescriptor"] =
                            "uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA course code",
                    }
                ),
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static JsonObject CourseOfferingBody(int schoolId) =>
        new()
        {
            ["localCourseCode"] = "DMS-1659",
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
            ["sessionReference"] = new JsonObject
            {
                ["schoolId"] = schoolId,
                ["schoolYear"] = 2025,
                ["sessionName"] = "Missing session",
            },
            ["courseReference"] = new JsonObject
            {
                ["courseCode"] = "DMS-1659",
                ["educationOrganizationId"] = 100,
            },
        };

    private static async Task SeedStudentAssessmentDependenciesAsync(ApiIntegrationHarness harness)
    {
        await CreateDescriptorAsync(
            harness,
            "/data/ed-fi/academicSubjectDescriptors",
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English Language Arts"
        );

        foreach (
            (string endpoint, JsonObject body) in new (string, JsonObject)[]
            {
                (
                    "/data/ed-fi/students",
                    new JsonObject
                    {
                        ["studentUniqueId"] = "S1",
                        ["firstName"] = "Ada",
                        ["lastSurname"] = "L",
                        ["birthDate"] = "2010-01-01",
                    }
                ),
                (
                    "/data/ed-fi/assessments",
                    new JsonObject
                    {
                        ["assessmentIdentifier"] = "A1",
                        ["namespace"] = "uri://ed-fi.org/Assessment",
                        ["assessmentTitle"] = "T",
                        ["academicSubjects"] = new JsonArray(
                            new JsonObject
                            {
                                ["academicSubjectDescriptor"] =
                                    "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts",
                            }
                        ),
                    }
                ),
            }
        )
        {
            using HttpResponseMessage response = await PostJsonAsync(harness, endpoint, body);
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// A StudentAssessment whose natural key is the identifier plus the existing student and assessment, so
    /// every variant creates a distinct document. A null <paramref name="reportedSchoolId"/> omits the
    /// optional reportedSchoolReference.
    /// </summary>
    private static JsonObject StudentAssessmentBody(string studentAssessmentIdentifier, int? reportedSchoolId)
    {
        JsonObject body = new()
        {
            ["studentAssessmentIdentifier"] = studentAssessmentIdentifier,
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = "S1" },
            ["assessmentReference"] = new JsonObject
            {
                ["assessmentIdentifier"] = "A1",
                ["namespace"] = "uri://ed-fi.org/Assessment",
            },
            ["eventDescription"] = "event",
        };

        if (reportedSchoolId is not null)
        {
            body["reportedSchoolReference"] = new JsonObject { ["schoolId"] = reportedSchoolId.Value };
        }

        return body;
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
