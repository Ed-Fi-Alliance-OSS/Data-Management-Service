// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that resolves the appropriate DMS instance based on route qualifiers
/// and client DmsInstanceIds from the JWT token
/// </summary>
internal class ResolveDmsInstanceMiddleware(
    IDmsInstanceProvider dmsInstanceProvider,
    IDmsInstanceSelection dmsInstanceSelection,
    ILogger<ResolveDmsInstanceMiddleware> logger
) : IPipelineStep
{
    /// <summary>
    /// Resolves the DMS instance by matching route qualifiers with authorized instances
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Validate ClientAuthorizations.DmsInstanceIds not empty
        if (requestInfo.ClientAuthorizations.DmsInstanceIds.Count == 0)
        {
            logger.LogError(
                "No DMS instances authorized for client - TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );

            requestInfo.FrontendResponse = CreateErrorResponse(
                403,
                "Authorization Denied",
                "No database instances are authorized for this client",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        // Get route qualifiers from the request
        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers = requestInfo
            .FrontendRequest
            .RouteQualifiers;

        // Try to find matching DMS instance
        DmsInstance? matchedInstance = await TryFindMatchingInstance(
            requestInfo,
            requestQualifiers,
            reloadOnCacheMiss: false
        );

        // If no match found and we haven't tried reloading yet, attempt cache reload
        if (matchedInstance == null)
        {
            logger.LogInformation(
                "No matching DMS instance found in cache, attempting reload from CMS - TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );

            try
            {
                await dmsInstanceProvider.LoadDmsInstances();

                // Retry matching after reload
                matchedInstance = await TryFindMatchingInstance(
                    requestInfo,
                    requestQualifiers,
                    reloadOnCacheMiss: true
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to reload DMS instances from CMS - TraceId: {TraceId}",
                    LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
                );
            }
        }

        // If still no match found after reload, error out
        if (matchedInstance == null)
        {
            // Check if an error response was already set (e.g., by TryFindMatchingInstance for ambiguous routing)
            if (requestInfo.FrontendResponse != No.FrontendResponse)
            {
                // Error already set (e.g., multiple matches), just return
                return;
            }

            string qualifierDetails =
                requestQualifiers.Count > 0
                    ? string.Join(
                        ", ",
                        requestQualifiers.Select(kv =>
                            $"{LoggingSanitizer.SanitizeForLogging(kv.Key.Value)}={LoggingSanitizer.SanitizeForLogging(kv.Value.Value)}"
                        )
                    )
                    : "(none)";

            List<long> checkedInstanceIds = requestInfo
                .ClientAuthorizations.DmsInstanceIds.Select(id => id.Value)
                .ToList();

            logger.LogError(
                "No DMS instance matches route qualifiers [{QualifierDetails}] from authorized instances [{InstanceIds}] - TraceId: {TraceId}",
                qualifierDetails,
                LoggingSanitizer.SanitizeForLogging(string.Join(", ", checkedInstanceIds)),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );

            requestInfo.FrontendResponse = CreateErrorResponse(
                404,
                "Route Resolution Error",
                "No database instance found matching the request route qualifiers",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        // Validate connection string
        if (string.IsNullOrWhiteSpace(matchedInstance.ConnectionString))
        {
            logger.LogError(
                "DMS instance {DmsInstanceId} has no connection string configured - TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(matchedInstance.Id.ToString()),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );

            requestInfo.FrontendResponse = CreateErrorResponse(
                503,
                "Service Configuration Error",
                "Database connection not configured for the matched instance",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        // Set selected DMS instance in scoped provider for repository access
        dmsInstanceSelection.SetSelectedDmsInstance(matchedInstance);

        logger.LogDebug(
            "Selected DMS instance {DmsInstanceId} ('{InstanceName}') - TraceId: {TraceId}",
            LoggingSanitizer.SanitizeForLogging(matchedInstance.Id.ToString()),
            LoggingSanitizer.SanitizeForLogging(matchedInstance.InstanceName),
            LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
        );

        // Continue to next middleware
        await next();
    }

    /// <summary>
    /// Attempts to find a DMS instance matching the route qualifiers from authorized instances
    /// </summary>
    private Task<DmsInstance?> TryFindMatchingInstance(
        RequestInfo requestInfo,
        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers,
        bool reloadOnCacheMiss
    )
    {
        DmsInstance? matchedInstance = null;

#pragma warning disable S3267 // Loops should be simplified with "LINQ" expressions - False positive: this loop has complex logic with early exits and side effects
        foreach (DmsInstanceId dmsInstanceId in requestInfo.ClientAuthorizations.DmsInstanceIds)
#pragma warning restore S3267
        {
            DmsInstance? dmsInstance = dmsInstanceProvider.GetById(dmsInstanceId.Value);

            if (dmsInstance == null)
            {
                string logMessage = reloadOnCacheMiss
                    ? "DMS instance {DmsInstanceId} still not found after cache reload - TraceId: {TraceId}"
                    : "DMS instance {DmsInstanceId} not found in cache - TraceId: {TraceId}";

                logger.LogWarning(
                    logMessage,
                    LoggingSanitizer.SanitizeForLogging(dmsInstanceId.Value.ToString()),
                    LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
                );
                continue;
            }

            // Check if this instance's route context matches the request qualifiers
            bool isMatch = IsRouteContextMatch(dmsInstance.RouteContext, requestQualifiers);

            if (isMatch)
            {
                if (matchedInstance != null)
                {
                    // Multiple matches - not supported
                    logger.LogError(
                        "Multiple DMS instances match route qualifiers (instances {FirstId} and {SecondId}) - TraceId: {TraceId}",
                        LoggingSanitizer.SanitizeForLogging(matchedInstance.Id.ToString()),
                        LoggingSanitizer.SanitizeForLogging(dmsInstance.Id.ToString()),
                        LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
                    );

                    requestInfo.FrontendResponse = CreateErrorResponse(
                        400,
                        "Route Resolution Error",
                        "Multiple database instances match the request route qualifiers - ambiguous routing not supported",
                        requestInfo.FrontendRequest.TraceId
                    );
                    return Task.FromResult<DmsInstance?>(null);
                }

                matchedInstance = dmsInstance;
            }
        }

        return Task.FromResult(matchedInstance);
    }

    /// <summary>
    /// Checks if the DMS instance's route context matches the request's route qualifiers
    /// </summary>
    private static bool IsRouteContextMatch(
        Dictionary<RouteQualifierName, RouteQualifierValue> instanceRouteContext,
        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers
    )
    {
        // Both must have same number of qualifiers
        if (instanceRouteContext.Count != requestQualifiers.Count)
        {
            return false;
        }

        // If no qualifiers, it's a match (both empty)
        if (instanceRouteContext.Count == 0)
        {
            return true;
        }

        // All qualifier names must match
        if (!instanceRouteContext.Keys.All(requestQualifiers.ContainsKey))
        {
            return false;
        }

        // All qualifier values must match
        foreach (KeyValuePair<RouteQualifierName, RouteQualifierValue> kvp in instanceRouteContext)
        {
            if (
                !requestQualifiers.TryGetValue(kvp.Key, out RouteQualifierValue requestValue)
                || !kvp.Value.Value.Equals(requestValue.Value, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates a standardized error response with problem details format.
    /// </summary>
    private static FrontendResponse CreateErrorResponse(
        int statusCode,
        string title,
        string errorDetail,
        TraceId traceId
    )
    {
        var problemDetails = new
        {
            detail = errorDetail,
            type = $"urn:ed-fi:api:{title.ToLower().Replace(" ", "-")}",
            title,
            status = statusCode,
            correlationId = traceId.Value,
            errors = new[] { errorDetail },
        };

        return new FrontendResponse(
            StatusCode: statusCode,
            Body: JsonSerializer.Serialize(problemDetails),
            Headers: [],
            LocationHeaderPath: null,
            ContentType: "application/problem+json"
        );
    }
}
