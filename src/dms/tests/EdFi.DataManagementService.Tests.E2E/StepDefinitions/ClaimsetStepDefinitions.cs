// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions
{
    [Binding]
    public class ClaimsetStepDefinitions
    {
        private readonly PlaywrightContext _playwrightContext;
        private readonly TestLogger _logger;
        private readonly ScenarioContext _scenarioContext;

        private IAPIResponse _apiResponse = null!;
        private int _lastUploadStatusCode = 0;

        public ClaimsetStepDefinitions(
            PlaywrightContext playwrightContext,
            TestLogger logger,
            ScenarioContext scenarioContext
        )
        {
            _playwrightContext = playwrightContext;
            _logger = logger;
            _scenarioContext = scenarioContext;
        }

        [Given("the claimSet {string} is authorized with namespacePrefixes {string}")]
        public async Task GivenTheClaimSetIsAuthorizedWithNamespacePrefixes(
            string claimSetName,
            string namespacePrefixes
        )
        {
            await SetAuthorizationToken(namespacePrefixes, string.Empty, claimSetName);
        }

        [Given("the claimSet {string} is authorized with educationOrganizationIds {string}")]
        public async Task GivenTheClaimSetIsAuthorizedWithEdOrgIds(
            string claimSetName,
            string educationOrganizationIds
        )
        {
            await SetAuthorizationToken("uri://ed-fi.org", educationOrganizationIds, claimSetName);
        }

        [Given(
            "the claimSet {string} is authorized with namespace {string} and educationOrganizationIds {string}"
        )]
        public async Task GivenTheClaimSetIsAuthorizedWithNamespaceAndEdOrgIds(
            string claimSetName,
            string namespacePrefixes,
            string educationOrganizationIds
        )
        {
            await SetAuthorizationToken(namespacePrefixes, educationOrganizationIds, claimSetName);
        }

        // Helper method to create and set an authorization token for a specific claim set
        private async Task SetAuthorizationToken(
            string namespacePrefixes,
            string educationOrganizationIds,
            string claimSetName = "E2E-NoFurtherAuthRequiredClaimSet"
        )
        {
            await AuthorizationDataProvider.Create(
                Guid.NewGuid().ToString(),
                "contactName",
                "contactName@example.com",
                namespacePrefixes,
                educationOrganizationIds,
                SystemAdministrator.Token,
                claimSetName
            );

            var bearerToken = await AuthorizationDataProvider.GetToken();
            var dmsToken = $"Bearer {bearerToken}";
            _scenarioContext["dmsToken"] = dmsToken;
        }

        [When("a claim set is uploaded to CMS that grants {string} access to {string}")]
        [Given("a claim set is uploaded to CMS that grants {string} access to {string}")]
        public async Task WhenAClaimSetIsUploadedToCMSThatGrantsEndpointAccess(
            string endpointName,
            string claimSetName
        )
        {
            var claimsJson = $$"""
                {
                    "claims": {
                        "claimSets": [
                            {
                                "claimSetName": "{{claimSetName}}",
                                "isSystemReserved": false
                            }
                        ],
                        "claimsHierarchy": [
                            {
                                "name": "http://ed-fi.org/identity/claims/domains/edFi",
                                "claims": [
                                    {
                                        "name": "http://ed-fi.org/identity/claims/ed-fi/{{endpointName}}",
                                        "claimSets": [
                                            {
                                                "name": "{{claimSetName}}",
                                                "actions": [
                                                    {
                                                        "name": "Create",
                                                        "authorizationStrategyOverrides": [
                                                            {
                                                                "name": "NoFurtherAuthorizationRequired"
                                                            }
                                                        ]
                                                    },
                                                    {
                                                        "name": "Read",
                                                        "authorizationStrategyOverrides": [
                                                            {
                                                                "name": "NoFurtherAuthorizationRequired"
                                                            }
                                                        ]
                                                    },
                                                    {
                                                        "name": "Update",
                                                        "authorizationStrategyOverrides": [
                                                            {
                                                                "name": "NoFurtherAuthorizationRequired"
                                                            }
                                                        ]
                                                    },
                                                    {
                                                        "name": "Delete",
                                                        "authorizationStrategyOverrides": [
                                                            {
                                                                "name": "NoFurtherAuthorizationRequired"
                                                            }
                                                        ]
                                                    }
                                                ]
                                            }
                                        ]
                                    }
                                ]
                            }
                        ]
                    }
                }
                """;

            // Call the CMS endpoint to upload the claim set
            var httpClient = new HttpClient();
            var content = new StringContent(claimsJson, System.Text.Encoding.UTF8, "application/json");

            // Get SystemAdministrator auth header for CMS request
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {SystemAdministrator.Token}");

            var response = await httpClient.PostAsync(
                $"http://localhost:{AppSettings.ConfigServicePort}/config/management/upload-claims",
                content
            );

            // Store the status code for verification
            _lastUploadStatusCode = (int)response.StatusCode;

            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.log.Information(
                $"CMS Upload URL: http://localhost:{AppSettings.ConfigServicePort}/config/management/upload-claims"
            );
            _logger.log.Information($"CMS Upload Request JSON: {claimsJson}");
            _logger.log.Information($"CMS Upload Response Status: {_lastUploadStatusCode}");
            _logger.log.Information($"CMS Upload Response Body: {responseBody}");
        }

        [Then("the claim set upload to CMS should be successful")]
        [Given("the claim set upload to CMS should be successful")]
        public void ThenTheClaimSetUploadToCMSShouldBeSuccessful()
        {
            _lastUploadStatusCode.Should().Be(200, "Claim set upload should succeed");
        }

        [When("a POST request is made to DMS management endpoint {string}")]
        public async Task WhenAPOSTRequestIsMadeToDMSManagementEndpoint(string endpoint)
        {
            // Use DMS URL directly with the management endpoint
            var url = endpoint.StartsWith('/') ? endpoint[1..] : endpoint;

            _logger.log.Information($"DMS POST url: {url}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        { "Authorization", $"Bearer {SystemAdministrator.Token}" },
                    },
                }
            )!;

            _logger.log.Information($"DMS POST Response Status: {_apiResponse.Status}");
            var responseBody = await _apiResponse.TextAsync();
            _logger.log.Information($"DMS POST Response Body: {responseBody}");
        }

        [When("a GET request is made to DMS management endpoint {string}")]
        public async Task WhenAGETRequestIsMadeToDMSManagementEndpoint(string endpoint)
        {
            // Use DMS URL directly with the management endpoint
            var url = endpoint.StartsWith('/') ? endpoint[1..] : endpoint;

            _logger.log.Information($"DMS GET url: {url}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
                url,
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        { "Authorization", $"Bearer {SystemAdministrator.Token}" },
                    },
                }
            )!;

            _logger.log.Information($"DMS GET Response Status: {_apiResponse.Status}");
            var responseBody = await _apiResponse.TextAsync();
            _logger.log.Information($"DMS GET Response Body: {responseBody}");

            _scenarioContext["dmsViewResponse"] = responseBody;
        }

        // Verifies that the DMS claimsets reload operation was successful
        [Then("the DMS claimsets reload should be successful")]
        public void ThenTheDMSClaimsetsReloadShouldBeSuccessful()
        {
            _apiResponse.Status.Should().Be(200, "DMS claimsets reload should succeed");

            var responseBody = _apiResponse.TextAsync().Result;
            _logger.log.Information($"DMS Reload Response: {responseBody}");
        }

        [Then("the DMS view claimsets should be successful")]
        public void ThenTheDMSViewClaimsetsShouldBeSuccessful()
        {
            _apiResponse.Status.Should().Be(200, "DMS view claimsets should succeed");

            // Store the response body for verification
            var responseBody = _apiResponse.TextAsync().Result;
            _logger.log.Information($"DMS View Response: {responseBody}");
            _scenarioContext["dmsViewResponse"] = responseBody;
        }

        [Then("the DMS view claimsets response should contain {string}")]
        public void ThenTheDMSViewClaimsetsResponseShouldContain(string claimSetName)
        {
            _scenarioContext.Should().ContainKey("dmsViewResponse", "DMS view response should be available");

            var responseBody = _scenarioContext["dmsViewResponse"] as string;
            responseBody.Should().NotBeNullOrEmpty("DMS view response should not be empty");

            responseBody
                .Should()
                .Contain(claimSetName, $"Claimsets view should contain the claimset '{claimSetName}'");
        }

        [Then("the DMS view claimsets response should not contain {string}")]
        public void ThenTheDMSViewClaimsetsResponseShouldNotContain(string claimSetName)
        {
            _scenarioContext.Should().ContainKey("dmsViewResponse", "DMS view response should be available");

            var responseBody = _scenarioContext["dmsViewResponse"] as string;
            responseBody.Should().NotBeNullOrEmpty("DMS view response should not be empty");

            responseBody
                .Should()
                .NotContain(claimSetName, $"Claimsets view should not contain the claimset '{claimSetName}'");
        }

        [When("a POST request is made to CMS {string}")]
        public async Task WhenAPOSTRequestIsMadeToCMS(string endpoint)
        {
            // Get system administrator token for CMS API access
            if (string.IsNullOrEmpty(SystemAdministrator.Token))
            {
                await SystemAdministrator.Register("SystemAdministratorClient", "SystemAdministratorSecret");
            }

            // Use HttpClient to call CMS management API
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
            };

            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SystemAdministrator.Token);

            // POST with empty body for reload-claims endpoint
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync($"config{endpoint}", content);

            // Store response for verification
            _lastUploadStatusCode = (int)response.StatusCode;

            // Also store the response body for reload ID verification if needed
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _scenarioContext["reloadResponse"] = responseBody;
            }
        }

        [Then("the CMS reload should be successful")]
        public void ThenTheCMSReloadShouldBeSuccessful()
        {
            _lastUploadStatusCode.Should().Be(200, "Claim set reload should succeed");

            // Optionally verify the response contains success flag
            if (_scenarioContext.ContainsKey("reloadResponse"))
            {
                var responseBody = _scenarioContext["reloadResponse"].ToString();
                responseBody.Should().Contain("\"success\":true", "Reload response should indicate success");
            }
        }

        [Then("system claim sets should have empty resource claims")]
        public void ThenSystemClaimSetsShouldHaveEmptyResourceClaims()
        {
            _scenarioContext.Should().ContainKey("dmsViewResponse", "DMS view response should be available");

            var responseBody = _scenarioContext["dmsViewResponse"] as string;
            responseBody.Should().NotBeNullOrEmpty("DMS view response should not be empty");

            // Parse the JSON response
            List<ClaimSetResponse>? deserializedClaimSets = System.Text.Json.JsonSerializer.Deserialize<
                List<ClaimSetResponse>
            >(
                responseBody!,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            List<ClaimSetResponse> claimSets = deserializedClaimSets ?? [];

            claimSets.Should().NotBeNull("Claim sets should be deserializable");

            // Define known system claim sets
            var systemClaimSetNames = new HashSet<string>
            {
                "SISVendor",
                "EdFiSandbox",
                "RosterVendor",
                "AssessmentVendor",
                "AssessmentRead",
                "BootstrapDescriptorsandEdOrgs",
                "DistrictHostedSISVendor",
                "EdFiODSAdminApp",
                "ABConnect",
                "E2E-NameSpaceBasedClaimSet",
                "E2E-NoFurtherAuthRequiredClaimSet",
                "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            };

            // Find and verify system claim sets
            List<ClaimSetResponse> systemClaimSets = claimSets!
                .Where(cs => systemClaimSetNames.Contains(cs.Name))
                .ToList();

            _logger.log.Information(
                "Found {SystemClaimSetCount} system claim sets out of {TotalClaimSetCount} total claim sets",
                systemClaimSets.Count,
                claimSets!.Count
            );

            foreach (var systemClaimSet in systemClaimSets)
            {
                _logger.log.Information(
                    "Verifying system claim set '{ClaimSetName}' has {ResourceClaimCount} resource claims",
                    systemClaimSet.Name,
                    systemClaimSet.ResourceClaims?.Count ?? 0
                );

                systemClaimSet
                    .ResourceClaims.Should()
                    .BeEmpty($"System claim set '{systemClaimSet.Name}' should have empty resource claims");
            }

            // Also verify that at least some system claim sets were found
            systemClaimSets
                .Should()
                .NotBeEmpty("At least some system claim sets should be present in the response");
        }

        // Helper class for deserializing the claim set response
        private class ClaimSetResponse
        {
            public string Name { get; set; } = string.Empty;
            public List<ResourceClaimResponse> ResourceClaims { get; set; } = [];
        }

        private class ResourceClaimResponse
        {
            // Properties intentionally left empty as we only care about the count
        }
    }
}
