// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Public-boundary coverage for NamespaceBased authorization: the exact ProblemDetails wire contracts from
/// <c>auth.md</c> §2.10–§2.12, the 403-over-412 collision ordering, and the sanitized 500 envelope a malformed
/// AUTH1 payload must produce. The provider-integration matrix lives in the backend suites; this scenario owns
/// only what those cannot observe — the served response body and the real JWT/claim-set prefix plumbing.
/// </summary>
internal static class NamespaceAuthorizationProblemDetailsScenario
{
    public const string AuthorizedPrefix = "uri://ns1.org/";
    public const string SecondAuthorizedPrefix = "uri://ns2.org/";
    private const string UnauthorizedNamespace = "uri://other.example/assessments";

    /// <summary>Configured prefixes as the failure formatter renders them: deduplicated and ordinal-sorted.</summary>
    public static IReadOnlyList<string> ConfiguredPrefixes { get; } =
    [AuthorizedPrefix, SecondAuthorizedPrefix];

    private const string NamespaceResourcesEndpoint = "/data/authz/authorizationNamespaceResources";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";

    private const int SchoolId = 100;

    private const string MismatchType =
        "urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch";
    private const string StoredUninitializedType =
        "urn:ed-fi:api:security:authorization:namespace:invalid-data:namespace-uninitialized";
    private const string ProposedRequiredType =
        "urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-required";
    private const string SecurityConfigurationType = "urn:ed-fi:api:system:configuration:security";

    private static readonly string[] _noFurtherAuthorizationRequiredStrategy =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];

    private static readonly string[] _namespaceBasedStrategy =
    [
        AuthorizationStrategyNameConstants.NamespaceBased,
    ];

    /// <summary>
    /// Rewrites the extracted AUTH1 payload to a namespace payload whose emitted index exceeds the planned
    /// checks, so it decodes as a namespace payload but cannot be mapped onto the plan. Applied only after the
    /// production authorization SQL has already raised a real provider exception.
    /// </summary>
    public static RelationshipAuthorizationProviderFailure ToUnmappablePayload(
        RelationshipAuthorizationProviderFailure providerFailure
    ) =>
        providerFailure with
        {
            Message =
                "Conversion failed when converting the varchar value 'AUTH1 - ns1|9|m' to data type int.",
        };

    /// <summary>
    /// Rewrites the extracted AUTH1 payload to a truncated <c>ns1|</c> payload that keeps the namespace
    /// discriminator but omits the required failure-kind field, so it cannot be parsed at all and dispatches as
    /// an invalid payload rather than as a namespace payload the plan cannot map. Applied only after the
    /// production authorization SQL has already raised a real provider exception.
    /// </summary>
    public static RelationshipAuthorizationProviderFailure ToMalformedPayload(
        RelationshipAuthorizationProviderFailure providerFailure
    ) =>
        providerFailure with
        {
            Message = "Conversion failed when converting the varchar value 'AUTH1 - ns1|0' to data type int.",
        };

    /// <summary>
    /// Reads, updates, and deletes use NamespaceBased while creates stay unauthorized-by-default, so a stored
    /// row with any namespace — including an omitted one — can be seeded before the denial is exercised.
    /// </summary>
    public static IClaimSetProvider CreateReadUpdateDeleteClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            create: _noFurtherAuthorizationRequiredStrategy,
            read: _namespaceBasedStrategy,
            update: _namespaceBasedStrategy,
            delete: _namespaceBasedStrategy
        );

    public static IClaimSetProvider CreateCreateClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            create: _namespaceBasedStrategy,
            read: _noFurtherAuthorizationRequiredStrategy,
            update: _noFurtherAuthorizationRequiredStrategy,
            delete: _noFurtherAuthorizationRequiredStrategy
        );

    public static async Task It_returns_namespace_mismatch_problem_details_for_get_by_id(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        var locationPath = await CreateNamespaceResourceAsync(harness, 101, UnauthorizedNamespace);

        using var response = await harness.HttpClient.GetAsync(locationPath);

        // §2.12 against stored data, so the detail includes the word "existing" and lists the caller's
        // prefixes in the order the formatter renders them.
        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Forbidden,
            MismatchType,
            "Authorization Denied",
            "Access to the requested data could not be authorized. The existing 'Namespace' value of the data "
                + $"does not start with any of the caller's associated namespace prefixes ('{AuthorizedPrefix}', "
                + $"'{SecondAuthorizedPrefix}').",
            expectedErrors: []
        );
    }

    public static async Task It_returns_stored_uninitialized_problem_details_for_get_by_id(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        var locationPath = await CreateNamespaceResourceAsync(harness, 102, storedNamespace: null);

        using var response = await harness.HttpClient.GetAsync(locationPath);

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Forbidden,
            StoredUninitializedType,
            "Authorization Denied",
            "Access to the requested data could not be authorized. The existing 'Namespace' value has not been "
                + "assigned but is required for authorization purposes.",
            expectedErrors:
            [
                "The existing resource item is inaccessible to clients using the 'NamespaceBased' authorization "
                    + "strategy because the 'Namespace' value has not been assigned.",
            ]
        );
    }

    public static async Task It_returns_proposed_namespace_required_problem_details_for_post_create(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);

        // The synthetic resource's namespace is optional, so an omitted value passes JSON schema validation and
        // reaches namespace authorization rather than being intercepted as a validation error.
        using var response = await PostJsonAsync(
            harness,
            NamespaceResourcesEndpoint,
            CreateNamespaceResourceBody(103, storedNamespace: null)
        );

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Forbidden,
            ProposedRequiredType,
            "Authorization Denied",
            "Access to the requested data could not be authorized. The 'Namespace' value has not been assigned "
                + "but is required for authorization purposes.",
            expectedErrors: []
        );
    }

    public static async Task It_returns_proposed_namespace_mismatch_problem_details_for_post_create(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);

        using var response = await PostJsonAsync(
            harness,
            NamespaceResourcesEndpoint,
            CreateNamespaceResourceBody(104, UnauthorizedNamespace)
        );

        // §2.12 against proposed data omits "existing", which is what distinguishes it from the stored-value
        // mismatch above.
        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Forbidden,
            MismatchType,
            "Authorization Denied",
            "Access to the requested data could not be authorized. The 'Namespace' value of the data does not "
                + $"start with any of the caller's associated namespace prefixes ('{AuthorizedPrefix}', "
                + $"'{SecondAuthorizedPrefix}').",
            expectedErrors: []
        );
    }

    public static async Task It_returns_403_rather_than_412_for_an_unauthorized_delete_with_a_stale_if_match(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        var locationPath = await CreateNamespaceResourceAsync(harness, 105, UnauthorizedNamespace);

        using var request = new HttpRequestMessage(HttpMethod.Delete, locationPath);
        request.Headers.TryAddWithoutValidation("If-Match", "\"stale-etag\"");
        using var response = await harness.HttpClient.SendAsync(request);

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Forbidden,
            MismatchType,
            "Authorization Denied",
            "Access to the requested data could not be authorized. The existing 'Namespace' value of the data "
                + $"does not start with any of the caller's associated namespace prefixes ('{AuthorizedPrefix}', "
                + $"'{SecondAuthorizedPrefix}').",
            expectedErrors: []
        );

        // The target must survive the denied delete.
        using var getResponse = await harness.HttpClient.GetAsync(locationPath);
        getResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    public static async Task It_returns_412_for_a_stale_delete_if_match_once_namespace_authorization_passes(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        var locationPath = await CreateNamespaceResourceAsync(harness, 106, AuthorizedPrefix + "assessments");

        using var request = new HttpRequestMessage(HttpMethod.Delete, locationPath);
        request.Headers.TryAddWithoutValidation("If-Match", "\"stale-etag\"");
        using var response = await harness.HttpClient.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.PreconditionFailed,
                $"the precondition result must survive once authorization succeeds, but got: {body}"
            );
    }

    public static async Task It_returns_a_sanitized_security_configuration_500_for_an_unmappable_payload(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        var locationPath = await CreateNamespaceResourceAsync(harness, 107, UnauthorizedNamespace);

        using var response = await harness.HttpClient.GetAsync(locationPath);

        // The canonical DMS-1099 envelope, in full.
        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.InternalServerError,
            SecurityConfigurationType,
            "Security Configuration Error",
            "A security configuration problem was detected. The request cannot be authorized.",
            expectedErrors:
            [
                "The namespace authorization failure payload returned by the authorization provider is invalid "
                    + "and cannot be mapped to the configured namespace authorization plan.",
            ]
        );

        // The payload was rewritten only after SQL Server itself raised the conversion failure that the
        // production compiler's AUTH1 cast provokes, so the sanitized response is proved to come from a real
        // provider exception rather than a stubbed one.
        var providerFailures =
            harness.ProviderFailureRecorder?.ProviderFailures
            ?? throw new InvalidOperationException(
                "The provider failure recorder must be enabled for this scenario."
            );
        providerFailures
            .Distinct()
            .Should()
            .ContainSingle("every exception filter must probe the one real provider exception");
        var sqlException = providerFailures[0].Should().BeOfType<SqlException>().Subject;
        sqlException.Number.Should().Be(245);
        sqlException.Message.Should().Contain("AUTH1 - ns1|0|m");
    }

    public static async Task It_returns_a_sanitized_security_configuration_500_for_a_malformed_payload(
        ApiIntegrationHarness harness
    )
    {
        await SeedReferenceDataAsync(harness);
        var locationPath = await CreateNamespaceResourceAsync(harness, 108, UnauthorizedNamespace);

        using var response = await harness.HttpClient.GetAsync(locationPath);

        // A payload that cannot be parsed at all maps to the same canonical envelope as one that parses but
        // cannot be mapped: only the withheld internal diagnostic differs.
        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.InternalServerError,
            SecurityConfigurationType,
            "Security Configuration Error",
            "A security configuration problem was detected. The request cannot be authorized.",
            expectedErrors:
            [
                "The namespace authorization failure payload returned by the authorization provider is invalid "
                    + "and cannot be mapped to the configured namespace authorization plan.",
            ]
        );

        // The internal diagnostic kind stays server-side; AssertProblemDetailsAsync already rejects the raw
        // 'AUTH1' / 'ns1|' payload fragments.
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("InvalidPayload").And.NotContain("NamespaceAuthorization.Auth1");

        // The payload was rewritten only after SQL Server itself raised the conversion failure that the
        // production compiler's AUTH1 cast provokes, so the sanitized response is proved to come from a real
        // provider exception rather than a stubbed one.
        var providerFailures =
            harness.ProviderFailureRecorder?.ProviderFailures
            ?? throw new InvalidOperationException(
                "The provider failure recorder must be enabled for this scenario."
            );
        providerFailures
            .Distinct()
            .Should()
            .ContainSingle("every exception filter must probe the one real provider exception");
        var sqlException = providerFailures[0].Should().BeOfType<SqlException>().Subject;
        sqlException.Number.Should().Be(245);
        sqlException.Message.Should().Contain("AUTH1 - ns1|0|m");
    }

    private static IClaimSetProvider CreateClaimSetProvider(
        FixtureContext fixture,
        IReadOnlyList<string> create,
        IReadOnlyList<string> read,
        IReadOnlyList<string> update,
        IReadOnlyList<string> delete
    ) =>
        new ConfigurableClaimSetProvider(
            fixture,
            (resource, action) =>
                !string.Equals(
                    resource.ResourceName,
                    "AuthorizationNamespaceResource",
                    StringComparison.Ordinal
                )
                    ? _noFurtherAuthorizationRequiredStrategy
                    : action switch
                    {
                        "Create" => create,
                        "Read" => read,
                        "Update" => update,
                        "Delete" => delete,
                        _ => _noFurtherAuthorizationRequiredStrategy,
                    }
        );

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
        await CreateSchoolAsync(harness);
    }

    private static async Task CreateDescriptorAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string @namespace,
        string codeValue
    )
    {
        using var response = await PostJsonAsync(
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

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task CreateSchoolAsync(ApiIntegrationHarness harness)
    {
        using var response = await PostJsonAsync(
            harness,
            SchoolsEndpoint,
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

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateNamespaceResourceAsync(
        ApiIntegrationHarness harness,
        int authorizationNamespaceId,
        string? storedNamespace
    )
    {
        using var response = await PostJsonAsync(
            harness,
            NamespaceResourcesEndpoint,
            CreateNamespaceResourceBody(authorizationNamespaceId, storedNamespace)
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.Headers.Location.Should().NotBeNull();

        return response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
    }

    private static JsonObject CreateNamespaceResourceBody(
        int authorizationNamespaceId,
        string? storedNamespace
    )
    {
        JsonObject body = new()
        {
            ["authorizationNamespaceId"] = authorizationNamespaceId,
            ["name"] = $"namespace-problem-details-{authorizationNamespaceId}",
            ["schoolReference"] = new JsonObject { ["schoolId"] = SchoolId },
            ["classPeriods"] = new JsonArray(),
        };

        if (storedNamespace is not null)
        {
            body["namespace"] = storedNamespace;
        }

        return body;
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

    private static async Task AssertProblemDetailsAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode,
        string expectedType,
        string expectedTitle,
        string expectedDetail,
        IReadOnlyList<string> expectedErrors
    )
    {
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatusCode, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be(expectedType);
        problem["title"]!.GetValue<string>().Should().Be(expectedTitle);
        problem["status"]!.GetValue<int>().Should().Be((int)expectedStatusCode);
        problem["detail"]!.GetValue<string>().Should().Be(expectedDetail);
        problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        problem["validationErrors"]!.AsObject().Count.Should().Be(0);
        problem["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(expectedErrors);

        AssertNoProviderDetailLeaked(body);
    }

    /// <summary>
    /// The served body must never carry provider or SQL internals, whether the response was produced from a
    /// decoded AUTH1 payload or from the sanitized security-configuration fallback.
    /// </summary>
    private static void AssertNoProviderDetailLeaked(string body)
    {
        string[] forbiddenFragments =
        [
            "AUTH1",
            "ns1|",
            "Conversion failed",
            "varchar",
            "SqlException",
            "Microsoft.Data.SqlClient",
            "CAST(",
            "SELECT ",
            "dms.",
            "authz.",
            "   at ",
        ];

        foreach (var fragment in forbiddenFragments)
        {
            body.Should()
                .NotContain(fragment, $"the served response must not leak provider detail '{fragment}'");
        }
    }
}
