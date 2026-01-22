// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that filters POST/PUT request bodies according to profile WriteContentType rules.
/// Fields excluded by the profile are silently stripped from the request before processing.
/// For POST requests, also validates that required fields are not excluded by the profile.
/// This middleware runs after ValidateDocumentMiddleware to filter validated request bodies.
/// </summary>
internal class ProfileWriteValidationMiddleware(
    IProfileResponseFilter profileFilter,
    IProfileCreatabilityValidator creatabilityValidator,
    ILogger<ProfileWriteValidationMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Only process if profile context with WriteContentType exists
        ContentTypeDefinition? writeContentType = requestInfo
            .ProfileContext
            ?.ResourceProfile
            .WriteContentType;

        if (writeContentType == null)
        {
            await next();
            return;
        }

        // Get the request body - only process JSON objects
        if (requestInfo.ParsedBody is not JsonObject requestBody)
        {
            await next();
            return;
        }

        logger.LogDebug(
            "Applying profile write filter. Profile: {ProfileName} - {TraceId}",
            SanitizeForLog(requestInfo.ProfileContext!.ProfileName),
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Pre-compute identity property names for reuse
        HashSet<string> identityPropertyNames = profileFilter.ExtractIdentityPropertyNames(
            requestInfo.ResourceSchema.IdentityJsonPaths
        );

        // For POST requests only, validate that the profile doesn't exclude required fields
        // PUT requests skip this check because existing resources already have the required values
        if (requestInfo.Method == RequestMethod.POST)
        {
            IReadOnlyList<string> excludedRequiredFields = creatabilityValidator.GetExcludedRequiredFields(
                requestInfo.ResourceSchema.RequiredFieldsForInsert,
                writeContentType,
                identityPropertyNames
            );

            if (excludedRequiredFields.Count > 0)
            {
                logger.LogDebug(
                    "Profile {ProfileName} excludes required fields: {ExcludedFields} - {TraceId}",
                    SanitizeForLog(requestInfo.ProfileContext.ProfileName),
                    SanitizeForLog(string.Join(", ", excludedRequiredFields)),
                    requestInfo.FrontendRequest.TraceId.Value
                );

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForDataPolicyEnforced(
                        requestInfo.ProfileContext.ProfileName,
                        requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                );
                return;
            }
        }

        // Filter the request body according to profile rules
        JsonNode filteredBody = profileFilter.FilterDocument(
            requestBody,
            writeContentType,
            identityPropertyNames
        );

        // Replace request body with filtered version
        requestInfo.ParsedBody = filteredBody;

        logger.LogDebug(
            "Profile write filter applied successfully - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        await next();
    }

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return new string(
            input
                .Where(c =>
                    char.IsLetterOrDigit(c)
                    || c == ' '
                    || c == '_'
                    || c == '-'
                    || c == '.'
                    || c == ':'
                    || c == '/'
                )
                .ToArray()
        );
    }
}
