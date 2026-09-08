// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public static class WebApplicationExtensions
{
    public static void MapRouteEndpoints(this WebApplication application)
    {
        var moduleInterface = typeof(IEndpointModule);
        var moduleClasses = Assembly
            .GetExecutingAssembly()
            .GetTypes()
            .Where(p => moduleInterface.IsAssignableFrom(p) && p.IsClass);

        List<IEndpointModule> modules = [];

        foreach (var moduleClass in moduleClasses)
        {
            if (
                ActivatorUtilities.CreateInstance(application.Services, moduleClass) is IEndpointModule module
            )
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
