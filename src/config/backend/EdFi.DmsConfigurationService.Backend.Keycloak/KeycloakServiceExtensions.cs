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
        var uri = new Uri(Authority);
        var baseUrl = uri.GetLeftPart(UriPartial.Authority);
        var realm = Authority.TrimEnd('/').Split('/').Last();

        services.AddScoped(x => new KeycloakContext(baseUrl, realm, ClientId, ClientSecret, RoleClaimType));

        services.AddTransient<IClientRepository, KeycloakClientRepository>();
        services.AddTransient<ITokenManager, KeycloakTokenManager>();

        return services;
    }
}
