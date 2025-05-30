// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

public static class AuthorizationDataProvider
{
    public static ClientCredentials? ClientCredentials { get; set; }

    private static readonly HttpClient _configurationServiceClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
    };

    private static readonly HttpClient _dmsClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.DmsPort}/"),
    };

    public static async Task Create(
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

        var requestJson = JsonSerializer.Serialize(
            new
            {
                vendorId,
                applicationName = "E2E",
                claimSetName,
                educationOrganizationIds,
            }
        );

        using StringContent applicationContent = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage applicationPostResponse = await _configurationServiceClient.PostAsync(
            "v2/applications",
            applicationContent
        );

        string applicationBody = await applicationPostResponse.Content.ReadAsStringAsync();
        var applicationJson = JsonDocument.Parse(applicationBody);

        var credentials = new ClientCredentials(
            applicationJson.RootElement.GetProperty("key").GetString() ?? "",
            applicationJson.RootElement.GetProperty("secret").GetString() ?? ""
        );

        ClientCredentials = credentials;
    }

    public static async Task<string> GetToken()
    {
        var formData = new FormUrlEncodedContent(
            new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }
        );

        byte[] basicBytes = Encoding.ASCII.GetBytes($"{ClientCredentials!.key}:{ClientCredentials.secret}");
        string basicB64 = Convert.ToBase64String(basicBytes);

        _dmsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue($"Basic", basicB64);
        var tokenResponse = await _dmsClient.PostAsync("oauth/token", formData);
        var jsonString = await tokenResponse.Content.ReadAsStringAsync();
        var tokenJson = JsonDocument.Parse(jsonString);

        return tokenJson.RootElement.GetProperty("access_token").GetString() ?? "";
    }
}
