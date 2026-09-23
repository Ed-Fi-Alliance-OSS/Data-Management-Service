// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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

        var response = await client.PostAsJsonAsync("/v3/tenants/", request);

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

        var response = await client.GetAsync("/v3/tenants/");
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

        var response = await _httpClient.PostAsJsonAsync("/v3/vendors", request);
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
            location = $"{_httpClient.BaseAddress}v3/vendors/{vendor.Id}";
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
    /// Create a new data store
    /// </summary>
    public async Task<InstanceResponse> CreateInstanceAsync(InstanceRequest request)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.PostAsJsonAsync("/v3/dataStores", request);
        response.EnsureSuccessStatusCode();

        var instance =
            await response.Content.ReadFromJsonAsync<InstanceResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize instance response");

        return instance;
    }

    /// <summary>
    /// Create a route context for a data store
    /// </summary>
    public async Task<RouteContextResponse> CreateRouteContextAsync(RouteContextRequest request)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.PostAsJsonAsync("/v3/dataStoreContexts", request);
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

        var response = await _httpClient.PostAsJsonAsync("/v3/applications", request);
        response.EnsureSuccessStatusCode();

        var application =
            await response.Content.ReadFromJsonAsync<ApplicationResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize application response");

        return application;
    }

    /// <summary>
    /// Create an API client under an application. The first client an application-create call
    /// mints is read back through <see cref="GetApiClientAsync"/>; this method mints every
    /// additional client.
    /// </summary>
    public async Task<(ApiClientCredentialsResponse Response, string Location)> CreateApiClientAsync(
        ApiClientRequest request
    )
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.PostAsJsonAsync("/v3/apiClients/", request);
        response.EnsureSuccessStatusCode();

        var location =
            response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("Failed to read the created API client's location.");

        var credentials =
            await response.Content.ReadFromJsonAsync<ApiClientCredentialsResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize API client credentials response");

        return (credentials, location);
    }

    /// <summary>
    /// Get an API client by its numeric identifier or its OAuth client key.
    /// </summary>
    public async Task<ApiClientResponse> GetApiClientAsync(string idOrClientKey)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.GetAsync($"/v3/apiClients/{idOrClientKey}");
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ApiClientResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize API client response");
    }

    /// <summary>
    /// Replace an API client's stored assignment (a full replacement, per the endpoint's own
    /// contract), typically used to retain an empty Data Store assignment across an update.
    /// </summary>
    public async Task UpdateApiClientAsync(int id, ApiClientRequest request)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.PutAsJsonAsync(
            $"/v3/apiClients/{id}",
            new
            {
                Id = id,
                request.ApplicationId,
                request.Name,
                request.IsApproved,
                request.DataStoreIds,
            }
        );
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Reset an API client's credentials, returning the newly issued secret. The prior secret is
    /// unrecoverable once this call succeeds.
    /// </summary>
    public async Task<ApiClientCredentialsResponse> ResetApiClientCredentialsAsync(int id)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.PutAsync($"/v3/apiClients/{id}/reset-credential", null);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ApiClientCredentialsResponse>()
            ?? throw new InvalidOperationException(
                "Failed to deserialize reset API client credentials response"
            );
    }

    /// <summary>
    /// Delete an API client.
    /// </summary>
    public async Task DeleteApiClientAsync(int id)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.DeleteAsync($"/v3/apiClients/{id}");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Imports (creates, or replaces in place when the name already exists - the endpoint's own
    /// upsert-by-name contract) a claim set whose only resource claim is the identity service
    /// claim, granting exactly the given actions. An empty action list produces a claim set that
    /// grants nothing, which the identity revocation E2E uses to simulate an administrator
    /// removing the identity claim from a claim set.
    /// </summary>
    public async Task<int> ImportIdentityOnlyClaimSetAsync(string claimSetName, params string[] actions)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        object payload =
            actions.Length == 0
                ? new { claimSetName, resourceClaims = Array.Empty<object>() }
                : new
                {
                    claimSetName,
                    resourceClaims = new object[]
                    {
                        new
                        {
                            name = "identity",
                            claimName = "http://ed-fi.org/identity/claims/services/identity",
                            actions = actions
                                .Select(action => new { name = action, enabled = true })
                                .ToArray(),
                        },
                    },
                };

        var response = await _httpClient.PostAsJsonAsync("/v3/claimSets/import", payload);
        response.EnsureSuccessStatusCode();

        var location =
            response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("Failed to read the imported claim set's location.");

        return int.Parse(location.Split('/')[^1], NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Delete a claim set by id.
    /// </summary>
    public async Task DeleteClaimSetAsync(int claimSetId)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.DeleteAsync($"/v3/claimSets/{claimSetId}");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Delete a data store
    /// </summary>
    public async Task DeleteInstanceAsync(int dataStoreId)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _accessToken
        );

        var response = await _httpClient.DeleteAsync($"/v3/dataStores/{dataStoreId}");
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

        var response = await _httpClient.DeleteAsync($"/v3/applications/{applicationId}");
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

        var response = await _httpClient.DeleteAsync($"/v3/vendors/{vendorId}");
        response.EnsureSuccessStatusCode();
    }
}
