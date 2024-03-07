// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Api.Content;

namespace EdFi.DataManagementService.Api.Modules;

public class DependenciesModule : IModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metadata/data/dependencies", GetDependencies);
    }

    internal async Task GetDependencies(
        HttpContext httpContext,
        IOpenApiContentProvider dependenciesContentProvider
    )
    {
        var content = await dependenciesContentProvider.GetContent();
        await httpContext.Response.WriteAsJsonAsync(content.First().metadata.Value);
    }
}
