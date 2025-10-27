// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using EdFi.InstanceManagement.Tests.E2E.Models;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Helper class for authentication token management
/// </summary>
public static class TokenHelper
{
    private static readonly HttpClient HttpClient = new();

    /// <summary>
    /// Get access token from Config Service using client credentials
    /// </summary>
    public static async Task<string> GetConfigServiceTokenAsync(
        string tokenUrl,
        string clientId,
        string clientSecret
    )
    {
        var requestContent = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "client_secret", clientSecret },
                { "grant_type", "client_credentials" },
                { "scope", "edfi_admin_api/full_access" },
            }
        );

        var response = await HttpClient.PostAsync(tokenUrl, requestContent);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        return tokenResponse?.AccessToken
            ?? throw new InvalidOperationException("Failed to get access token");
    }

    /// <summary>
    /// Get access token from DMS using Basic authentication
    /// </summary>
    public static async Task<string> GetDmsTokenAsync(string tokenUrl, string clientKey, string clientSecret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);

        // Basic authentication: base64(key:secret)
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientKey}:{clientSecret}"));
        request.Headers.Add("Authorization", $"Basic {credentials}");

        var requestContent = new FormUrlEncodedContent(
            new Dictionary<string, string> { { "grant_type", "client_credentials" } }
        );

        request.Content = requestContent;

        var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        return tokenResponse?.AccessToken
            ?? throw new InvalidOperationException("Failed to get DMS access token");
    }
}
