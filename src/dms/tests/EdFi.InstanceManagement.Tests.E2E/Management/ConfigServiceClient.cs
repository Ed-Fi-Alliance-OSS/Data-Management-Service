// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using EdFi.InstanceManagement.Tests.E2E.Models;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Client for interacting with the Configuration Service API
/// </summary>
public class ConfigServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _accessToken;

    /// <summary>
    /// Creates a new ConfigServiceClient
    /// </summary>
    /// <param name="baseUrl">Base URL of the Configuration Service</param>
    /// <param name="accessToken">Bearer token for authentication</param>
    /// <param name="tenantName">Optional tenant name for multi-tenant support</param>
    public ConfigServiceClient(string baseUrl, string accessToken, string? tenantName = null)
    {
        _baseUrl = baseUrl;
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _accessToken = accessToken;

        if (!string.IsNullOrEmpty(tenantName))
        {
            _httpClient.DefaultRequestHeaders.Add("Tenant", tenantName);
        }
    }

    /// <summary>
    /// Create a tenant. This is a system-level operation that doesn't require the Tenant header.
    /// </summary>
    public async Task<TenantResponse> CreateTenantAsync(TenantRequest request)
    {
        // Tenant operations don't use the Tenant header - create a fresh client
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await client.PostAsJsonAsync("/v2/tenants/", request);

        // If tenant already exists (400 Bad Request with duplicate name), try to get existing tenant
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var existingTenants = await GetTenantsAsync();
            var existing = Array.Find(
                existingTenants,
                t => t.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)
            );
            if (existing != null)
            {
                return existing;
            }
        }

        response.EnsureSuccessStatusCode();

        var tenant =
            await response.Content.ReadFromJsonAsync<TenantResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize tenant response");

        return tenant;
    }

    /// <summary>
    /// Get all tenants. This is a system-level operation that doesn't require the Tenant header.
    /// </summary>
    public async Task<TenantResponse[]> GetTenantsAsync()
    {
        // Tenant operations don't use the Tenant header - create a fresh client
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await client.GetAsync("/v2/tenants/");
        response.EnsureSuccessStatusCode();

        var tenants =
            await response.Content.ReadFromJsonAsync<TenantResponse[]>()
            ?? throw new InvalidOperationException("Failed to deserialize tenants response");

        return tenants;
    }

    /// <summary>
    /// Ensure a tenant exists, creating it if necessary.
    /// </summary>
    public async Task<TenantResponse> EnsureTenantExistsAsync(string tenantName)
    {
        var existingTenants = await GetTenantsAsync();
        var existing = Array.Find(
            existingTenants,
            t => t.Name.Equals(tenantName, StringComparison.OrdinalIgnoreCase)
        );
        if (existing != null)
        {
            return existing;
        }

        return await CreateTenantAsync(new TenantRequest(tenantName));
    }

    /// <summary>
    /// Create a new vendor
    /// </summary>
    public async Task<(VendorResponse Response, string Location)> CreateVendorAsync(VendorRequest request)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.PostAsJsonAsync("/v2/vendors", request);
        response.EnsureSuccessStatusCode();

        // Try to get location header first
        var location = response.Headers.Location?.ToString();

        VendorResponse vendor;
        if (location != null)
        {
            // Get the vendor to extract the ID using the location
            vendor = await GetVendorByLocationAsync(location);
        }
        else
        {
            // If no location header, the response body should contain the vendor
            vendor =
                await response.Content.ReadFromJsonAsync<VendorResponse>()
                ?? throw new InvalidOperationException("Failed to deserialize vendor response");

            // Construct location from vendor ID
            location = $"{_httpClient.BaseAddress}v2/vendors/{vendor.Id}";
        }

        return (vendor, location);
    }

    /// <summary>
    /// Get vendor by location URL
    /// </summary>
    public async Task<VendorResponse> GetVendorByLocationAsync(string location)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.GetAsync(location);
        response.EnsureSuccessStatusCode();

        var vendor =
            await response.Content.ReadFromJsonAsync<VendorResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize vendor response");

        return vendor;
    }

    /// <summary>
    /// Create a new DMS instance
    /// </summary>
    public async Task<InstanceResponse> CreateInstanceAsync(InstanceRequest request)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.PostAsJsonAsync("/v2/dmsInstances", request);
        response.EnsureSuccessStatusCode();

        var instance =
            await response.Content.ReadFromJsonAsync<InstanceResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize instance response");

        return instance;
    }

    /// <summary>
    /// Create a route context for an instance
    /// </summary>
    public async Task<RouteContextResponse> CreateRouteContextAsync(RouteContextRequest request)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.PostAsJsonAsync("/v2/dmsInstanceRouteContexts", request);
        response.EnsureSuccessStatusCode();

        var routeContext =
            await response.Content.ReadFromJsonAsync<RouteContextResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize route context response");

        return routeContext;
    }

    /// <summary>
    /// Create an application
    /// </summary>
    public async Task<ApplicationResponse> CreateApplicationAsync(ApplicationRequest request)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.PostAsJsonAsync("/v2/applications", request);
        response.EnsureSuccessStatusCode();

        var application =
            await response.Content.ReadFromJsonAsync<ApplicationResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize application response");

        return application;
    }

    /// <summary>
    /// Delete an instance
    /// </summary>
    public async Task DeleteInstanceAsync(int instanceId)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.DeleteAsync($"/v2/dmsInstances/{instanceId}");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Delete an application
    /// </summary>
    public async Task DeleteApplicationAsync(int applicationId)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.DeleteAsync($"/v2/applications/{applicationId}");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Delete a vendor
    /// </summary>
    public async Task DeleteVendorAsync(int vendorId)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.DeleteAsync($"/v2/vendors/{vendorId}");
        response.EnsureSuccessStatusCode();
    }
}
