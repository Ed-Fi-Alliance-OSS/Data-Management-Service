// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Client for interacting with the DMS API with route qualifiers
/// </summary>
public class DmsApiClient(string baseUrl, string accessToken)
{
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri(baseUrl) };
    private readonly string _accessToken = accessToken;

    /// <summary>
    /// POST a resource to DMS with route qualifiers
    /// </summary>
    public async Task<HttpResponseMessage> PostResourceAsync(
        string districtId,
        string schoolYear,
        string resource,
        object body
    )
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var url = $"/{districtId}/{schoolYear}/data/ed-fi/{resource}";
        var response = await _httpClient.PostAsJsonAsync(url, body);

        return response;
    }

    /// <summary>
    /// GET a resource collection from DMS with route qualifiers
    /// </summary>
    public async Task<HttpResponseMessage> GetResourceAsync(
        string districtId,
        string schoolYear,
        string resource
    )
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var url = $"/{districtId}/{schoolYear}/data/ed-fi/{resource}";
        var response = await _httpClient.GetAsync(url);

        return response;
    }

    /// <summary>
    /// GET a resource by full location URL
    /// </summary>
    public async Task<HttpResponseMessage> GetByLocationAsync(string location)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.GetAsync(location);

        return response;
    }

    /// <summary>
    /// GET a resource without route qualifiers (for error testing)
    /// </summary>
    public async Task<HttpResponseMessage> GetResourceWithoutQualifiersAsync(string resource)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var url = $"/data/ed-fi/{resource}";
        var response = await _httpClient.GetAsync(url);

        return response;
    }

    /// <summary>
    /// Get DMS discovery API
    /// </summary>
    public async Task<JsonDocument> GetDiscoveryAsync()
    {
        var response = await _httpClient.GetAsync("/");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }
}
