// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

/// <summary>
/// Management endpoints for claims administrative tasks
/// </summary>
public class ClaimsManagementModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var managementEndpoints = endpoints.MapGroup("/management");

        // Reload Claims endpoint
        managementEndpoints
            .MapPost("/reload-claims", ReloadClaims)
            .WithName("ReloadClaims")
            .WithSummary("Reloads the Claims from the configured source");

        // Upload Claims endpoint
        managementEndpoints
            .MapPost("/upload-claims", UploadClaims)
            .WithName("UploadClaims")
            .WithSummary("Uploads Claims from request body")
            .Accepts<UploadClaimsRequest>("application/json")
            .Produces<UploadClaimsResponse>(200)
            .Produces(400)
            .Produces(404)
            .Produces(500);

        // Get Current Claims endpoint
        managementEndpoints
            .MapGet("/current-claims", GetCurrentClaims)
            .WithName("GetCurrentClaims")
            .WithSummary("Retrieves the currently loaded claims in their original format")
            .Produces<JsonNode>(200)
            .Produces(404)
            .Produces(500);
    }

    internal static async Task<IResult> ReloadClaims(
        IClaimsUploadService claimsUploadService,
        IClaimsProvider claimsProvider,
        IOptions<ClaimsOptions> claimsOptions,
        ILogger<ClaimsManagementModule> logger
    )
    {
        // Check if dynamic claims loading is enabled
        if (!claimsOptions.Value.DangerouslyEnableUnrestrictedClaimsLoading)
        {
            logger.LogWarning(
                "Claims reload requested but DangerouslyEnableUnrestrictedClaimsLoading is disabled"
            );
            return Results.NotFound();
        }

        logger.LogInformation("Claims reload requested via management endpoint");

        try
        {
            var status = await claimsUploadService.ReloadClaimsAsync();

            if (status.Success)
            {
                logger.LogInformation(
                    "Claims reloaded successfully with reload ID {ReloadId}",
                    claimsProvider.ReloadId
                );
                return Results.Ok(new ReloadClaimsResponse(Success: true, ReloadId: claimsProvider.ReloadId));
            }

            logger.LogError("Claims reload failed with {FailureCount} failures", status.Failures.Count);
            return Results.Json(
                new ReloadClaimsResponse(
                    Success: false,
                    Errors: status
                        .Failures.Select(f => new ClaimsReloadError(
                            ErrorType: f.FailureType,
                            Message: f.Message
                        ))
                        .ToList()
                ),
                statusCode: 500
            );
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON error during claims reload");
            return Results.Json(
                new ReloadClaimsResponse(
                    Success: false,
                    Errors: new List<ClaimsReloadError>
                    {
                        new ClaimsReloadError(
                            ErrorType: "JsonError",
                            Message: "Invalid JSON format: " + ex.Message
                        ),
                    }
                ),
                statusCode: 400
            );
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation during claims reload");
            return Results.Json(
                new ReloadClaimsResponse(
                    Success: false,
                    Errors: new List<ClaimsReloadError>
                    {
                        new ClaimsReloadError(ErrorType: "OperationError", Message: ex.Message),
                    }
                ),
                statusCode: 500
            );
        }
    }

    internal static async Task<IResult> UploadClaims(
        UploadClaimsRequest request,
        IClaimsUploadService claimsUploadService,
        IClaimsProvider claimsProvider,
        IOptions<ClaimsOptions> claimsOptions,
        ILogger<ClaimsManagementModule> logger
    )
    {
        // Check if dynamic claims loading is enabled
        if (!claimsOptions.Value.DangerouslyEnableUnrestrictedClaimsLoading)
        {
            logger.LogWarning(
                "Claims upload requested but DangerouslyEnableUnrestrictedClaimsLoading is disabled"
            );
            return Results.NotFound();
        }

        logger.LogInformation("Claims upload requested via management endpoint");

        if (request?.Claims == null)
        {
            return Results.Json(
                UploadClaimsResponse.Failed(
                    new List<ClaimsUploadError>
                    {
                        new ClaimsUploadError(
                            ErrorType: "ValidationError",
                            Message: "Claims JSON is required"
                        ),
                    }
                ),
                statusCode: 400
            );
        }

        try
        {
            var status = await claimsUploadService.UploadClaimsAsync(request.Claims);

            if (status.Success)
            {
                logger.LogInformation(
                    "Claims uploaded successfully with reload ID {ReloadId}",
                    claimsProvider.ReloadId
                );
                return Results.Json(
                    UploadClaimsResponse.Successful(claimsProvider.ReloadId),
                    statusCode: 200
                );
            }

            logger.LogError("Claims upload failed with {FailureCount} failures", status.Failures.Count);
            return Results.Json(
                UploadClaimsResponse.Failed(
                    status
                        .Failures.Select(f => new ClaimsUploadError(
                            ErrorType: f.FailureType,
                            Message: f.Message,
                            Path: f.Path
                        ))
                        .ToList()
                ),
                statusCode: 400
            );
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON error during claims upload");
            return Results.Json(
                UploadClaimsResponse.Failed(
                    new List<ClaimsUploadError>
                    {
                        new ClaimsUploadError(
                            ErrorType: "JsonError",
                            Message: "Invalid JSON format: " + ex.Message
                        ),
                    }
                ),
                statusCode: 400
            );
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Invalid argument during claims upload");
            return Results.Json(
                UploadClaimsResponse.Failed(
                    new List<ClaimsUploadError>
                    {
                        new ClaimsUploadError(ErrorType: "ArgumentError", Message: ex.Message),
                    }
                ),
                statusCode: 400
            );
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation during claims upload");
            return Results.Json(
                UploadClaimsResponse.Failed(
                    new List<ClaimsUploadError>
                    {
                        new ClaimsUploadError(ErrorType: "OperationError", Message: ex.Message),
                    }
                ),
                statusCode: 500
            );
        }
    }

    internal static IResult GetCurrentClaims(
        IClaimsProvider claimsProvider,
        IOptions<ClaimsOptions> claimsOptions,
        HttpContext httpContext,
        ILogger<ClaimsManagementModule> logger
    )
    {
        // Check if dynamic claims loading is enabled
        if (!claimsOptions.Value.DangerouslyEnableUnrestrictedClaimsLoading)
        {
            logger.LogWarning(
                "Current claims requested but DangerouslyEnableUnrestrictedClaimsLoading is disabled"
            );
            return Results.NotFound();
        }

        logger.LogInformation("Current claims requested via management endpoint");

        try
        {
            // Get current claims document
            ClaimsDocument claimsDocument = claimsProvider.GetClaimsDocumentNodes();

            // Reconstruct the original JSON format with both claimSets and claimsHierarchy
            // We need to deep copy the nodes to avoid "node already has a parent" error
            JsonObject originalFormat = new()
            {
                ["claimSets"] = JsonNode.Parse(claimsDocument.ClaimSetsNode.ToJsonString())!,
                ["claimsHierarchy"] = JsonNode.Parse(claimsDocument.ClaimsHierarchyNode.ToJsonString())!,
            };

            // Add ReloadId as response header
            httpContext.Response.Headers.Append("X-Reload-Id", claimsProvider.ReloadId.ToString());

            logger.LogInformation(
                "Returning current claims with reload ID {ReloadId}",
                claimsProvider.ReloadId
            );

            // Return the claims directly without wrapping in CurrentClaimsResponse
            // to match the exact upload format
            return Results.Ok(originalFormat);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON error while retrieving current claims");
            return Results.Json(new { error = "JSON format error", message = ex.Message }, statusCode: 500);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation while retrieving current claims");
            return Results.Json(new { error = "Invalid operation", message = ex.Message }, statusCode: 500);
        }
    }
}
