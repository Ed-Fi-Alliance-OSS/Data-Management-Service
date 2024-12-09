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
        string Url,
        string Realm,
        string ClientId,
        string ClientSecret,
        string RoleClaimType,
        string Scope
    )
    {
        services.AddScoped(x => new KeycloakContext(
            Url,
            Realm,
            ClientId,
            ClientSecret,
            RoleClaimType,
            Scope
        ));

        services.AddTransient<IClientRepository, KeycloakClientRepository>();
        services.AddTransient<ITokenManager, KeycloakTokenManager>();

        return services;
    }
}
