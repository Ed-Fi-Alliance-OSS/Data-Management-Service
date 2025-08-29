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

        // Reload ApiSchema endpoint
        managementEndpoints
            .MapPost("/reload-api-schema", ReloadApiSchema)
            .WithName("ReloadApiSchema")
            .WithSummary("Reloads the ApiSchema from the configured source");

        // Upload ApiSchema endpoint
        managementEndpoints
            .MapPost("/upload-api-schema", UploadApiSchema)
            .WithName("UploadApiSchema")
            .WithSummary("Uploads ApiSchema from request body")
            .Accepts<UploadSchemaRequest>("application/json")
            .Produces<UploadSchemaResponse>(200)
            .Produces(400)
            .Produces(404)
            .Produces(500);

        // Reload Claimsets endpoint
        managementEndpoints
            .MapPost("/reload-claimsets", ReloadClaimsets)
            .WithName("ReloadClaimsets")
            .WithSummary("Reloads the Claimsets from the configured source");

        // View Claimsets endpoint
        managementEndpoints
            .MapGet("/view-claimsets", ViewClaimsets)
            .WithName("ViewClaimsets")
            .WithSummary("Views the current Claimsets configuration");
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

    internal static async Task<IResult> UploadApiSchema(
        UploadSchemaRequest request,
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        logger.LogInformation("Schema upload requested via management endpoint");

        var response = await apiService.UploadApiSchemaAsync(request);

        return response.StatusCode switch
        {
            200 => Results.Json(response.Body, statusCode: 200),
            400 => Results.Json(response.Body, statusCode: 400),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
    }

    internal static async Task<IResult> ReloadClaimsets(
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        logger.LogInformation("Claimsets reload requested via management endpoint");

        var response = await apiService.ReloadClaimsetsAsync();

        return response.StatusCode switch
        {
            200 => Results.Ok(response.Body),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
    }

    internal static async Task<IResult> ViewClaimsets(
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        logger.LogInformation("View claimsets requested via management endpoint");

        var response = await apiService.ViewClaimsetsAsync();

        return response.StatusCode switch
        {
            200 => Results.Ok(response.Body),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
    }
}
