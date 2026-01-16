// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Tests.E2E.Profiles;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

/// <summary>
/// Extends authorization functionality to support profile assignment when creating applications.
/// Used for E2E tests that need to test profile-based response filtering.
/// </summary>
public static class ProfileAwareAuthorizationProvider
{
    /// <summary>
    /// Client credentials obtained from the most recent call to CreateClientCredentialsWithProfiles().
    /// </summary>
    private static ClientCredentials _clientCredentials = null!;

    /// <summary>
    /// HTTP client configured to communicate with the Configuration Management Service (CMS).
    /// </summary>
    private static readonly HttpClient _configurationServiceClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
    };

    /// <summary>
    /// HTTP client configured to communicate with the Data Management Service (DMS).
    /// </summary>
    private static readonly HttpClient _dmsClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.DmsPort}/"),
    };

    /// <summary>
    /// Creates a new vendor and application with profile assignments in the Configuration Management Service.
    /// This enables testing of profile-based response filtering.
    /// </summary>
    /// <param name="company">The name of the vendor company to register</param>
    /// <param name="contactName">The name of the primary contact for the vendor</param>
    /// <param name="contactEmailAddress">The email address of the primary contact</param>
    /// <param name="namespacePrefixes">Comma-separated list of namespace prefixes</param>
    /// <param name="edOrgIds">Comma-separated list of education organization IDs</param>
    /// <param name="systemAdministratorToken">Bearer token with system administrator privileges</param>
    /// <param name="claimSetName">The name of the claim set to assign</param>
    /// <param name="profileNames">Optional list of profile names to assign to the application</param>
    public static async Task CreateClientCredentialsWithProfiles(
        string company,
        string contactName,
        string contactEmailAddress,
        string namespacePrefixes,
        string edOrgIds,
        string systemAdministratorToken,
        string claimSetName = "E2E-NoFurtherAuthRequiredClaimSet",
        params string[] profileNames
    )
    {
        _configurationServiceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            systemAdministratorToken
        );

        // Create DmsInstance first
        int dmsInstanceId = await CreateDmsInstance();

        // Create vendor
        int vendorId = await CreateVendor(company, contactName, contactEmailAddress, namespacePrefixes);

        // Resolve profile IDs from profile names
        int[] profileIds = ResolveProfileIds(profileNames);

        // Parse education organization IDs
        long[] educationOrganizationIds = ParseEducationOrganizationIds(edOrgIds);

        // Create application with profiles
        await CreateApplication(vendorId, claimSetName, educationOrganizationIds, dmsInstanceId, profileIds);
    }

    /// <summary>
    /// Creates a profile in the Configuration Management Service.
    /// Returns the profile ID from the Location header.
    /// </summary>
    /// <param name="profileName">The profile name</param>
    /// <param name="profileXml">The profile XML definition</param>
    /// <param name="systemAdministratorToken">Bearer token with system administrator privileges</param>
    /// <returns>The profile ID</returns>
    public static async Task<int> CreateProfile(
        string profileName,
        string profileXml,
        string systemAdministratorToken
    )
    {
        _configurationServiceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            systemAdministratorToken
        );

        var profileRequest = new { name = profileName, definition = profileXml };

        using StringContent content = new(
            JsonSerializer.Serialize(profileRequest),
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage response = await _configurationServiceClient.PostAsync(
            "v2/profiles/",
            content
        );

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync();

            // Check if it's a duplicate error (profile already exists)
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && errorBody.Contains("exists"))
            {
                // Profile already exists, try to find it
                return await FindExistingProfile(profileName);
            }

            throw new InvalidOperationException(
                $"Failed to create profile '{profileName}'. Status: {response.StatusCode}, Response: {errorBody}"
            );
        }

        // Extract profile ID from Location header
        string? location = response.Headers.Location?.AbsoluteUri;
        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException(
                $"Profile created but Location header is missing for '{profileName}'"
            );
        }

        // Location format: http://localhost:8081/v2/profiles/123
        string[] segments = location.Split('/');
        string idString = segments[^1];
        if (!int.TryParse(idString, out int profileId))
        {
            throw new InvalidOperationException(
                $"Could not parse profile ID from Location header: {location}"
            );
        }

        return profileId;
    }

    /// <summary>
    /// Finds an existing profile by name and returns its ID.
    /// </summary>
    private static async Task<int> FindExistingProfile(string profileName)
    {
        using HttpResponseMessage response = await _configurationServiceClient.GetAsync(
            $"v2/profiles/?limit=100&offset=0"
        );

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to list profiles while searching for '{profileName}'"
            );
        }

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        foreach (JsonElement profile in doc.RootElement.EnumerateArray())
        {
            if (
                profile.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.GetString() == profileName
                && profile.TryGetProperty("id", out JsonElement idElement)
            )
            {
                return idElement.GetInt32();
            }
        }

        throw new InvalidOperationException($"Profile '{profileName}' not found in existing profiles");
    }

    /// <summary>
    /// Generates an OAuth access token using the stored client credentials.
    /// </summary>
    public static async Task<string> GetToken()
    {
        var formData = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
        ]);

        byte[] basicBytes = Encoding.ASCII.GetBytes($"{_clientCredentials.key}:{_clientCredentials.secret}");
        string basicB64 = Convert.ToBase64String(basicBytes);

        _dmsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicB64);
        HttpResponseMessage tokenResponse = await _dmsClient.PostAsync("oauth/token", formData);
        string jsonString = await tokenResponse.Content.ReadAsStringAsync();

        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to get token. Status: {tokenResponse.StatusCode}, Response: {jsonString}"
            );
        }

        using JsonDocument tokenJson = JsonDocument.Parse(jsonString);

        if (!tokenJson.RootElement.TryGetProperty("access_token", out JsonElement accessTokenProperty))
        {
            throw new InvalidOperationException(
                $"Token response missing access_token. Response: {jsonString}"
            );
        }

        return accessTokenProperty.GetString() ?? "";
    }

    private static async Task<int> CreateDmsInstance()
    {
        var dmsInstanceRequest = new
        {
            instanceType = "Test",
            instanceName = $"E2E Profile Test DMS Instance {Guid.NewGuid():N}",
            connectionString = "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;",
        };

        using StringContent content = new(
            JsonSerializer.Serialize(dmsInstanceRequest),
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage response = await _configurationServiceClient.PostAsync(
            "v2/dmsInstances",
            content
        );

        string? location = response.Headers.Location?.AbsoluteUri;
        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException("DMS Instance created but Location header is missing");
        }

        using HttpResponseMessage getResponse = await _configurationServiceClient.GetAsync(location);
        string body = await getResponse.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    private static async Task<int> CreateVendor(
        string company,
        string contactName,
        string contactEmailAddress,
        string namespacePrefixes
    )
    {
        var vendorRequest = new
        {
            company,
            contactName,
            contactEmailAddress,
            namespacePrefixes,
        };

        using StringContent content = new(
            JsonSerializer.Serialize(vendorRequest),
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage response = await _configurationServiceClient.PostAsync(
            "v2/vendors",
            content
        );

        string? location = response.Headers.Location?.AbsoluteUri;
        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException("Vendor created but Location header is missing");
        }

        using HttpResponseMessage getResponse = await _configurationServiceClient.GetAsync(location);
        string body = await getResponse.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    private static int[] ResolveProfileIds(string[] profileNames)
    {
        if (profileNames.Length == 0)
        {
            return [];
        }

        var profileIds = new List<int>();
        foreach (string profileName in profileNames)
        {
            if (ProfileTestData.TryGetProfileId(profileName, out int profileId))
            {
                profileIds.Add(profileId);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Profile '{profileName}' not found in ProfileTestData. "
                        + $"Available profiles: {string.Join(", ", ProfileTestData.RegisteredProfileNames)}"
                );
            }
        }

        return [.. profileIds];
    }

    private static long[] ParseEducationOrganizationIds(string edOrgIds)
    {
        if (string.IsNullOrEmpty(edOrgIds))
        {
            return [];
        }

        return edOrgIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.Parse(s.Trim()))
            .ToArray();
    }

    private static async Task CreateApplication(
        int vendorId,
        string claimSetName,
        long[] educationOrganizationIds,
        int dmsInstanceId,
        int[] profileIds
    )
    {
        object applicationRequest;

        if (profileIds.Length > 0)
        {
            applicationRequest = new
            {
                vendorId,
                applicationName = $"E2E Profile Test {Guid.NewGuid():N}",
                claimSetName,
                educationOrganizationIds,
                dmsInstanceIds = new[] { dmsInstanceId },
                profileIds,
            };
        }
        else
        {
            applicationRequest = new
            {
                vendorId,
                applicationName = $"E2E Profile Test {Guid.NewGuid():N}",
                claimSetName,
                educationOrganizationIds,
                dmsInstanceIds = new[] { dmsInstanceId },
            };
        }

        string requestJson = JsonSerializer.Serialize(applicationRequest);

        using StringContent content = new(requestJson, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _configurationServiceClient.PostAsync(
            "v2/applications",
            content
        );

        string applicationBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to create application. Status: {response.StatusCode}, Response: {applicationBody}"
            );
        }

        using JsonDocument applicationJson = JsonDocument.Parse(applicationBody);

        if (
            !applicationJson.RootElement.TryGetProperty("key", out JsonElement keyProperty)
            || !applicationJson.RootElement.TryGetProperty("secret", out JsonElement secretProperty)
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
}
