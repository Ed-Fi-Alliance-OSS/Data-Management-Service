// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Api.Modules;

namespace EdFi.DataManagementService.Api.Infrastructure;

public static class WebApplicationExtensions
{
    public static void MapRouteEndpoints(this WebApplication application)
    {
        var moduleInterface = typeof(IModule);
        var moduleImpls = Assembly
            .GetExecutingAssembly()
            .GetTypes()
            .Where(p => moduleInterface.IsAssignableFrom(p) && p.IsClass);

        var modules = new List<IModule>();

        foreach (var moduleImpl in moduleImpls)
        {
            if (Activator.CreateInstance(moduleImpl) is IModule module)
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
