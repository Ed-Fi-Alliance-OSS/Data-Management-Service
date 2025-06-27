// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Management endpoints for administrative tasks
/// </summary>
public class ManagementEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var managementEndpoints = endpoints.MapGroup("/management");

        // Reload API schema endpoint
        managementEndpoints
            .MapPost("/reload-schema", ReloadApiSchema)
            .WithName("ReloadApiSchema")
            .WithSummary("Reloads the API schema from the configured source");

        // Upload and reload API schema endpoint
        managementEndpoints
            .MapPost("/upload-and-reload-schema", UploadAndReloadApiSchema)
            .WithName("UploadAndReloadApiSchema")
            .WithSummary("Uploads and reloads API schemas from request body")
            .Accepts<UploadSchemaRequest>("application/json")
            .Produces<UploadSchemaResponse>(200)
            .Produces(400)
            .Produces(404)
            .Produces(500);
    }

    internal static async Task<IResult> ReloadApiSchema(
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        logger.LogInformation("Schema reload requested via management endpoint");

        var response = await apiService.ReloadApiSchemaAsync();

        return response.StatusCode switch
        {
            200 => Results.Ok(response.Body),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
    }

    internal static async Task<IResult> UploadAndReloadApiSchema(
        UploadSchemaRequest request,
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        logger.LogInformation("Schema upload and reload requested via management endpoint");

        var response = await apiService.UploadAndReloadApiSchemaAsync(request);

        if (response.Success)
        {
            return Results.Ok(response);
        }

        // Handle different error scenarios using explicit flags
        if (response.IsManagementEndpointsDisabled)
        {
            return Results.NotFound();
        }

        if (response.IsValidationError)
        {
            return Results.BadRequest(response);
        }

        return Results.Json(response, statusCode: 500);
    }
}
