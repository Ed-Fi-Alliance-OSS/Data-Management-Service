// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that resolves and validates API profile headers, populating ProfileContext in RequestInfo
/// </summary>
internal class ProfileResolutionMiddleware(
    IProfileService profileService,
    IApplicationContextProvider applicationContextProvider,
    ILogger<ProfileResolutionMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ProfileResolutionMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Get the appropriate header based on request method
        string? headerValue = GetProfileHeader(requestInfo);

        // Parse the header
        ProfileHeaderParseResult parseResult = ProfileHeaderParser.Parse(headerValue);
        if (!parseResult.IsSuccess)
        {
            logger.LogDebug(
                "Profile header parse failed: {Error} - {TraceId}",
                LoggingSanitizer.SanitizeForLogging(parseResult.ErrorMessage ?? "Unknown error"),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = CreateProfileError(
                statusCode: 400,
                errorType: "urn:ed-fi:api:profile:invalid-profile-usage",
                title: "Invalid Profile Usage",
                detail: "The request construction was invalid with respect to usage of a data policy.",
                errors: [parseResult.ErrorMessage ?? "Invalid profile header format."],
                traceId: requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        // Get application context to find ApplicationId
        ApplicationContext? appContext = await applicationContextProvider.GetApplicationByClientIdAsync(
            requestInfo.ClientAuthorizations.ClientId
        );

        if (appContext == null)
        {
            logger.LogWarning(
                "Application context not found for client during profile resolution. ClientId: {ClientId} - {TraceId}",
                LoggingSanitizer.SanitizeForLogging(requestInfo.ClientAuthorizations.ClientId),
                requestInfo.FrontendRequest.TraceId.Value
            );

            // If no application context and no profile header, continue without profile
            if (parseResult.ParsedHeader == null)
            {
                await next();
                return;
            }

            // If profile header was specified but we can't find app context, return error
            requestInfo.FrontendResponse = CreateProfileError(
                statusCode: GetNotFoundStatusCode(requestInfo.Method),
                errorType: "urn:ed-fi:api:profile:invalid-profile-usage",
                title: "Invalid Profile Usage",
                detail: "The request construction was invalid with respect to usage of a data policy.",
                errors: ["Unable to resolve application context for profile validation."],
                traceId: requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        // Get tenant ID if multi-tenancy is enabled
        string? tenantId = requestInfo.FrontendRequest.Tenant;

        // Resolve the profile
        ProfileResolutionResult resolutionResult = await profileService.ResolveProfileAsync(
            parseResult.ParsedHeader,
            requestInfo.Method,
            requestInfo.ResourceSchema.ResourceName.Value,
            appContext.ApplicationId,
            tenantId
        );

        if (!resolutionResult.IsSuccess)
        {
            logger.LogDebug(
                "Profile resolution failed: {Error} - {TraceId}",
                LoggingSanitizer.SanitizeForLogging(resolutionResult.Error?.Title ?? "Unknown error"),
                requestInfo.FrontendRequest.TraceId.Value
            );

            ProfileResolutionError error = resolutionResult.Error!;
            requestInfo.FrontendResponse = CreateProfileError(
                statusCode: error.StatusCode,
                errorType: error.ErrorType,
                title: error.Title,
                detail: error.Detail,
                errors: error.Errors,
                traceId: requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        // Set profile context if a profile applies
        if (resolutionResult.ProfileContext != null)
        {
            requestInfo.ProfileContext = resolutionResult.ProfileContext;
            logger.LogDebug(
                "Profile resolved successfully. Profile: {ProfileName}, Explicit: {WasExplicit} - {TraceId}",
                LoggingSanitizer.SanitizeForLogging(resolutionResult.ProfileContext.ProfileName),
                resolutionResult.ProfileContext.WasExplicitlySpecified,
                requestInfo.FrontendRequest.TraceId.Value
            );
        }
        else
        {
            logger.LogDebug(
                "No profile applies to this request - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
        }

        await next();
    }

    private static string? GetProfileHeader(RequestInfo requestInfo)
    {
        // GET uses Accept header, POST/PUT use Content-Type header
        string? headerName = requestInfo.Method switch
        {
            RequestMethod.GET => "Accept",
            RequestMethod.POST => "Content-Type",
            RequestMethod.PUT => "Content-Type",
            _ => null,
        };

        if (headerName == null)
        {
            return null;
        }

        return requestInfo.FrontendRequest.Headers.TryGetValue(headerName, out var value) ? value : null;
    }

    private static int GetNotFoundStatusCode(RequestMethod method)
    {
        // GET uses 406 Not Acceptable, POST/PUT use 415 Unsupported Media Type
        return method switch
        {
            RequestMethod.GET => 406,
            RequestMethod.POST => 415,
            RequestMethod.PUT => 415,
            _ => throw new InvalidOperationException($"Unexpected method for profile resolution: {method}"),
        };
    }

    private static FrontendResponse CreateProfileError(
        int statusCode,
        string errorType,
        string title,
        string detail,
        string[] errors,
        TraceId traceId
    )
    {
        var responseBody = new JsonObject
        {
            ["detail"] = detail,
            ["type"] = errorType,
            ["title"] = title,
            ["status"] = statusCode,
            ["correlationId"] = traceId.Value,
            ["errors"] = JsonSerializer.SerializeToNode(errors, SerializerOptions),
        };

        return new FrontendResponse(
            StatusCode: statusCode,
            Body: responseBody,
            Headers: [],
            ContentType: "application/problem+json"
        );
    }
}
