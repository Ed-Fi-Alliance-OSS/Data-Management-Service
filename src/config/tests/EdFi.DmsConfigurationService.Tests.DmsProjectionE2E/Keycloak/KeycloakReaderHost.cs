// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Keycloak;

/// <summary>
/// One request the reader sent: its method, its URL, and the <c>iss</c> claim of the bearer token it carried, if any.
/// The token itself is never kept.
/// </summary>
public sealed record ObservedRequest(HttpMethod Method, Uri Url, string? BearerIssuer);

/// <summary>Records every request the reader sends on its way to DMS, unchanged.</summary>
public sealed class ObservingHandler(List<ObservedRequest> requests) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        lock (requests)
        {
            requests.Add(new ObservedRequest(request.Method, request.RequestUri!, BearerIssuer(request)));
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>The <c>iss</c> of a bearer JWT, decoded in memory; a bearer value that is not a JWT reads as such.</summary>
    private static string? BearerIssuer(HttpRequestMessage request)
    {
        if (
            request.Headers.Authorization is not { Parameter: { } token } authorization
            || !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            return "<not a JWT>";
        }

        using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        return payload.RootElement.TryGetProperty("iss", out JsonElement issuer) ? issuer.GetString() : null;
    }
}

/// <summary>
/// The production reader registration
/// (<see cref="ServiceCollectionExtensions.AddDmsEducationOrganizationProjectionReader"/>) with a single-tenant
/// credential and the DMS base URL, plus the observing handler on its named client.
/// </summary>
public sealed class KeycloakReaderHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<ObservedRequest> _requests = [];

    public KeycloakReaderHost(Uri dmsBaseUrl, ClientCredentials credential)
    {
        string section = DmsEducationOrganizationProjectionSettings.SectionName;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddHttpClient();
        services.AddDmsEducationOrganizationProjectionReader(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{section}:DmsBaseUrl"] = dmsBaseUrl.ToString().TrimEnd('/'),
                        [$"{section}:PageSize"] = 2.ToString(CultureInfo.InvariantCulture),
                        [$"{section}:Credentials:ClientId"] = credential.Key,
                        [$"{section}:Credentials:ClientSecret"] = credential.Secret,
                    }
                )
                .Build()
        );
        services
            .AddHttpClient(DmsEducationOrganizationProjectionHttpClient.Name)
            .AddHttpMessageHandler(() => new ObservingHandler(_requests));
        _provider = services.BuildServiceProvider(validateScopes: true);

        // What ValidateOnStart does in a host: the settings must pass the production validator.
        _ = _provider.GetRequiredService<IOptions<DmsEducationOrganizationProjectionSettings>>().Value;
    }

    /// <summary>The type the registration resolves for the reader interface.</summary>
    public Type ReaderType =>
        _provider.GetRequiredService<IEducationOrganizationProjectionReader>().GetType();

    public IReadOnlyList<ObservedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>A single-tenant read: no tenant name.</summary>
    public Task<EducationOrganizationProjectionReadResult> ReadAsync(
        int dataStoreId,
        IReadOnlyDictionary<string, string> contexts
    ) =>
        _provider
            .GetRequiredService<IEducationOrganizationProjectionReader>()
            .ReadAllAsync(
                new EducationOrganizationProjectionReadRequest(null, dataStoreId, contexts),
                CancellationToken.None
            );

    public void Dispose() => _provider.Dispose();
}
