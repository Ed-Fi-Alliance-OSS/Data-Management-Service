// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Write-side <c>If-None-Match</c> create-guard: POSTs/PUTs a Student against the real DMS HTTP
/// pipeline with various <c>If-None-Match</c> request headers, asserting the create-only semantics
/// (RFC 9110 §13.1.2 <c>If-None-Match: *</c> succeeds only when no current representation exists). Hard-coded
/// for the ProfileRootOnlyMerge fixture's Student shape: project endpoint <c>ed-fi</c>, resource
/// <c>students</c>, required identity <c>studentUniqueId</c>, required non-identity <c>firstName</c>.
/// Each scenario uses a fresh, per-call unique <c>studentUniqueId</c> to avoid cross-test collisions
/// within a shared leased database.
/// </summary>
internal static class WriteCreateGuardIfNoneMatchScenario
{
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string IfNoneMatchHeaderName = "If-None-Match";
    private const string IfMatchHeaderName = "If-Match";

    // A syntactically valid but never-created resource id; RFC 4122 version 4 shaped so it survives
    // any UUID-format validation while remaining guaranteed absent from a freshly leased database.
    private const string NonExistentResourceId = "00000000-0000-4000-a000-000000000000";

    public static async Task It_permits_a_post_insert_under_a_wildcard_if_none_match(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("ins-wc");

        using var request = new HttpRequestMessage(HttpMethod.Post, StudentsEndpoint)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada"),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                $"If-None-Match: * against a brand-new resource is the create-only success case. Body: {body}"
            );
        response.Headers.Location.Should().NotBeNull();
        response.TryReadRawEtag(out _).Should().BeTrue("a successful POST create must emit an ETag header");
    }

    public static async Task It_rejects_a_post_upsert_to_an_existing_document_under_a_wildcard_if_none_match(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pup-wc");
        await CreateStudentAsync(harness, studentUniqueId, "Ada");

        using var request = new HttpRequestMessage(HttpMethod.Post, StudentsEndpoint)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-changed"),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.PreconditionFailed,
                $"a POST that resolves to an existing document under If-None-Match: * must 412, not silently upsert. Body: {body}"
            );
        AssertIfNoneMatchPreconditionFailed(body);
    }

    public static async Task It_rejects_an_existing_put_under_a_wildcard_if_none_match(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pwe-wc");
        (string locationPath, _) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.PreconditionFailed,
                $"PUT to an existing target under If-None-Match: * must 412 (the target already has a representation). Body: {body}"
            );
        AssertIfNoneMatchPreconditionFailed(body);
    }

    public static async Task It_returns_not_found_for_a_missing_put_under_a_wildcard_if_none_match(
        ApiIntegrationHarness harness
    )
    {
        string putPath = $"{StudentsEndpoint}/{NonExistentResourceId}";
        string studentUniqueId = UniqueStudentId("pwm-wc");

        using var request = new HttpRequestMessage(HttpMethod.Put, putPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada", NonExistentResourceId),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NotFound,
                $"PUT to a genuinely missing target under If-None-Match: * is the success case for the create-guard, so it must fall through to the normal 404 rather than 412. Body: {body}"
            );
    }

    public static async Task It_rejects_an_existing_put_under_a_matching_specific_if_none_match(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pws-sp");
        (string locationPath, string etag) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var matchingRequest = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        matchingRequest.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, $"\"{etag}\"");

        using HttpResponseMessage matchingResponse = await harness.HttpClient.SendAsync(matchingRequest);
        string matchingBody = await matchingResponse.Content.ReadAsStringAsync();
        matchingResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.PreconditionFailed,
                $"a specific If-None-Match tag that matches the current ETag must 412. Body: {matchingBody}"
            );
        AssertIfNoneMatchPreconditionFailed(matchingBody);

        using var nonMatchingRequest = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed-again", resourceId),
        };
        // A stale, non-matching tag: the client's copy no longer reflects the current representation,
        // so If-None-Match is satisfied and the write proceeds normally.
        nonMatchingRequest.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "\"1-00000000.j._.n\"");

        using HttpResponseMessage nonMatchingResponse = await harness.HttpClient.SendAsync(
            nonMatchingRequest
        );
        string nonMatchingBody = await nonMatchingResponse.Content.ReadAsStringAsync();
        nonMatchingResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"a non-matching (stale) If-None-Match tag must be satisfied and let the PUT succeed. Body: {nonMatchingBody}"
            );
    }

    public static async Task It_prefers_if_match_when_both_headers_are_present(ApiIntegrationHarness harness)
    {
        string studentUniqueId = UniqueStudentId("ifm-pr");
        (string locationPath, string etag) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        // If-Match is correct (matches the current ETag); If-None-Match: * would 412 an existing
        // target if it were evaluated. If-Match governing must let this PUT succeed.
        request.Headers.TryAddWithoutValidation(IfMatchHeaderName, $"\"{etag}\"");
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"when both headers are present, If-Match must govern; a satisfied If-Match must not be overridden by a would-be-failing If-None-Match. Body: {body}"
            );
    }

    /// <summary>
    /// Exercises the deferred (post-proposed-authorization) precondition branch in
    /// <c>DefaultRelationalWriteExecutor</c>: a PUT against a resource whose Update action requires
    /// <c>RelationshipsWithEdOrgsOnly</c> authorization defers the etag precondition check until after
    /// proposed-value authorization succeeds. This proves the create-guard still 412s on that route
    /// (backed at the unit level by
    /// <c>It_returns_precondition_failure_on_the_deferred_path_for_an_existing_put_under_if_none_match</c>).
    /// </summary>
    public static async Task It_returns_precondition_failure_on_the_deferred_path_for_an_existing_put_under_a_wildcard_if_none_match(
        ApiIntegrationHarness harness
    )
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        // Derived deterministically from the suffix's own hex value (not string.GetHashCode(), which
        // is process-randomized) and kept well clear of any int32 boundary.
        long schoolId = 1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string namespaceUri = $"uri://ed-fi.org/WcgDeferred/{suffix}";

        await SeedAuthorizedSchoolAsync(harness, schoolId, namespaceUri);

        string locationPath = await CreateRootChildAsync(harness, 1, $"deferred-{suffix}", schoolId);
        string resourceId = GetResourceId(locationPath);

        // The proposed schoolId is unchanged (still authorized), so proposed-relationship
        // authorization succeeds and the write falls through to the deferred precondition check --
        // the only route that exercises TryBuildDeferredPreconditionFailureResult end to end.
        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateRootChildContent(1, $"deferred-{suffix}-updated", schoolId, resourceId),
        };
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, "*");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.PreconditionFailed,
                $"the deferred (post-proposed-authorization) branch must still honor If-None-Match: * against an existing target. Body: {body}"
            );
    }

    public static async Task It_rejects_an_existing_put_when_a_matching_tag_is_in_a_list(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pws-list");
        (string locationPath, string etag) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        // RFC 9110 §13.1.2: a list precondition fails the write (412) when ANY member matches the
        // current representation. The matching tag is placed among stale tags to prove list iteration.
        request.Headers.TryAddWithoutValidation(IfNoneMatchHeaderName, $"\"1-00000000.j._.n\", \"{etag}\"");

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.PreconditionFailed,
                $"a list containing the current tag must 412, even when other list members are stale. Body: {body}"
            );
    }

    public static async Task It_permits_an_existing_put_when_no_tag_in_a_list_matches(
        ApiIntegrationHarness harness
    )
    {
        string studentUniqueId = UniqueStudentId("pwm-list");
        (string locationPath, _) = await CreateStudentAsync(harness, studentUniqueId, "Ada");
        string resourceId = GetResourceId(locationPath);

        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateStudentContent(studentUniqueId, "Ada-renamed", resourceId),
        };
        // All members are stale, so If-None-Match is satisfied and the write proceeds normally.
        request.Headers.TryAddWithoutValidation(
            IfNoneMatchHeaderName,
            "\"1-00000000.j._.n\", \"2-11111111.j._.n\""
        );

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"a list in which no member matches must be satisfied and let the PUT succeed. Body: {body}"
            );
    }

    private static void AssertIfNoneMatchPreconditionFailed(string responseBody)
    {
        JsonObject problem = JsonNode.Parse(responseBody)!.AsObject();

        problem
            .Select(static property => property.Key)
            .Should()
            .BeEquivalentTo(
                "detail",
                "type",
                "title",
                "status",
                "correlationId",
                "validationErrors",
                "errors"
            );
        problem["detail"]!
            .GetValue<string>()
            .Should()
            .Be(
                "The If-None-Match precondition failed because a current representation of the resource matched the request header."
            );
        problem["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:precondition-failed:if-none-match");
        problem["title"]!.GetValue<string>().Should().Be("If-None-Match Precondition Failed");
        problem["status"]!.GetValue<int>().Should().Be(412);
        problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        problem["validationErrors"]!.AsObject().Should().BeEmpty();
        problem["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(
                "The 'If-None-Match' request header requires that no current representation match the supplied value, but a matching representation exists."
            );
    }

    private static async Task<(string locationPath, string etag)> CreateStudentAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        string firstName
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            CreateStudentContent(studentUniqueId, firstName)
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.TryReadRawEtag(out string etag).Should().BeTrue("the initial POST must emit an ETag header");

        string locationPath = response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location!.AbsolutePath
            : response.Headers.Location!.OriginalString;

        return (locationPath, etag);
    }

    private static StringContent CreateStudentContent(
        string studentUniqueId,
        string firstName,
        string? id = null
    )
    {
        var payload = new JsonObject { ["studentUniqueId"] = studentUniqueId, ["firstName"] = firstName };
        if (id is not null)
        {
            payload["id"] = id;
        }
        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private static string UniqueStudentId(string scenario) => $"wcg-{scenario}-{Guid.NewGuid():N}"[..24];

    private static string GetResourceId(string locationPath) =>
        locationPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    // --- Deferred-path seeding: mirrors RelationshipAuthorizationProblemDetailsScenario's
    // authorizationRootChildResources seeding, kept self-contained here so this file has no
    // dependency on that scenario's private members. ---

    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string RootChildEndpoint = "/data/authz/authorizationRootChildResources";

    private static async Task SeedAuthorizedSchoolAsync(
        ApiIntegrationHarness harness,
        long schoolId,
        string namespaceUri
    )
    {
        await CreateDescriptorAsync(
            harness,
            EducationOrganizationCategoryDescriptorsEndpoint,
            $"{namespaceUri}/EducationOrganizationCategoryDescriptor",
            "School"
        );
        await CreateDescriptorAsync(
            harness,
            GradeLevelDescriptorsEndpoint,
            $"{namespaceUri}/GradeLevelDescriptor",
            "Tenth grade"
        );

        var schoolPayload = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = $"Wcg-Deferred-School-{schoolId}",
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        $"{namespaceUri}/EducationOrganizationCategoryDescriptor#School",
                }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject
                {
                    ["gradeLevelDescriptor"] = $"{namespaceUri}/GradeLevelDescriptor#Tenth grade",
                }
            ),
        };
        using var schoolContent = new StringContent(
            schoolPayload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage schoolResponse = await harness.HttpClient.PostAsync(
            SchoolsEndpoint,
            schoolContent
        );
        string schoolBody = await schoolResponse.Content.ReadAsStringAsync();
        schoolResponse.StatusCode.Should().Be(HttpStatusCode.Created, schoolBody);

        await InsertAuthEdgeAsync(
            harness,
            RelationshipAuthorizationProblemDetailsScenario.ClaimEducationOrganizationId,
            schoolId
        );
    }

    private static async Task CreateDescriptorAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string @namespace,
        string codeValue
    )
    {
        var payload = new JsonObject
        {
            ["codeValue"] = codeValue,
            ["description"] = codeValue,
            ["namespace"] = @namespace,
            ["shortDescription"] = codeValue,
        };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task<string> CreateRootChildAsync(
        ApiIntegrationHarness harness,
        int authorizationRootChildId,
        string name,
        long schoolId
    )
    {
        using var content = CreateRootChildContent(authorizationRootChildId, name, schoolId, id: null);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(RootChildEndpoint, content);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        response.Headers.Location.Should().NotBeNull();
        return response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
    }

    private static StringContent CreateRootChildContent(
        int authorizationRootChildId,
        string name,
        long schoolId,
        string? id
    )
    {
        var payload = new JsonObject
        {
            ["authorizationRootChildId"] = authorizationRootChildId,
            ["name"] = name,
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
            ["classPeriods"] = new JsonArray(),
        };
        if (id is not null)
        {
            payload["id"] = id;
        }
        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
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

        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;

        DbParameter sourceParameter = command.CreateParameter();
        sourceParameter.ParameterName = "@sourceEducationOrganizationId";
        sourceParameter.Value = sourceEducationOrganizationId;
        command.Parameters.Add(sourceParameter);

        DbParameter targetParameter = command.CreateParameter();
        targetParameter.ParameterName = "@targetEducationOrganizationId";
        targetParameter.Value = targetEducationOrganizationId;
        command.Parameters.Add(targetParameter);

        await command.ExecuteNonQueryAsync();
    }

    private static bool IsMssql(DbConnection connection)
    {
        string? fullName = connection.GetType().FullName;
        return fullName is not null && fullName.Contains("SqlClient", StringComparison.Ordinal);
    }
}
