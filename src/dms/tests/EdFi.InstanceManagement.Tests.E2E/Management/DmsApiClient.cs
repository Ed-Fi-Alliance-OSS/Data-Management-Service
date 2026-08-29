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
    private readonly string? _tenant;
    private bool _disposed;

    public DmsApiClient(string baseUrl, string accessToken, string? tenant = null)
    {
        _baseUrl = baseUrl;
        _accessToken = accessToken;
        _tenant = tenant;

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
    /// Builds a URL path with optional tenant prefix
    /// </summary>
    private string BuildPath(string path)
    {
        if (string.IsNullOrEmpty(_tenant))
        {
            return path;
        }
        return $"/{_tenant}{path}";
    }

    /// <summary>
    /// The route-qualified path of one Ed-Fi resource collection, tenant segment included.
    /// </summary>
    /// <remarks>
    /// Every route-qualified data-plane request composes its path from here, so a sibling operation
    /// cannot lose the tenant segment or a qualifier the collection request keeps. Copies of the same
    /// interpolation would hold that property only by coincidence.
    /// </remarks>
    private string ResourcePath(string districtId, string schoolYear, string resource) =>
        BuildPath($"/{districtId}/{schoolYear}/data/ed-fi/{resource}");

    /// <summary>
    /// The request header that asks DMS to serve a read from the data store's configured snapshot rather
    /// than from current data.
    /// </summary>
    public const string UseSnapshotHeaderName = "Use-Snapshot";

    /// <summary>
    /// Sends one request, optionally asking for a snapshot.
    /// </summary>
    /// <remarks>
    /// The header is attached per request rather than to the client's default headers, so one client can
    /// send a snapshot-requesting and a plain request in the same scenario and a stray default cannot
    /// leak into a later assertion. The value is the caller's verbatim text, because the parsing rule -
    /// only a value that parses as boolean true asks for a snapshot - is the behavior under test.
    /// </remarks>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        object? body = null,
        string? useSnapshot = null
    )
    {
        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (useSnapshot is not null)
        {
            request.Headers.TryAddWithoutValidation(UseSnapshotHeaderName, useSnapshot);
        }

        return await _httpClient.SendAsync(request);
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
        var url = ResourcePath(districtId, schoolYear, resource);

        return await SendAsync(HttpMethod.Post, url, body);
    }

    /// <summary>
    /// GET a resource collection from DMS with route qualifiers
    /// </summary>
    public async Task<HttpResponseMessage> GetResourceAsync(
        string districtId,
        string schoolYear,
        string resource,
        string? query = null,
        string? useSnapshot = null
    )
    {
        var url = ResourcePath(districtId, schoolYear, resource);

        if (!string.IsNullOrEmpty(query))
        {
            url = $"{url}?{query}";
        }

        return await SendAsync(HttpMethod.Get, url, useSnapshot: useSnapshot);
    }

    /// <summary>
    /// GET one resource by id under route qualifiers, so a by-id read can be routed the same way a
    /// collection read is.
    /// </summary>
    public async Task<HttpResponseMessage> GetResourceByIdAsync(
        string districtId,
        string schoolYear,
        string resource,
        string id,
        string? useSnapshot = null
    )
    {
        var url = $"{ResourcePath(districtId, schoolYear, resource)}/{id}";

        return await SendAsync(HttpMethod.Get, url, useSnapshot: useSnapshot);
    }

    /// <summary>
    /// GET the partitions sibling of a resource collection, with route qualifiers.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="ResourcePath" />, the same composer <see cref="GetResourceAsync" /> uses,
    /// so a routed partitions request cannot silently lose the tenant segment or a qualifier the
    /// collection request keeps.
    /// </remarks>
    public async Task<HttpResponseMessage> GetPartitionsAsync(
        string districtId,
        string schoolYear,
        string resource
    )
    {
        var url = $"{ResourcePath(districtId, schoolYear, resource)}/partitions";

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
    /// GET availableChangeVersions for a route-qualified instance.
    /// </summary>
    public async Task<HttpResponseMessage> GetAvailableChangeVersionsAsync(
        string districtId,
        string schoolYear,
        string? useSnapshot = null
    )
    {
        var url = BuildPath($"/{districtId}/{schoolYear}/changeQueries/v1/availableChangeVersions");

        return await SendAsync(HttpMethod.Get, url, useSnapshot: useSnapshot);
    }

    /// <summary>
    /// GET a tracked-change endpoint (segment = "deletes" or "keyChanges") for a route-qualified instance.
    /// </summary>
    public async Task<HttpResponseMessage> GetTrackedChangesAsync(
        string districtId,
        string schoolYear,
        string resource,
        string segment,
        string? useSnapshot = null
    )
    {
        var url = $"{ResourcePath(districtId, schoolYear, resource)}/{segment}";

        return await SendAsync(HttpMethod.Get, url, useSnapshot: useSnapshot);
    }

    /// <summary>
    /// DELETE a resource by its stored Location header value.
    /// </summary>
    public async Task<HttpResponseMessage> DeleteByLocationAsync(string location)
    {
        return await _httpClient.DeleteAsync(location);
    }

    /// <summary>
    /// PUT a resource by its stored Location header value.
    /// </summary>
    public async Task<HttpResponseMessage> PutByLocationAsync(string location, object body)
    {
        return await _httpClient.PutAsJsonAsync(location, body);
    }

    /// <summary>
    /// GET a resource without route qualifiers (for error testing)
    /// </summary>
    public async Task<HttpResponseMessage> GetResourceWithoutQualifiersAsync(string resource)
    {
        var url = BuildPath($"/data/ed-fi/{resource}");
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

    /// <summary>
    /// Get XSD metadata with tenant prefix
    /// </summary>
    public async Task<HttpResponseMessage> GetXsdMetadataWithTenantAsync(string tenant)
    {
        var url = $"/{tenant}/metadata/xsd";

        // Use shared HttpClient for unauthenticated requests
        if (string.IsNullOrEmpty(_accessToken))
        {
            var fullUrl = $"{_baseUrl}{url}";
            var response = await _sharedHttpClient.GetAsync(fullUrl);
            return response;
        }

        var authenticatedResponse = await _httpClient.GetAsync(url);
        return authenticatedResponse;
    }

    /// <summary>
    /// Get a specific XSD file under a tenant prefix
    /// </summary>
    public async Task<HttpResponseMessage> GetXsdFileWithTenantAsync(
        string tenant,
        string section,
        string fileName
    )
    {
        var url = $"/{tenant}/metadata/xsd/{section}/{fileName}.xsd";

        // Use shared HttpClient for unauthenticated requests
        if (string.IsNullOrEmpty(_accessToken))
        {
            var fullUrl = $"{_baseUrl}{url}";
            var response = await _sharedHttpClient.GetAsync(fullUrl);
            return response;
        }

        var authenticatedResponse = await _httpClient.GetAsync(url);
        return authenticatedResponse;
    }

    /// <summary>
    /// GET view-claimsets management endpoint (tenant-aware)
    /// </summary>
    public async Task<HttpResponseMessage> GetViewClaimsetsAsync(string? tenant = null)
    {
        var url = tenant != null ? $"/management/{tenant}/view-claimsets" : "/management/view-claimsets";

        var fullUrl = $"{_baseUrl}{url}";
        var response = await _sharedHttpClient.GetAsync(fullUrl);
        return response;
    }

    /// <summary>
    /// POST reload-claimsets management endpoint (tenant-aware)
    /// </summary>
    public async Task<HttpResponseMessage> PostReloadClaimsetsAsync(string? tenant = null)
    {
        var url = tenant != null ? $"/management/{tenant}/reload-claimsets" : "/management/reload-claimsets";

        var fullUrl = $"{_baseUrl}{url}";
        var response = await _sharedHttpClient.PostAsync(fullUrl, null);
        return response;
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
