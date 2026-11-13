// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

/// <summary>
/// The education organization projection read the way the Configuration Service reader reads it: the
/// Discovery document, a token from the URL it advertises as <c>urls.oauth</c> (HTTP Basic client
/// credentials, <c>grant_type=client_credentials</c>, no scope), and the URL it advertises as
/// <c>urls.educationOrganizationProjection</c>. The projection-only claim set is imported through the
/// Configuration Service and removed after the scenario, with the application that uses it.
/// </summary>
public partial class StepDefinitions
{
    private const string ProjectionClaimName =
        "http://ed-fi.org/identity/claims/services/educationOrganizationProjection";
    private const string ProjectionContractVersion = "educationOrganizationProjection.v1";

    private static readonly HttpClient _projectionDmsClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.DmsPort}/"),
    };

    private readonly Dictionary<string, ClientCredentials> _projectionCredentials = new(
        StringComparer.Ordinal
    );
    private int _projectionTargetDataStoreId;
    private int? _projectionClaimSetId;
    private int? _projectionApplicationId;
    private Uri? _discoveredTokenUrl;
    private Uri? _discoveredProjectionUrl;
    private HttpStatusCode _projectionStatus;
    private string _projectionBody = "";

    [Given("a resource-only application on claim set {string} with its own data store")]
    public async Task GivenAResourceOnlyApplicationWithItsOwnDataStore(string claimSetName)
    {
        await AuthorizationDataProvider.CreateClientCredentials(
            Guid.NewGuid().ToString(),
            "Projection E2E",
            "projection-e2e@example.com",
            "uri://ed-fi.org",
            string.Empty,
            await SystemAdministrator.GetToken(),
            claimSetName
        );

        // The provider keeps only the most recent application, so this one is captured before the next.
        _projectionCredentials["resource-only"] = AuthorizationDataProvider.CurrentClientCredentials;
        _projectionTargetDataStoreId = AuthorizationDataProvider.CurrentDataStoreId;
    }

    [Given(
        "a projection-only application whose claim set grants only Read on the education organization projection"
    )]
    public async Task GivenAProjectionOnlyApplication()
    {
        string systemAdministratorToken = await SystemAdministrator.GetToken();
        string claimSetName = $"E2EProjection{Guid.NewGuid():N}"[..29];

        JsonObject import = new()
        {
            ["claimSetName"] = claimSetName,
            ["resourceClaims"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "educationOrganizationProjection",
                    ["claimName"] = ProjectionClaimName,
                    ["actions"] = new JsonArray(new JsonObject { ["name"] = "Read", ["enabled"] = true }),
                }
            ),
        };
        var (importStatus, importBody) = await AuthorizationDataProvider.SendToConfigurationService(
            HttpMethod.Post,
            "v3/claimSets/import",
            import.ToJsonString(),
            systemAdministratorToken
        );
        importStatus.Should().Be(HttpStatusCode.Created, importBody);

        var (listStatus, listBody) = await AuthorizationDataProvider.SendToConfigurationService(
            HttpMethod.Get,
            "v3/claimSets?offset=0&limit=1000",
            string.Empty,
            systemAdministratorToken
        );
        listStatus.Should().Be(HttpStatusCode.OK, listBody);
        _projectionClaimSetId = JsonNode
            .Parse(listBody)!
            .AsArray()
            .Single(claimSet => claimSet!["claimSetName"]?.GetValue<string>() == claimSetName)![
            "id"
        ]!.GetValue<int>();

        // CMS skips a resource claim missing from its stored hierarchy instead of rejecting the import, so
        // the grant is read back: without it a later 403 would be a provisioning failure, not a denial.
        var (exportStatus, exportBody) = await AuthorizationDataProvider.SendToConfigurationService(
            HttpMethod.Get,
            $"v3/claimSets/{_projectionClaimSetId}/export",
            string.Empty,
            systemAdministratorToken
        );
        exportStatus.Should().Be(HttpStatusCode.OK, exportBody);
        string[] grants =
        [
            .. JsonNode.Parse(exportBody)!["resourceClaims"]!
                .AsArray()
                .SelectMany(claim =>
                    claim!["actions"]!
                        .AsArray()
                        .Where(action => action!["enabled"]!.GetValue<bool>())
                        .Select(action =>
                            $"{claim["claimName"]!.GetValue<string>()}#{action!["name"]!.GetValue<string>()}"
                        )
                ),
        ];
        grants
            .Should()
            .Equal([$"{ProjectionClaimName}#Read"], $"claim set {claimSetName} must grant only Read");

        // DMS reads claim sets from CMS; it must know this one before a token for it is used.
        using HttpRequestMessage reload = new(HttpMethod.Post, "management/reload-claimsets")
        {
            Content = new StringContent("{}"),
        };
        reload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", systemAdministratorToken);
        using HttpResponseMessage reloaded = await _projectionDmsClient.SendAsync(reload);
        reloaded.StatusCode.Should().Be(HttpStatusCode.OK, "DMS must reload the imported claim set");

        await AuthorizationDataProvider.CreateClientCredentials(
            Guid.NewGuid().ToString(),
            "Projection E2E",
            "projection-e2e@example.com",
            "uri://ed-fi.org",
            string.Empty,
            systemAdministratorToken,
            claimSetName
        );
        _projectionCredentials["projection-only"] = AuthorizationDataProvider.CurrentClientCredentials;
        _projectionApplicationId = AuthorizationDataProvider.CurrentApplicationId;
    }

    [When("the Discovery document is read")]
    public async Task WhenTheDiscoveryDocumentIsRead()
    {
        using HttpResponseMessage response = await _projectionDmsClient.GetAsync("/");
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        JsonNode urls = JsonNode.Parse(body)!["urls"]!;
        _discoveredTokenUrl = new Uri(urls["oauth"]!.GetValue<string>());
        _discoveredProjectionUrl = new Uri(urls["educationOrganizationProjection"]!.GetValue<string>());
    }

    [When(
        "the {string} application reads the projection of the resource-only application's data store through the discovered URLs"
    )]
    public async Task WhenTheApplicationReadsTheProjectionThroughTheDiscoveredUrls(string application)
    {
        _discoveredTokenUrl.Should().NotBeNull("the Discovery document is read first");
        _projectionCredentials.Should().ContainKey(application);

        // Basic with the percent-encoded id and secret, grant_type=client_credentials and no scope.
        string token = await AuthorizationDataProvider.GetToken(
            _discoveredTokenUrl!.ToString(),
            _projectionCredentials[application]
        );

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"{_discoveredProjectionUrl}?dataStoreId={_projectionTargetDataStoreId}"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await _projectionDmsClient.SendAsync(request);
        _projectionStatus = response.StatusCode;
        _projectionBody = await response.Content.ReadAsStringAsync();
    }

    [Then("the projection read responds with 200 and a projection page of that data store")]
    public void ThenTheProjectionReadRespondsWithAPage()
    {
        _projectionStatus.Should().Be(HttpStatusCode.OK, _projectionBody);
        JsonNode page = JsonNode.Parse(_projectionBody)!;
        page["contractVersion"]!.GetValue<string>().Should().Be(ProjectionContractVersion);
        page["dataStoreId"]!.GetValue<long>().Should().Be(_projectionTargetDataStoreId);
        page["items"].Should().BeOfType<JsonArray>();
    }

    [Then("the projection read responds with 403 and no items")]
    public void ThenTheProjectionReadRespondsWith403()
    {
        _projectionStatus.Should().Be(HttpStatusCode.Forbidden, _projectionBody);
        JsonNode problem = JsonNode.Parse(_projectionBody)!;
        problem["status"]!.GetValue<int>().Should().Be(403);
        problem["items"].Should().BeNull();
    }

    [AfterScenario("EducationOrganizationProjection")]
    public async Task RemoveTheProjectionClaimSetAsync()
    {
        string systemAdministratorToken = await SystemAdministrator.GetToken();

        // The application first: CMS keeps a claim set an application still names.
        if (_projectionApplicationId is { } applicationId)
        {
            var (status, body) = await AuthorizationDataProvider.SendToConfigurationService(
                HttpMethod.Delete,
                $"v3/applications/{applicationId}",
                string.Empty,
                systemAdministratorToken
            );
            status.Should().BeOneOf([HttpStatusCode.NoContent, HttpStatusCode.OK], body);
        }

        if (_projectionClaimSetId is { } claimSetId)
        {
            var (status, body) = await AuthorizationDataProvider.SendToConfigurationService(
                HttpMethod.Delete,
                $"v3/claimSets/{claimSetId}",
                string.Empty,
                systemAdministratorToken
            );
            status.Should().BeOneOf([HttpStatusCode.NoContent, HttpStatusCode.OK], body);
        }
    }
}
