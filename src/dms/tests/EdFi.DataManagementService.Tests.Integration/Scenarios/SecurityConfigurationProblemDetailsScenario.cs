// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class SecurityConfigurationProblemDetailsScenario
{
    private const string AuthorizationRootChildResourcesEndpoint =
        "/data/authz/authorizationRootChildResources";
    private const string AuthorizationNullableResourcesEndpoint =
        "/data/authz/authorizationNullableResources";
    private const string SchoolTypeDescriptorsEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    private const string UnknownStrategyName = "SecurityConfigurationUnknownStrategy";

    private static readonly string[] _noFurtherAuthorizationRequiredStrategy =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];

    public static IClaimSetProvider CreateEmptyClaimSetCatalogProvider() => new StaticClaimSetProvider([]);

    public static IClaimSetProvider CreateCreateOnlyRootChildClaimSetProvider() =>
        new StaticClaimSetProvider([
            new ClaimSet(
                ExternalDoublesConstants.SmokeClaimSetName,
                [
                    new ResourceClaim(
                        $"{Conventions.EdFiOdsResourceClaimBaseUri}/authz/authorizationrootchildresource",
                        "Create",
                        [
                            new AuthorizationStrategy(
                                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                            ),
                        ]
                    ),
                ]
            ),
        ]);

    public static IClaimSetProvider CreateNoStrategiesWriteClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (resource, action) =>
                IsResourceAction(resource, "Authz", "AuthorizationNullableResource", action, "Create")
                    ? []
                    : _noFurtherAuthorizationRequiredStrategy
        );

    public static IClaimSetProvider CreateUnknownStrategyResourceReadClaimSetProvider(
        FixtureContext fixture
    ) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (resource, action) =>
                IsResourceAction(resource, "Authz", "AuthorizationRootChildResource", action, "Read")
                    ? [UnknownStrategyName]
                    : _noFurtherAuthorizationRequiredStrategy
        );

    public static IClaimSetProvider CreateUnknownStrategyDescriptorReadClaimSetProvider(
        FixtureContext fixture
    ) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (resource, action) =>
                IsResourceAction(resource, "Ed-Fi", "SchoolTypeDescriptor", action, "Read")
                    ? [UnknownStrategyName]
                    : _noFurtherAuthorizationRequiredStrategy
        );

    public static async Task It_returns_missing_metadata_problem_details_for_resource_read(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            AuthorizationRootChildResourcesEndpoint
        );

        await AssertSecurityConfigurationProblemDetailsAsync(
            response,
            [SecurityConfigurationFailureMessages.MissingSecurityMetadata]
        );
    }

    public static async Task It_returns_no_strategies_problem_details_for_resource_write(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            AuthorizationNullableResourcesEndpoint,
            new JsonObject { ["authorizationNullableId"] = 1099, ["name"] = "no-strategies-write" }
        );

        await AssertSecurityConfigurationProblemDetailsAsync(
            response,
            [
                SecurityConfigurationFailureMessages.NoAuthorizationStrategies(
                    "Create",
                    [$"{Conventions.EdFiOdsResourceClaimBaseUri}/authz/authorizationnullableresource"],
                    $"{Conventions.EdFiOdsResourceClaimBaseUri}/authz/authorizationnullableresource"
                ),
            ]
        );
    }

    public static async Task It_returns_unknown_strategy_problem_details_for_resource_read(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            AuthorizationRootChildResourcesEndpoint
        );

        await AssertSecurityConfigurationProblemDetailsAsync(
            response,
            [SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([UnknownStrategyName])]
        );
    }

    public static async Task It_returns_unknown_strategy_problem_details_for_descriptor_read(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(SchoolTypeDescriptorsEndpoint);

        await AssertSecurityConfigurationProblemDetailsAsync(
            response,
            [SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([UnknownStrategyName])]
        );
    }

    public static async Task It_keeps_no_matching_resource_action_claim_as_forbidden(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            AuthorizationRootChildResourcesEndpoint
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:access-denied:action");
        problem["title"]!.GetValue<string>().Should().Be("Authorization Denied");
        problem["status"]!.GetValue<int>().Should().Be(403);
        problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        problem["validationErrors"]!.AsObject().Count.Should().Be(0);
        problem["errors"]!.AsArray().Count.Should().Be(1);
    }

    private static bool IsResourceAction(
        QualifiedResourceName resource,
        string projectName,
        string resourceName,
        string action,
        string expectedAction
    ) =>
        string.Equals(resource.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(resource.ResourceName, resourceName, StringComparison.Ordinal)
        && string.Equals(action, expectedAction, StringComparison.Ordinal);

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

    private static async Task AssertSecurityConfigurationProblemDetailsAsync(
        HttpResponseMessage response,
        IReadOnlyList<string> expectedErrors
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Type);
        problem["title"]!.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Title);
        problem["status"]!.GetValue<int>().Should().Be(SecurityConfigurationProblemDetails.Status);
        problem["detail"]!.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Detail);
        problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        problem["validationErrors"]!.AsObject().Count.Should().Be(0);
        problem["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(expectedErrors);
    }
}
