// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal enum CdcApiResource
{
    Student,
    SchoolTypeDescriptor,
}

/// <summary>HTTP-only mutations adapted from the hosted projector and completed-projection scenarios.
/// Base URLs (including any route context) and target ID come from the retained handoff resolution.
/// No feature hooks, database reset, static endpoints, or data-store registration.</summary>
internal sealed class CdcApiClient : IDisposable
{
    private readonly HttpClient _dms;
    private readonly HttpClient _cms;
    private readonly int _dataStoreId;

    public CdcApiClient(Uri dmsBaseUrl, Uri cmsBaseUrl, int dataStoreId)
        : this(dmsBaseUrl, cmsBaseUrl, dataStoreId, new HttpClientHandler(), new HttpClientHandler()) { }

    internal CdcApiClient(
        Uri dmsBaseUrl,
        Uri cmsBaseUrl,
        int dataStoreId,
        HttpMessageHandler dmsHandler,
        HttpMessageHandler cmsHandler
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dataStoreId);
        _dataStoreId = dataStoreId;
        _dms = new(dmsHandler) { BaseAddress = BaseUrl(dmsBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _cms = new(cmsHandler) { BaseAddress = BaseUrl(cmsBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task AuthenticateAsync(CancellationToken token)
    {
        var registration = await SystemAdministrator.RegisterAsync(
            _cms,
            $"CdcE2E{Guid.NewGuid():N}",
            SystemAdministrator.DefaultClientSecret,
            token
        );
        _cms.DefaultRequestHeaders.Authorization = new("Bearer", registration.Token);

        // Select the already-admitted target by identity, never a default name or a new registration.
        using HttpResponseMessage dataStore = await _cms.GetAsync($"v3/dataStores/{_dataStoreId}", token);
        RequireStatus(dataStore, HttpStatusCode.OK, "CMS target selection");
        using JsonDocument target = JsonDocument.Parse(await dataStore.Content.ReadAsStringAsync(token));
        if (target.RootElement.GetProperty("id").GetInt32() != _dataStoreId)
        {
            throw new InvalidOperationException("CMS returned a different data-store identity.");
        }

        using StringContent vendorContent = JsonContent(
            new JsonObject
            {
                ["company"] = $"CDC E2E {Guid.NewGuid():N}"[..30],
                ["contactName"] = "CDC E2E",
                ["contactEmailAddress"] = "cdc-e2e@example.com",
                ["namespacePrefixes"] = "uri://ed-fi.org",
            }
        );
        using HttpResponseMessage vendor = await _cms.PostAsync("v3/vendors", vendorContent, token);
        RequireStatus(vendor, HttpStatusCode.Created, "CMS vendor creation");
        // Follow the CMS path at the resolved host endpoint, even if Location advertises a container host.
        int vendorId = int.Parse(
            RequiredLocation(vendor).Segments[^1],
            System.Globalization.CultureInfo.InvariantCulture
        );
        using HttpResponseMessage vendorGet = await _cms.GetAsync($"v3/vendors/{vendorId}", token);
        RequireStatus(vendorGet, HttpStatusCode.OK, "CMS vendor retrieval");
        using JsonDocument vendorJson = JsonDocument.Parse(await vendorGet.Content.ReadAsStringAsync(token));
        string applicationJson = AuthorizationDataProvider.CreateApplicationRequestJson(
            vendorJson.RootElement.GetProperty("id").GetInt32(),
            AuthorizationClaimSetNames.NoFurtherAuthRequired,
            [],
            _dataStoreId
        );
        using StringContent applicationContent = new(applicationJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage application = await _cms.PostAsync(
            "v3/applications",
            applicationContent,
            token
        );
        RequireStatus(application, HttpStatusCode.Created, "CMS application creation");
        using JsonDocument credentials = JsonDocument.Parse(
            await application.Content.ReadAsStringAsync(token)
        );
        using var request = new HttpRequestMessage(HttpMethod.Post, "oauth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            OAuthClientCredentialsEncoder.CreateBasicSchemeParameter(
                credentials.RootElement.GetProperty("key").GetString()!,
                credentials.RootElement.GetProperty("secret").GetString()!
            )
        );
        request.Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]);
        using HttpResponseMessage response = await _dms.SendAsync(request, token);
        RequireStatus(response, HttpStatusCode.OK, "DMS token acquisition");
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        _dms.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            result.RootElement.GetProperty("access_token").GetString()!
        );
    }

    public async Task<Guid> PostAsync(CdcApiResource resource, JsonObject body, CancellationToken token)
    {
        using StringContent content = JsonContent(body);
        using HttpResponseMessage response = await _dms.PostAsync(ResourcePath(resource), content, token);
        RequireStatus(response, HttpStatusCode.Created, "DMS create");
        return Guid.Parse(RequiredLocation(response).Segments[^1]);
    }

    public async Task PutAsync(CdcApiResource resource, Guid id, JsonObject body, CancellationToken token)
    {
        JsonObject payload = (JsonObject)body.DeepClone();
        payload["id"] = id.ToString("D");
        using StringContent content = JsonContent(payload);
        using HttpResponseMessage response = await _dms.PutAsync(
            $"{ResourcePath(resource)}/{id:D}",
            content,
            token
        );
        RequireStatus(response, HttpStatusCode.NoContent, "DMS update");
    }

    public async Task<JsonObject> GetAsync(CdcApiResource resource, Guid id, CancellationToken token)
    {
        using HttpResponseMessage response = await _dms.GetAsync($"{ResourcePath(resource)}/{id:D}", token);
        RequireStatus(response, HttpStatusCode.OK, "DMS read");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!.AsObject();
    }

    public async Task DeleteAsync(CdcApiResource resource, Guid id, CancellationToken token)
    {
        using HttpResponseMessage response = await _dms.DeleteAsync(
            $"{ResourcePath(resource)}/{id:D}",
            token
        );
        RequireStatus(response, HttpStatusCode.NoContent, "DMS delete");
    }

    public static JsonObject NewStudent(string firstName) =>
        new()
        {
            ["studentUniqueId"] = $"cdc-{Guid.NewGuid():N}"[..32],
            ["firstName"] = firstName,
            ["lastSurname"] = "CDC",
            ["birthDate"] = "2010-05-01",
        };

    public static JsonObject NewSchoolTypeDescriptor(string shortDescription) =>
        new()
        {
            ["namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
            ["codeValue"] = $"CDC-{Guid.NewGuid():N}",
            ["shortDescription"] = shortDescription,
        };

    private static string ResourcePath(CdcApiResource resource) =>
        resource switch
        {
            CdcApiResource.Student => "data/ed-fi/students",
            CdcApiResource.SchoolTypeDescriptor => "data/ed-fi/schoolTypeDescriptors",
            _ => throw new ArgumentOutOfRangeException(nameof(resource)),
        };

    private static Uri BaseUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != "http" && url.Scheme != "https"))
        {
            throw new ArgumentException("An absolute HTTP endpoint is required.");
        }
        return new Uri(url.AbsoluteUri.TrimEnd('/') + "/");
    }

    private static Uri RequiredLocation(HttpResponseMessage response) =>
        new(
            new Uri("http://location.invalid/"),
            response.Headers.Location
                ?? throw new InvalidOperationException("Successful create did not return Location.")
        );

    private static StringContent JsonContent(JsonObject body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static void RequireStatus(HttpResponseMessage response, HttpStatusCode status, string operation)
    {
        if (response.StatusCode != status)
        {
            // Response bodies can echo domain data or credentials. Keep failure diagnostics payload-free.
            throw new InvalidOperationException(
                $"{operation} failed: HTTP {(int)response.StatusCode}; expected {(int)status}."
            );
        }
    }

    public void Dispose()
    {
        _dms.Dispose();
        _cms.Dispose();
    }
}
