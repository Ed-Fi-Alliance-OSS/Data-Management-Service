// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Api.Configuration;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Api.Modules;
using Microsoft.Extensions.Options;

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

    public static void UseValidationErrorsHandlingMiddleware(this WebApplication application)
    {
        InjectInvalidConfigurationMiddleware(application);
        InjectInvalidApiSchemaMiddleware(application);
    }

    private static void InjectInvalidConfigurationMiddleware(WebApplication app)
    {
        try
        {
            // Accessing IOptions<T> forces validation
            _ = app.Services.GetRequiredService<IOptions<AppSettings>>().Value;
            _ = app.Services.GetRequiredService<IOptions<ConnectionStrings>>().Value;
        }
        catch (OptionsValidationException ex)
        {
            app.UseMiddleware<InvalidConfigurationMiddleware>(ex.Failures);
        }
    }

    private static void InjectInvalidApiSchemaMiddleware(WebApplication app)
    {
        var apiSchemaProvider = app.Services.GetRequiredService<IApiSchemaProvider>();
        var apiSchema = apiSchemaProvider.ApiSchemaRootNode;

        var validator = app.Services.GetRequiredService<IApiSchemaValidator>();
        var validationErrors = validator.Validate(apiSchema).Value;
        if (validationErrors.Any())
        {
            app.UseMiddleware<InvalidApiSchemaMiddleware>(validationErrors);
        }
    }
}
