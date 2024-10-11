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
            .Where(p => moduleInterface.IsAssignableFrom(p) && p.IsClass);

        var modules = new List<IEndpointModule>();

        foreach (var moduleClass in moduleClasses)
        {
            if (Activator.CreateInstance(moduleClass) is IEndpointModule module)
                modules.Add(module);
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
