// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FluentValidation.Results;
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
            .MapSecuredPost("/reload-claims", ReloadClaims)
            .WithName("ReloadClaims")
            .WithSummary("Reloads the Claims from the configured source");

        // Upload Claims endpoint
        managementEndpoints
            .MapSecuredPost("/upload-claims", UploadClaims)
            .WithName("UploadClaims")
            .WithSummary("Uploads Claims from request body")
            .Accepts<UploadClaimsRequest>("application/json")
            .Produces<UploadClaimsResponse>(200)
            .Produces(400)
            .Produces(404)
            .Produces(500);

        // Get Current Claims endpoint
        managementEndpoints
            .MapSecuredGet("/current-claims", GetCurrentClaims)
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
        HttpContext httpContext,
        ILogger<ClaimsManagementModule> logger
    )
    {
        // Check if dynamic claims loading is enabled
        if (!claimsOptions.Value.DangerouslyEnableUnrestrictedClaimsLoading)
        {
            logger.LogWarning(
                "Claims reload requested but DangerouslyEnableUnrestrictedClaimsLoading is disabled"
            );
            return FailureResults.NotFound(
                "Claims reload endpoint is not available.",
                httpContext.TraceIdentifier
            );
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

            // Reload reads a server-managed claims source and accepts no client input, so any failure —
            // including a malformed (JSON) source — is a server-side error (500), not a client 400. The
            // service catches JSON/argument/operational errors internally and reports them as failures
            // (it does not throw). The failure detail is logged server-side only and never surfaced.
            logger.LogError(
                "Claims reload failed with a server-side error: {Failures}",
                DescribeFailures(status.Failures)
            );
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        catch (JsonException ex)
        {
            // A malformed server-managed claims source is a server-side error, not a client 400.
            logger.LogError(ex, "JSON error during claims reload");
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation during claims reload");
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
    }

    internal static async Task<IResult> UploadClaims(
        UploadClaimsRequest request,
        IClaimsUploadService claimsUploadService,
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
                "Claims upload requested but DangerouslyEnableUnrestrictedClaimsLoading is disabled"
            );
            return FailureResults.NotFound(
                "Claims upload endpoint is not available.",
                httpContext.TraceIdentifier
            );
        }

        logger.LogInformation("Claims upload requested via management endpoint");

        if (request?.Claims is null)
        {
            return FailureResults.DataValidation(
                [new ValidationFailure("Claims", "Claims JSON is required.")],
                httpContext.TraceIdentifier
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

            return ClassifyClaimsFailures(
                status.Failures,
                "The claims upload request was invalid.",
                httpContext,
                logger,
                "upload"
            );
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON error during claims upload");
            return FailureResults.BadRequest(
                "The request body could not be parsed as valid claims JSON.",
                httpContext.TraceIdentifier
            );
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Invalid argument during claims upload");
            return FailureResults.BadRequest(
                "The claims upload request was invalid.",
                httpContext.TraceIdentifier
            );
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation during claims upload");
            return FailureResults.Unknown(httpContext.TraceIdentifier);
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
            return FailureResults.NotFound(
                "Current claims endpoint is not available.",
                httpContext.TraceIdentifier
            );
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
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation while retrieving current claims");
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
    }

    // Maps a failed ClaimsLoadStatus to the appropriate Ed-Fi response. Server-side failures (database,
    // operational, unexpected) return a safe 500 with the details logged server-side only; malformed
    // source input (invalid JSON / arguments) returns a generic 400; genuine data-validation failures
    // return a 400 with grouped validationErrors. Shared by the reload and upload endpoints because the
    // service reports the same failure classifications for both.
    private static IResult ClassifyClaimsFailures(
        List<ClaimsFailure> failures,
        string badRequestDetail,
        HttpContext httpContext,
        ILogger logger,
        string operation
    )
    {
        if (failures.Exists(IsServerSideFailure))
        {
            logger.LogError(
                "Claims {Operation} failed with a server-side error: {Failures}",
                operation,
                DescribeFailures(failures)
            );
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        if (failures.Exists(failure => BadRequestFailureTypes.Contains(failure.FailureType)))
        {
            logger.LogError(
                "Claims {Operation} rejected as a bad request: {Failures}",
                operation,
                DescribeFailures(failures)
            );
            return FailureResults.BadRequest(badRequestDetail, httpContext.TraceIdentifier);
        }

        logger.LogError(
            "Claims {Operation} failed validation with {FailureCount} failures",
            operation,
            failures.Count
        );
        return FailureResults.DataValidation(
            failures.Select(failure => new ValidationFailure(
                string.IsNullOrEmpty(failure.Path) ? failure.FailureType : failure.Path,
                failure.Message
            )),
            httpContext.TraceIdentifier
        );
    }

    // ClaimsFailure.FailureType values from IClaimsUploadService that represent genuine client
    // data-validation failures whose messages are safe to surface in grouped validationErrors.
    private static readonly HashSet<string> DataValidationFailureTypes = new(StringComparer.Ordinal)
    {
        "Validation",
        "Structure",
    };

    // FailureType values that represent malformed client input: a generic 400 with no echoed detail.
    private static readonly HashSet<string> BadRequestFailureTypes = new(StringComparer.Ordinal)
    {
        "JsonError",
        "ArgumentError",
    };

    // Any failure that is not a recognized client-side failure (e.g. Database, Unexpected, Unknown,
    // OperationError) is treated as a server-side error and must not leak its message to the client.
    private static bool IsServerSideFailure(ClaimsFailure failure) =>
        !DataValidationFailureTypes.Contains(failure.FailureType)
        && !BadRequestFailureTypes.Contains(failure.FailureType);

    private static string DescribeFailures(IEnumerable<ClaimsFailure> failures) =>
        string.Join("; ", failures.Select(failure => $"[{failure.FailureType}] {failure.Message}"));
}
