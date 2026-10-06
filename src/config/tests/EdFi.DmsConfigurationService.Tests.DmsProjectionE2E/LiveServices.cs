// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E;

/// <summary>An application's first client, as CMS issues it.</summary>
public sealed record ClientCredentials(string Tenant, int ApplicationId, string Key, string Secret);

/// <summary>
/// The Configuration Service and DMS endpoints the fixture provisions and seeds through. Every call that does not
/// answer as expected throws <see cref="InvalidOperationException"/> naming the call and its status, never a
/// credential, token or connection string.
/// </summary>
public sealed class LiveServices(ProjectionE2EEnvironment environment) : IDisposable
{
    public const string ProjectionClaimName =
        "http://ed-fi.org/identity/claims/services/educationOrganizationProjection";

    private readonly HttpClient _http = new();
    private string? _adminToken;

    public async Task<string> AdminTokenAsync()
    {
        if (_adminToken is not null)
        {
            return _adminToken;
        }

        using FormUrlEncodedContent form = new(
            new Dictionary<string, string>
            {
                ["client_id"] = environment.AdminClientId,
                ["client_secret"] = environment.AdminClientSecret,
                ["grant_type"] = "client_credentials",
                ["scope"] = "edfi_admin_api/full_access",
            }
        );
        using HttpResponseMessage response = await _http.PostAsync(Cms("connect/token"), form);
        _adminToken = await ReadAccessTokenAsync(response, "the Configuration Service admin token");
        return _adminToken;
    }

    /// <summary>A DMS token for an application client, issued by the Configuration Service.</summary>
    public async Task<string> ClientTokenAsync(ClientCredentials client)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, Cms("connect/token"))
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" }
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    $"{Uri.EscapeDataString(client.Key)}:{Uri.EscapeDataString(client.Secret)}"
                )
            )
        );
        using HttpResponseMessage response = await _http.SendAsync(request);
        return await ReadAccessTokenAsync(response, $"a token for application {client.ApplicationId}");
    }

    /// <summary>
    /// Imports a claim set granting exactly Read on the projection claim and reads it back: CMS skips a claim missing
    /// from its hierarchy instead of rejecting the import, which would otherwise surface later as a 403.
    /// </summary>
    public async Task<int> ImportProjectionClaimSetAsync(string tenant, string claimSetName)
    {
        JsonObject payload = new()
        {
            ["claimSetName"] = claimSetName,
            ["resourceClaims"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "educationOrganizationProjection",
                    ["claimName"] = ProjectionClaimName,
                    ["actions"] = new JsonArray(new JsonObject { ["name"] = "Read", ["enabled"] = true }),
                }
            ),
        };
        using HttpResponseMessage imported = await CmsSendAsync(
            HttpMethod.Post,
            tenant,
            "v3/claimSets/import",
            payload
        );
        int claimSetId = await CreatedIdAsync(imported, $"importing claim set {claimSetName}");

        using HttpResponseMessage exported = await CmsSendAsync(
            HttpMethod.Get,
            tenant,
            $"v3/claimSets/{claimSetId}/export"
        );
        JsonNode export = await RequireJsonAsync(
            exported,
            HttpStatusCode.OK,
            $"exporting claim set {claimSetName}"
        );
        string[] grants =
        [
            .. export["resourceClaims"]!
                .AsArray()
                .SelectMany(claim =>
                    claim!["actions"]!
                        .AsArray()
                        .Where(action => action!["enabled"]!.GetValue<bool>())
                        .Select(action =>
                            $"{claim["claimName"]!.GetValue<string>()}#{action!["name"]!.GetValue<string>()}"
                        )
                ),
        ];
        if (grants is not [$"{ProjectionClaimName}#Read"])
        {
            throw new InvalidOperationException(
                $"Setup failed: claim set {claimSetName} grants [{string.Join(", ", grants)}], "
                    + "not exactly Read on the projection claim."
            );
        }

        return claimSetId;
    }

    public async Task<ClientCredentials> CreateApplicationAsync(
        string tenant,
        string name,
        string claimSetName,
        IReadOnlyList<int> dataStoreIds
    )
    {
        using HttpResponseMessage response = await CmsSendAsync(
            HttpMethod.Post,
            tenant,
            "v3/applications",
            new JsonObject
            {
                ["vendorId"] = environment.VendorIdsByTenant[tenant],
                ["applicationName"] = name,
                ["claimSetName"] = claimSetName,
                ["educationOrganizationIds"] = new JsonArray(),
                ["dataStoreIds"] = new JsonArray([.. dataStoreIds.Select(id => (JsonNode)id)]),
            }
        );
        JsonNode application = await RequireJsonAsync(
            response,
            HttpStatusCode.Created,
            $"creating application {name}"
        );
        return new ClientCredentials(
            tenant,
            application["id"]!.GetValue<int>(),
            application["key"]!.GetValue<string>(),
            application["secret"]!.GetValue<string>()
        );
    }

    /// <summary>Registers a data store with the given route contexts and returns its id.</summary>
    public async Task<int> CreateDataStoreAsync(
        string tenant,
        string name,
        string connectionString,
        IReadOnlyDictionary<string, string> contexts
    )
    {
        using HttpResponseMessage created = await CmsSendAsync(
            HttpMethod.Post,
            tenant,
            "v3/dataStores",
            new JsonObject
            {
                ["dataStoreType"] = "Local",
                ["name"] = name,
                ["connectionString"] = connectionString,
                ["provider"] = environment.DatabaseEngine == "mssql" ? "sqlserver" : "postgresql",
            }
        );
        JsonNode dataStore = await RequireJsonAsync(
            created,
            HttpStatusCode.Created,
            $"registering data store {name}"
        );
        int dataStoreId = dataStore["id"]!.GetValue<int>();

        foreach ((string key, string value) in contexts)
        {
            using HttpResponseMessage context = await CmsSendAsync(
                HttpMethod.Post,
                tenant,
                "v3/dataStoreContexts",
                new JsonObject
                {
                    ["dataStoreId"] = dataStoreId,
                    ["contextKey"] = key,
                    ["contextValue"] = value,
                }
            );
            await RequireJsonAsync(
                context,
                HttpStatusCode.Created,
                $"adding context {key} to data store {name}"
            );
        }

        return dataStoreId;
    }

    /// <summary>The data store's route contexts as the CMS catalog holds them, which is what a CMS job reads.</summary>
    public async Task<IReadOnlyDictionary<string, string>> DataStoreContextsAsync(
        string tenant,
        int dataStoreId
    )
    {
        using HttpResponseMessage response = await CmsSendAsync(
            HttpMethod.Get,
            tenant,
            $"v3/dataStores/{dataStoreId}"
        );
        JsonNode dataStore = await RequireJsonAsync(
            response,
            HttpStatusCode.OK,
            $"reading data store {dataStoreId}"
        );
        return dataStore["dataStoreContexts"]!
            .AsArray()
            .ToDictionary(
                context => context!["contextKey"]!.GetValue<string>(),
                context => context!["contextValue"]!.GetValue<string>(),
                StringComparer.Ordinal
            );
    }

    public Task DeleteApplicationAsync(string tenant, int id) =>
        CmsDeleteAsync(tenant, $"v3/applications/{id}");

    public Task DeleteClaimSetAsync(string tenant, int id) => CmsDeleteAsync(tenant, $"v3/claimSets/{id}");

    public Task DeleteDataStoreAsync(string tenant, int id) => CmsDeleteAsync(tenant, $"v3/dataStores/{id}");

    /// <summary>Makes DMS read the tenant's claim sets again, so a claim set imported after it started is known.</summary>
    public async Task ReloadClaimSetsAsync(string tenant)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            Dms($"management/{Uri.EscapeDataString(tenant)}/reload-claimsets")
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());
        using HttpResponseMessage response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Setup failed: reloading the claim sets of {tenant} answered {(int)response.StatusCode}."
            );
        }
    }

    /// <summary>Writes one resource document and returns its location; anything but 201 or the allowed 200 throws.</summary>
    public async Task<(HttpStatusCode Status, string? Location)> PostResourceAsync(
        string token,
        FixtureRoute route,
        string resource,
        JsonObject body,
        bool allowExisting
    )
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            Dms($"{RoutePath(route)}/data/ed-fi/{resource}")
        )
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await _http.SendAsync(request);

        if (
            response.StatusCode == HttpStatusCode.Created
            || (allowExisting && response.StatusCode == HttpStatusCode.OK)
        )
        {
            return (response.StatusCode, response.Headers.Location?.ToString());
        }

        throw new InvalidOperationException(
            $"Setup failed: writing {resource} to {route.Tenant}/{route.Qualifier} answered "
                + $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}"
        );
    }

    public async Task<HttpStatusCode> DeleteResourceAsync(string token, string location)
    {
        using HttpRequestMessage request = new(HttpMethod.Delete, new Uri(environment.DmsBaseUrl, location));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await _http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Reads one projection page directly, outside the reader: the empty-store precondition.
    /// </summary>
    public async Task<(HttpStatusCode Status, string Body)> GetProjectionPageAsync(
        string token,
        FixtureRoute route
    )
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            Dms(
                $"{RoutePath(route)}/management/education-organizations?dataStoreId="
                    + route.DataStoreId.ToString(CultureInfo.InvariantCulture)
            )
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await _http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => _http.Dispose();

    private static string RoutePath(FixtureRoute route) =>
        $"{Uri.EscapeDataString(route.Tenant)}/{route.DistrictId}/{route.SchoolYear}";

    private Uri Cms(string path) => new(environment.ConfigServiceUrl, path);

    private Uri Dms(string path) => new(environment.DmsBaseUrl, path);

    private async Task<HttpResponseMessage> CmsSendAsync(
        HttpMethod method,
        string tenant,
        string path,
        JsonNode? body = null
    )
    {
        using HttpRequestMessage request = new(method, Cms(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());
        request.Headers.Add("Tenant", tenant);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _http.SendAsync(request);
    }

    private async Task CmsDeleteAsync(string tenant, string path)
    {
        using HttpResponseMessage response = await CmsSendAsync(HttpMethod.Delete, tenant, path);
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            throw new InvalidOperationException(
                $"Cleanup failed: deleting {path} in {tenant} answered {(int)response.StatusCode}."
            );
        }
    }

    private static async Task<int> CreatedIdAsync(HttpResponseMessage response, string action)
    {
        if (response.StatusCode != HttpStatusCode.Created || response.Headers.Location is null)
        {
            throw new InvalidOperationException(
                $"Setup failed: {action} answered {(int)response.StatusCode}: "
                    + await response.Content.ReadAsStringAsync()
            );
        }

        return int.Parse(
            response.Headers.Location.ToString().Split('/')[^1],
            NumberStyles.None,
            CultureInfo.InvariantCulture
        );
    }

    private static async Task<JsonNode> RequireJsonAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string action
    )
    {
        string body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException(
                $"Setup failed: {action} answered {(int)response.StatusCode}, expected {(int)expected}: {body}"
            );
        }

        return JsonNode.Parse(body.Length == 0 ? "{}" : body)!;
    }

    private static async Task<string> ReadAccessTokenAsync(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Setup failed: requesting {what} answered {(int)response.StatusCode}."
            );
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("access_token").GetString()!;
    }
}
