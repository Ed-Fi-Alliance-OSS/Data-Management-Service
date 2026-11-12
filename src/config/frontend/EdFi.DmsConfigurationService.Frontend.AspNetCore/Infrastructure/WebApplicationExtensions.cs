// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

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
            if (
                !string.Equals(identityProvider, "self-contained", StringComparison.OrdinalIgnoreCase)
                && (
                    moduleClass.Name == typeof(JwksEndpointModule).Name
                    || moduleClass.Name == typeof(OpenIdConfigurationModule).Name
                )
            )
            {
                continue;
            }

            // Exclude TenantModule when multi-tenancy is disabled
            if (!multiTenancyEnabled && moduleClass.Name == typeof(TenantModule).Name)
            {
                continue;
            }

            var module =
                ActivatorUtilities.CreateInstance(application.Services, moduleClass) as IEndpointModule;
            if (module != null)
            {
                modules.Add(module);
            }
        }
        application.UseEndpoints(endpoints =>
        {
            foreach (var routeBuilder in modules)
            {
                // An empty-prefix group leaves every route pattern unchanged and carries the tag, which
                // also covers lambda handlers that would otherwise be tagged with the assembly name.
                var group = endpoints.MapGroup(string.Empty);
                if (OpenApiModuleTags.ByModule.TryGetValue(routeBuilder.GetType(), out var tag))
                {
                    group.WithTags(tag);
                }

                routeBuilder.MapEndpoints(group);
            }
        });
    }
}
