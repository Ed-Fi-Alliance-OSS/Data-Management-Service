// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Real-HTTP-pipeline coverage for the DMS-1373 application-context provider: per-request demand is
/// memoized to a single simulated CMS call, tenants resolve independent contexts, and the required-context
/// failure mapping (401 NotFound / 503 Unavailable) never discloses ownership or provider internals. Hard-coded
/// for the ProfileRootOnlyMerge fixture's Student shape, matching <see cref="CrudRoundTripScenario"/>.
/// </summary>
internal static class ApplicationContextIntegrationScenario
{
    private const string StudentsEndpointFormat = "/{0}/data/ed-fi/students";

    public static async Task It_resolves_application_context_at_most_once_per_request(
        ApiIntegrationHarness harness,
        RecordingConfigurationServiceApplicationProvider provider,
        string tenant
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            tenant,
            studentUniqueId: "app-context-call-count-001"
        );
        string body = await response.Content.ReadAsStringAsync();

        // The upsert pipeline demands application context three times - once from ProfileResolutionMiddleware
        // and twice from ApplicationContextRequirementMiddleware (once before, once after resource-action
        // authorization) - so a single simulated CMS call proves the scoped CachedApplicationContextProvider
        // memoized the first result for the rest of the request.
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        provider.Invocations.Should().ContainSingle(invocation => invocation.Tenant == tenant);
    }

    public static async Task It_resolves_independent_contexts_per_tenant(
        ApiIntegrationHarness harness,
        RecordingConfigurationServiceApplicationProvider provider,
        string firstTenant,
        string secondTenant
    )
    {
        using HttpResponseMessage firstResponse = await PostStudentAsync(
            harness,
            firstTenant,
            studentUniqueId: "app-context-tenant-isolation-001"
        );
        string firstBody = await firstResponse.Content.ReadAsStringAsync();
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created, firstBody);

        using HttpResponseMessage secondResponse = await PostStudentAsync(
            harness,
            secondTenant,
            studentUniqueId: "app-context-tenant-isolation-002"
        );
        string secondBody = await secondResponse.Content.ReadAsStringAsync();
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created, secondBody);

        // Each tenant must reach the simulated CMS independently: a shared cache key across tenants would
        // let the second request reuse the first tenant's cached success without ever calling the provider.
        provider.Invocations.Should().Contain(invocation => invocation.Tenant == firstTenant);
        provider.Invocations.Should().Contain(invocation => invocation.Tenant == secondTenant);
    }

    public static async Task It_maps_not_found_to_401_without_disclosure(
        ApiIntegrationHarness harness,
        string tenant
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            tenant,
            studentUniqueId: "app-context-not-found-001"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
        AssertNoApplicationContextDetailLeaked(body);
    }

    public static async Task It_maps_unavailable_to_503_without_disclosure(
        ApiIntegrationHarness harness,
        string tenant
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            tenant,
            studentUniqueId: "app-context-unavailable-001"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        AssertNoApplicationContextDetailLeaked(body);
    }

    private static async Task<HttpResponseMessage> PostStudentAsync(
        ApiIntegrationHarness harness,
        string tenant,
        string studentUniqueId
    )
    {
        JsonObject payload = new() { ["studentUniqueId"] = studentUniqueId, ["firstName"] = "Ada" };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            string.Format(StudentsEndpointFormat, tenant)
        )
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        return await harness.HttpClient.SendAsync(request);
    }

    /// <summary>
    /// The served body must never carry ownership token values, the internal result-type names, or
    /// provider/exception detail, regardless of which required-context failure produced it.
    /// </summary>
    private static void AssertNoApplicationContextDetailLeaked(string body)
    {
        string[] forbiddenFragments =
        [
            "ownershipToken",
            "OwnershipToken",
            "CreatorOwnershipTokenId",
            "ApplicationContextResult",
            "ApplicationContext.NotFound",
            "ApplicationContext.Unavailable",
            "Exception",
            "   at ",
        ];

        foreach (var fragment in forbiddenFragments)
        {
            body.Should()
                .NotContain(
                    fragment,
                    $"the served response must not leak application-context detail '{fragment}'"
                );
        }
    }
}
