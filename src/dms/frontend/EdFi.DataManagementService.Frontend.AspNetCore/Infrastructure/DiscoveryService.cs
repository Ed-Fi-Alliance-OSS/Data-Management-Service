// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class DiscoveryService(IHttpClientFactory httpClientFactory, ILogger<DiscoveryService> logger)
{
    public async Task<string> GetTokenEndpointAsync(string discoveryUrl)
    {
        try
        {
            logger.LogInformation("Fetching OpenID discovery document from: {DiscoveryUrl}", discoveryUrl);

            var httpClient = httpClientFactory.CreateClient();
            var discoveryResponse = await httpClient.GetAsync(discoveryUrl);

            if (!discoveryResponse.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Failed to fetch OpenID configuration. HTTP Status: {discoveryResponse.StatusCode}");
            }

            string discoveryContent = await discoveryResponse.Content.ReadAsStringAsync();
            logger.LogDebug("Discovery document content: {DiscoveryContent}", discoveryContent);

            var discoveryDocument = JsonSerializer.Deserialize<DiscoveryDocument>(discoveryContent);
            if (discoveryDocument == null || string.IsNullOrEmpty(discoveryDocument.TokenEndpoint))
            {
                throw new InvalidOperationException("Token endpoint is missing in the discovery document.");
            }

            logger.LogInformation("Resolved token endpoint: {TokenEndpoint}", discoveryDocument.TokenEndpoint);
            return discoveryDocument.TokenEndpoint;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unexpected error occurred while fetching the discovery document.");
            throw new Exception();
        }
    }

    public class DiscoveryDocument
    {
        [JsonPropertyName("token_endpoint")]
        public required string TokenEndpoint { get; set; }
    }

}

