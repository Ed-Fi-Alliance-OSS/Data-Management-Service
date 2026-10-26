// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

/// <summary>
/// API clients narrowed to some of their Application's education organizations, created through the
/// Configuration Service, whose real tokens DMS authorizes. The Application is the one the last
/// "is authorized with educationOrganizationIds" step created, unless a scenario creates one
/// without default credentials.
/// </summary>
public partial class StepDefinitions
{
    private const string ScopedApplicationKey = "scopedApplicationId";
    private const string ScopedApplicationNameKey = "scopedApplicationName";
    private const string ScopedClaimSetKey = "scopedClaimSetName";

    private readonly Dictionary<string, ClientCredentials> _scopedClients = [];

    private int ScopedApplicationId =>
        _scenarioContext.TryGetValue(ScopedApplicationKey, out object? id) && id is int scopedId
            ? scopedId
            : AuthorizationDataProvider.CurrentApplicationId;

    [Given(
        "an Application without default credentials for claimSet {string} with educationOrganizationIds {string}"
    )]
    public async Task GivenAnApplicationWithoutDefaultCredentials(
        string claimSetName,
        string educationOrganizationIds
    )
    {
        string name = $"E2E Scoped {Guid.NewGuid():N}";
        var (status, body) = await AuthorizationDataProvider.SendToConfigurationService(
            HttpMethod.Post,
            "v3/applications",
            $$"""
            {
              "vendorId": {{AuthorizationDataProvider.CurrentVendorId}},
              "applicationName": "{{name}}",
              "claimSetName": "{{claimSetName}}",
              "educationOrganizationIds": [{{educationOrganizationIds}}],
              "createDefaultCredentials": false
            }
            """,
            await SystemAdministrator.GetToken()
        );
        status.Should().Be(HttpStatusCode.Created, body);

        _scenarioContext[ScopedApplicationKey] = JsonDocument
            .Parse(body)
            .RootElement.GetProperty("id")
            .GetInt32();
        _scenarioContext[ScopedApplicationNameKey] = name;
        _scenarioContext[ScopedClaimSetKey] = claimSetName;
    }

    [Given("API client {string} narrowed to educationOrganizationIds {string} is created on the Application")]
    public async Task GivenANarrowedApiClientIsCreated(string clientName, string educationOrganizationIds)
    {
        _scopedClients[clientName] = await AuthorizationDataProvider.CreateApiClient(
            ScopedApplicationId,
            $"\"educationOrganizationIds\": [{educationOrganizationIds}]",
            await SystemAdministrator.GetToken()
        );
    }

    /// <summary>
    /// Saves the Application a scenario created without default credentials with a new list,
    /// through the Application update that refreshes the Application's first client.
    /// </summary>
    [When("the Application's educationOrganizationIds are changed to {string}")]
    public async Task WhenTheApplicationEducationOrganizationIdsAreChanged(string educationOrganizationIds)
    {
        var (status, body) = await AuthorizationDataProvider.SendToConfigurationService(
            HttpMethod.Put,
            $"v3/applications/{ScopedApplicationId}",
            $$"""
            {
              "id": {{ScopedApplicationId}},
              "vendorId": {{AuthorizationDataProvider.CurrentVendorId}},
              "applicationName": "{{_scenarioContext[ScopedApplicationNameKey]}}",
              "claimSetName": "{{_scenarioContext[ScopedClaimSetKey]}}",
              "educationOrganizationIds": [{{educationOrganizationIds}}]
            }
            """,
            await SystemAdministrator.GetToken()
        );
        status.Should().Be(HttpStatusCode.NoContent, body);
    }

    [When("the token of API client {string} is used")]
    public async Task WhenTheTokenOfApiClientIsUsed(string clientName)
    {
        _scenarioContext["dmsToken"] =
            $"Bearer {await AuthorizationDataProvider.GetToken("oauth/token", _scopedClients[clientName])}";
    }

    [Then("the current token carries exactly the education organizations {string}")]
    public void ThenTheCurrentTokenCarriesExactlyTheEducationOrganizations(string educationOrganizationIds)
    {
        EducationOrganizationIdClaims().Should().Equal(educationOrganizationIds);
    }

    /// <summary>
    /// The token states an empty scope the way its identity provider does: the self-contained
    /// provider issues one empty educationOrganizationIds claim and Keycloak issues none. DMS reads
    /// both as no education organization access.
    /// </summary>
    [Then("the current token carries no education organizations")]
    public void ThenTheCurrentTokenCarriesNoEducationOrganizations()
    {
        string[] claims = EducationOrganizationIdClaims();
        if (
            string.Equals(
                Environment.GetEnvironmentVariable("CONFIGURATION_SERVICE_IDENTITY_PROVIDER"),
                "keycloak",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            claims.Should().BeEmpty("Keycloak issues no educationOrganizationIds claim for an empty scope");
        }
        else
        {
            claims
                .Should()
                .ContainSingle("the self-contained provider issues one claim")
                .Which.Should()
                .BeEmpty();
        }
    }

    private string[] EducationOrganizationIdClaims() =>
        [
            .. new JwtSecurityTokenHandler()
                .ReadJwtToken(GetDmsTokenFromContext().Replace("Bearer ", string.Empty))
                .Claims.Where(claim => claim.Type == "educationOrganizationIds")
                .Select(claim => claim.Value),
        ];
}
