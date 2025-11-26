// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;
using Microsoft.AspNetCore.Routing;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public static class WebApplicationExtensions
{
    public static void MapRouteEndpoints(this WebApplication application)
    {
        var moduleInterface = typeof(IEndpointModule);
        var moduleClasses = Assembly
            .GetExecutingAssembly()
            .GetTypes()
            .Where(p => moduleInterface.IsAssignableFrom(p) && p.IsClass && !p.IsGenericType);

        var identityProvider = application.Configuration["AppSettings:IdentityProvider"];
        var multiTenancyEnabled = application.Configuration.GetValue<bool>("AppSettings:MultiTenancy");
        var modules = new List<IEndpointModule>();

        foreach (var moduleClass in moduleClasses)
        {
            // Exclude OpenID modules when not using self-contained identity provider
            if (!string.Equals(identityProvider, "self-contained", StringComparison.OrdinalIgnoreCase))
            {
                if (
                    moduleClass.Name == typeof(JwksEndpointModule).Name
                    || moduleClass.Name == typeof(OpenIdConfigurationModule).Name
                )
                {
                    continue;
                }
            }

            // Exclude TenantModule when multi-tenancy is disabled
            if (!multiTenancyEnabled && moduleClass.Name == typeof(TenantModule).Name)
            {
                continue;
            }

            if (Activator.CreateInstance(moduleClass) is IEndpointModule module)
            {
                modules.Add(module);
            }
        }
        application.UseEndpoints(endpoints =>
        {
            foreach (var routeBuilder in modules)
            {
                routeBuilder.MapEndpoints(endpoints);
            }
        });
    }
}
