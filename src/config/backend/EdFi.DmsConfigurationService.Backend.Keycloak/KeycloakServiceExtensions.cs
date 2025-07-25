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

        var realmsUriSegmentIndex = uri
            .Segments.ToList()
            .FindIndex(segment => segment.Equals("realms/", StringComparison.OrdinalIgnoreCase));

        if (realmsUriSegmentIndex < 0)
        {
            throw new InvalidOperationException(
                $"The 'realms/' segment is missing from the '{nameof(Authority)}' URL."
            );
        }

        var baseUrl = new Uri(uri, string.Concat(uri.Segments.Take(realmsUriSegmentIndex)))
            .ToString()
            .Trim('/');

        var realm = uri.Segments.Skip(realmsUriSegmentIndex + 1).Take(1).Single().Trim('/');

        services.AddScoped(x => new KeycloakContext(baseUrl, realm, ClientId, ClientSecret, RoleClaimType));

        services.AddTransient<IClientRepository, KeycloakClientRepository>();
        services.AddTransient<ITokenManager, KeycloakTokenManager>();

        return services;
    }
}
