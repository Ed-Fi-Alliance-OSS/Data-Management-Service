// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

/// <summary>
/// Service extensions to register keycloak access points
/// </summary>
public static class KeycloakServiceExtensions
{
    public static IServiceCollection AddKeycloakServices(
        this IServiceCollection services,
        string Authority,
        string ClientId,
        string ClientSecret,
        string RoleClaimType
    )
    {
        var errorMessage =
            "An exception occurred while validating the Keycloak discovery endpoint. Please verify that the authority URL is correct, especially the realm segment, and ensure it is accessible.";

        var discoveryUrl = $"{Authority}/.well-known/openid-configuration";
        var baseUrl = string.Empty;
        var realm = string.Empty;

        using var httpClient = new HttpClient();
        try
        {
            var response = Task.Run(() => httpClient.GetAsync(discoveryUrl)).Result;
            if (response.IsSuccessStatusCode)
            {
                var uri = new Uri(Authority);
                baseUrl = uri.GetLeftPart(UriPartial.Authority);
                realm = Authority.TrimEnd('/').Split('/').Last();
            }
            else
            {
                throw new InvalidOperationException(errorMessage);
            }
        }
        catch
        {
            throw new InvalidOperationException(errorMessage);
        }

        services.AddScoped(x => new KeycloakContext(baseUrl, realm, ClientId, ClientSecret, RoleClaimType));

        services.AddTransient<IClientRepository, KeycloakClientRepository>();
        services.AddTransient<ITokenManager, KeycloakTokenManager>();

        return services;
    }
}
