// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

/// <summary>
/// Provides authorization functionality for E2E tests by managing vendor registration,
/// application creation, and OAuth token generation for the Ed-Fi Data Management Service.
/// This class handles the complete authorization workflow needed for test scenarios.
/// </summary>
public static class AuthorizationDataProvider
{
    /// <summary>
    /// The client credentials obtained from the most recent call to CreateClientCredentials().
    /// These credentials are used for OAuth token generation.
    /// </summary>
    private static ClientCredentials _clientCredentials = null!;

    /// <summary>
    /// HTTP client configured to communicate with the Configuration Management Service (CMS).
    /// Used for vendor and application registration operations.
    /// </summary>
    private static readonly HttpClient _configurationServiceClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
    };

    /// <summary>
    /// HTTP client configured to communicate with the Data Management Service (DMS).
    /// Used for OAuth token generation and authentication operations.
    /// </summary>
    private static readonly HttpClient _dmsClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.DmsPort}/"),
    };

    /// <summary>
    /// Creates a new vendor and application in the Configuration Management Service with specified
    /// authorization parameters. This establishes the foundation for OAuth authentication in tests.
    /// </summary>
    /// <param name="company">The name of the vendor company to register</param>
    /// <param name="contactName">The name of the primary contact for the vendor</param>
    /// <param name="contactEmailAddress">The email address of the primary contact</param>
    /// <param name="namespacePrefixes">Comma-separated list of namespace prefixes the vendor is authorized to use</param>
    /// <param name="edOrgIds">Comma-separated list of education organization IDs the vendor has access to</param>
    /// <param name="systemAdministratorToken">Bearer token with system administrator privileges for CMS API access</param>
    /// <param name="claimSetName">The name of the claim set to assign to the application (default: "SIS-Vendor")</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public static async Task CreateClientCredentials(
        string company,
        string contactName,
        string contactEmailAddress,
        string namespacePrefixes,
        string edOrgIds,
        string systemAdministratorToken,
        string claimSetName = "SIS-Vendor"
    )
    {
        _configurationServiceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            systemAdministratorToken
        );

        // Create DmsInstance first
        using StringContent dmsInstanceContent = new(
            JsonSerializer.Serialize(
                new
                {
                    instanceType = "Test",
                    instanceName = "E2E Test DMS Instance",
                    connectionString = "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;",
                }
            ),
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage dmsInstancePostResponse = await _configurationServiceClient.PostAsync(
            "v2/dmsInstances",
            dmsInstanceContent
        );

        var dmsInstanceLocation = dmsInstancePostResponse.Headers.Location?.AbsoluteUri ?? "";

        using HttpResponseMessage dmsInstanceGetResponse = await _configurationServiceClient.GetAsync(
            dmsInstanceLocation
        );
        string dmsInstanceBody = await dmsInstanceGetResponse.Content.ReadAsStringAsync();

        int dmsInstanceId = JsonDocument.Parse(dmsInstanceBody).RootElement.GetProperty("id").GetInt32();

        // Create vendor
        using StringContent vendorContent = new(
            JsonSerializer.Serialize(
                new
                {
                    company,
                    contactName,
                    contactEmailAddress,
                    namespacePrefixes,
                }
            ),
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage vendorPostResponse = await _configurationServiceClient.PostAsync(
            "v2/vendors",
            vendorContent
        );

        var vendorLocation = vendorPostResponse.Headers.Location?.AbsoluteUri ?? "";

        using HttpResponseMessage vendorGetResponse = await _configurationServiceClient.GetAsync(
            vendorLocation
        );
        string vendorBody = await vendorGetResponse.Content.ReadAsStringAsync();

        int vendorId = JsonDocument.Parse(vendorBody).RootElement.GetProperty("id").GetInt32();

        long[] educationOrganizationIds = [];
        if (!string.IsNullOrEmpty(edOrgIds))
        {
            educationOrganizationIds = edOrgIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.Parse(s.Trim()))
                .ToArray();
        }

        // Create application with DmsInstance
        var requestJson = JsonSerializer.Serialize(
            new
            {
                vendorId,
                applicationName = "E2E",
                claimSetName,
                educationOrganizationIds,
                dmsInstanceIds = new[] { dmsInstanceId },
            }
        );

        using StringContent applicationContent = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage applicationPostResponse = await _configurationServiceClient.PostAsync(
            "v2/applications",
            applicationContent
        );

        string applicationBody = await applicationPostResponse.Content.ReadAsStringAsync();

        if (!applicationPostResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to create application. Status: {applicationPostResponse.StatusCode}, "
                    + $"Response: {applicationBody}"
            );
        }

        var applicationJson = JsonDocument.Parse(applicationBody);

        if (
            !applicationJson.RootElement.TryGetProperty("key", out var keyProperty)
            || !applicationJson.RootElement.TryGetProperty("secret", out var secretProperty)
        )
        {
            throw new InvalidOperationException(
                $"Application response missing key or secret. Response: {applicationBody}"
            );
        }

        _clientCredentials = new ClientCredentials(
            keyProperty.GetString() ?? "",
            secretProperty.GetString() ?? ""
        );
    }

    /// <summary>
    /// Generates an OAuth access token using the stored client credentials.
    /// This token is required for authenticating API requests to the Data Management Service.
    /// </summary>
    /// <returns>A valid OAuth access token as a string</returns>
    public static async Task<string> GetToken()
    {
        var formData = new FormUrlEncodedContent(
            new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }
        );

        byte[] basicBytes = Encoding.ASCII.GetBytes($"{_clientCredentials.key}:{_clientCredentials.secret}");
        string basicB64 = Convert.ToBase64String(basicBytes);

        _dmsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue($"Basic", basicB64);
        var tokenResponse = await _dmsClient.PostAsync("oauth/token", formData);
        var jsonString = await tokenResponse.Content.ReadAsStringAsync();

        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to get token. Status: {tokenResponse.StatusCode}, " + $"Response: {jsonString}"
            );
        }

        var tokenJson = JsonDocument.Parse(jsonString);

        if (!tokenJson.RootElement.TryGetProperty("access_token", out var accessTokenProperty))
        {
            throw new InvalidOperationException(
                $"Token response missing access_token. Response: {jsonString}"
            );
        }

        return accessTokenProperty.GetString() ?? "";
    }
}
