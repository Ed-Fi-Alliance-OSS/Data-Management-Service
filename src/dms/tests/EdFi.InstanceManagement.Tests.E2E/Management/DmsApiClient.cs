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
public class DmsApiClient : IDisposable
{
    // Shared HttpClient for unauthenticated requests (e.g., discovery endpoints)
    // HttpClient is thread-safe and designed to be reused
    private static readonly HttpClient _sharedHttpClient = new();

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _accessToken;
    private bool _disposed;

    public DmsApiClient(string baseUrl, string accessToken)
    {
        _baseUrl = baseUrl;
        _accessToken = accessToken;

        // Create and configure HttpClient with base URL and authorization header
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Set authorization header once during client creation (not on every request)
        // This is the recommended pattern for HttpClient to avoid threading issues
        if (!string.IsNullOrEmpty(accessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                accessToken
            );
        }
    }

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
        var url = $"/{districtId}/{schoolYear}/data/ed-fi/{resource}";
        var response = await _httpClient.GetAsync(url);

        return response;
    }

    /// <summary>
    /// GET a resource by full location URL
    /// </summary>
    public async Task<HttpResponseMessage> GetByLocationAsync(string location)
    {
        var response = await _httpClient.GetAsync(location);

        return response;
    }

    /// <summary>
    /// GET a resource without route qualifiers (for error testing)
    /// </summary>
    public async Task<HttpResponseMessage> GetResourceWithoutQualifiersAsync(string resource)
    {
        var url = $"/data/ed-fi/{resource}";
        var response = await _httpClient.GetAsync(url);

        return response;
    }

    /// <summary>
    /// Get DMS discovery API
    /// </summary>
    public async Task<JsonDocument> GetDiscoveryAsync()
    {
        // Use shared HttpClient for unauthenticated discovery requests
        if (string.IsNullOrEmpty(_accessToken))
        {
            var fullUrl = $"{_baseUrl}/";
            var response = await _sharedHttpClient.GetAsync(fullUrl);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(content);
        }

        var authenticatedResponse = await _httpClient.GetAsync("/");
        authenticatedResponse.EnsureSuccessStatusCode();

        var authenticatedContent = await authenticatedResponse.Content.ReadAsStringAsync();
        return JsonDocument.Parse(authenticatedContent);
    }

    /// <summary>
    /// Get DMS discovery API with route qualifiers
    /// </summary>
    public async Task<HttpResponseMessage> GetDiscoveryWithRouteAsync(string route)
    {
        var url = string.IsNullOrEmpty(route) ? "/" : $"/{route}";

        // Use shared HttpClient for unauthenticated requests to avoid connection exhaustion
        if (string.IsNullOrEmpty(_accessToken))
        {
            var fullUrl = $"{_baseUrl}{url}";
            var response = await _sharedHttpClient.GetAsync(fullUrl);
            return response;
        }

        var authenticatedResponse = await _httpClient.GetAsync(url);
        return authenticatedResponse;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _httpClient.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
