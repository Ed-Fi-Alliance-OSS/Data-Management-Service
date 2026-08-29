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
    private const string ProfiledStudentWritableContentType =
        "application/vnd.ed-fi.student.testprofile.writable+json";

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

    public static async Task It_fails_closed_for_profiled_invalid_puts_before_document_validation(
        ApiIntegrationHarness harness,
        string notFoundTenant,
        string unavailableTenant
    )
    {
        await AssertProfiledInvalidPutFailsClosedAsync(harness, notFoundTenant, HttpStatusCode.Unauthorized);
        await AssertProfiledInvalidPutFailsClosedAsync(
            harness,
            unavailableTenant,
            HttpStatusCode.ServiceUnavailable
        );
    }

    /// <summary>
    /// The delete pipeline carries no ProfileResolutionMiddleware, so the only application-context demand a
    /// DELETE raises is ApplicationContextRequirementMiddleware's, and it is raised only after
    /// ResourceActionAuthorizationMiddleware has selected OwnershipBased for the action. On a tenant no other
    /// scenario touches, that single recorded lookup is the ownership gate itself and the 401 is the gate
    /// failing closed - drop the gate from the delete pipeline and the lookup count falls to zero.
    /// </summary>
    public static async Task It_requires_application_context_at_the_ownership_gate_for_delete(
        ApiIntegrationHarness harness,
        RecordingConfigurationServiceApplicationProvider provider,
        string tenant
    )
    {
        string resourcePath = $"{string.Format(StudentsEndpointFormat, tenant)}/{Guid.NewGuid()}";

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(resourcePath);
        string deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        AssertRequiredContextNotFound(deleteResponse, deleteBody);

        provider
            .Invocations.Where(invocation => invocation.Tenant == tenant)
            .Should()
            .ContainSingle()
            .Which.ClientId.Should()
            .Be(ExternalDoublesConstants.SmokeClientId);
    }

    /// <summary>
    /// Ownership-authorized GET and PUT require application context too, but their pipelines resolve profiles
    /// first: ProfileResolutionMiddleware raises the request's first demand and fails closed on it before any
    /// strategy has been selected, and the scoped CachedApplicationContextProvider memoizes that outcome for
    /// the rest of the request. What a served response can prove for those two methods is therefore the
    /// requirement itself - context is resolved before either is served, a request that cannot resolve it is
    /// refused without disclosure, and a request that resolves it runs on past every demand - not which
    /// middleware raised the demand. That the gate still follows strategy selection on both pipelines is held by
    /// PipelineOrderingTests.It_places_the_application_context_gate_immediately_after_strategy_selection, and
    /// its ownership branch by
    /// ApplicationContextRequirementMiddlewareTests.It_requires_application_context_for_OwnershipBased_resource_actions.
    /// </summary>
    public static async Task It_requires_application_context_for_ownership_authorized_gets_and_puts(
        ApiIntegrationHarness harness,
        RecordingConfigurationServiceApplicationProvider provider,
        string notFoundTenant,
        string successTenant
    )
    {
        string unresolvableId = Guid.NewGuid().ToString();
        string unresolvablePath = $"{string.Format(StudentsEndpointFormat, notFoundTenant)}/{unresolvableId}";

        using HttpResponseMessage unresolvableGet = await harness.HttpClient.GetAsync(unresolvablePath);
        string unresolvableGetBody = await unresolvableGet.Content.ReadAsStringAsync();
        AssertRequiredContextNotFound(unresolvableGet, unresolvableGetBody);

        using HttpResponseMessage unresolvablePut = await PutStudentAsync(
            harness,
            unresolvablePath,
            unresolvableId,
            studentUniqueId: "app-context-ownership-update-001"
        );
        string unresolvablePutBody = await unresolvablePut.Content.ReadAsStringAsync();
        AssertRequiredContextNotFound(unresolvablePut, unresolvablePutBody);

        (string ClientId, string? Tenant)[] unresolvableInvocations =
        [
            .. provider.Invocations.Where(invocation => invocation.Tenant == notFoundTenant),
        ];
        unresolvableInvocations.Should().HaveCount(2);
        unresolvableInvocations
            .Should()
            .OnlyContain(invocation => invocation.ClientId == ExternalDoublesConstants.SmokeClientId);

        // The same two requests against a tenant the provider resolves must clear every application-context
        // demand the pipeline raises - profile resolution's and the ownership gate's - so neither may answer
        // with either required-context failure. One simulated CMS call covers both requests here, against one
        // per request above: only a resolved context is cacheable, so the failures re-ask on every request
        // while the success is served to the second request from the cache the first one filled.
        string resolvableId = Guid.NewGuid().ToString();
        string resolvablePath = $"{string.Format(StudentsEndpointFormat, successTenant)}/{resolvableId}";

        using HttpResponseMessage resolvableGet = await harness.HttpClient.GetAsync(resolvablePath);
        string resolvableGetBody = await resolvableGet.Content.ReadAsStringAsync();
        AssertRequiredContextSatisfied(resolvableGet, resolvableGetBody);

        using HttpResponseMessage resolvablePut = await PutStudentAsync(
            harness,
            resolvablePath,
            resolvableId,
            studentUniqueId: "app-context-ownership-success-001"
        );
        string resolvablePutBody = await resolvablePut.Content.ReadAsStringAsync();
        AssertRequiredContextSatisfied(resolvablePut, resolvablePutBody);

        provider
            .Invocations.Where(invocation => invocation.Tenant == successTenant)
            .Should()
            .ContainSingle()
            .Which.ClientId.Should()
            .Be(ExternalDoublesConstants.SmokeClientId);
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

    private static async Task<HttpResponseMessage> PutStudentAsync(
        ApiIntegrationHarness harness,
        string resourcePath,
        string resourceId,
        string studentUniqueId
    )
    {
        JsonObject payload = new()
        {
            ["id"] = resourceId,
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = "Ada",
        };

        using var request = new HttpRequestMessage(HttpMethod.Put, resourcePath)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task AssertProfiledInvalidPutFailsClosedAsync(
        ApiIntegrationHarness harness,
        string tenant,
        HttpStatusCode expectedStatusCode
    )
    {
        string resourcePath = $"{string.Format(StudentsEndpointFormat, tenant)}/{Guid.NewGuid()}";
        using var request = new HttpRequestMessage(HttpMethod.Put, resourcePath)
        {
            Content = new StringContent("{}", Encoding.UTF8, ProfiledStudentWritableContentType),
        };
        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatusCode, body);
        if (expectedStatusCode == HttpStatusCode.Unauthorized)
        {
            response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
        }

        AssertNoApplicationContextDetailLeaked(body);
    }

    private static void AssertRequiredContextNotFound(HttpResponseMessage response, string body)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
        AssertNoApplicationContextDetailLeaked(body);
    }

    /// <summary>
    /// Asserts a request cleared every application-context demand rather than being turned back by one. The
    /// served status is deliberately not pinned: what follows the last demand is the resource's own outcome,
    /// which belongs to the authorization and handler tests, while the two required-context failures are the
    /// only answers this scenario is entitled to rule out.
    /// </summary>
    private static void AssertRequiredContextSatisfied(HttpResponseMessage response, string body)
    {
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, body);
        response.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable, body);
        response.Headers.WwwAuthenticate.ToString().Should().NotContain("invalid_token");
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
