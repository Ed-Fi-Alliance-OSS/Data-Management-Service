// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that resolves the appropriate data store based on route qualifiers
/// and client DataStoreIds from the JWT token
/// </summary>
internal class ResolveDataStoreMiddleware(
    IDataStoreProvider dataStoreProvider,
    ILogger<ResolveDataStoreMiddleware> logger
) : IPipelineStep
{
    /// <summary>
    /// Resolves the data store by matching route qualifiers with authorized instances
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Resolve scoped service from the per-request scope
        var dataStoreSelection = requestInfo.ScopedServiceProvider.GetRequiredService<IDataStoreSelection>();

        // Validate ClientAuthorizations.DataStoreIds not empty
        if (requestInfo.ClientAuthorizations.DataStoreIds.Count == 0)
        {
            logger.LogError(
                "No data stores authorized for client - Tenant: {Tenant}, TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.Tenant ?? "(none)"),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 403,
                Body: FailureResponse.ForAuthorizationDenied(
                    "No database instances are authorized for this client",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
            return;
        }

        try
        {
            await dataStoreProvider.RefreshInstancesIfExpiredAsync(requestInfo.FrontendRequest.Tenant);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to refresh data store cache for tenant {Tenant} before route resolution - TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.Tenant ?? "(default)"),
                requestInfo.FrontendRequest.TraceId.Value
            );
        }

        // Get route qualifiers from the request
        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers = requestInfo
            .FrontendRequest
            .RouteQualifiers;

        // Try to find matching data store
        DataStore? matchedInstance = await TryFindMatchingInstance(
            requestInfo,
            requestQualifiers,
            reloadOnCacheMiss: false
        );

        // If no match found and we haven't tried reloading yet, attempt cache reload
        if (matchedInstance == null)
        {
            logger.LogInformation(
                "No matching data store found in cache, attempting reload from CMS - TraceId: {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            try
            {
                await dataStoreProvider.LoadDataStores(requestInfo.FrontendRequest.Tenant);

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
                    "Failed to reload data stores from CMS - TraceId: {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
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

            List<long> checkedDataStoreIds = requestInfo
                .ClientAuthorizations.DataStoreIds.Select(id => id.Value)
                .ToList();

            logger.LogError(
                "No data store matches route qualifiers [{QualifierDetails}] from authorized data stores [{DataStoreIds}] - Tenant: {Tenant}, TraceId: {TraceId}",
                qualifierDetails,
                LoggingSanitizer.SanitizeForLogging(string.Join(", ", checkedDataStoreIds)),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.Tenant ?? "(none)"),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: FailureResponse.ForRouteResolutionError(
                    "No database instance found matching the request route qualifiers",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
            return;
        }

        // Validate connection string
        if (string.IsNullOrWhiteSpace(matchedInstance.ConnectionString))
        {
            logger.LogError(
                "data store {DataStoreId} has no connection string configured - Tenant: {Tenant}, TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(matchedInstance.Id.ToString()),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.Tenant ?? "(none)"),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForServiceConfigurationError(
                    "Database connection not configured for the matched instance",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
            return;
        }

        // Set selected data store in scoped provider for repository access
        dataStoreSelection.SetSelectedDataStore(matchedInstance);

        logger.LogDebug(
            "Selected data store {DataStoreId} ('{Name}') - TraceId: {TraceId}",
            LoggingSanitizer.SanitizeForLogging(matchedInstance.Id.ToString()),
            LoggingSanitizer.SanitizeForLogging(matchedInstance.Name),
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Continue to next middleware
        await next();
    }

    /// <summary>
    /// Attempts to find a data store matching the route qualifiers from authorized instances
    /// </summary>
    private Task<DataStore?> TryFindMatchingInstance(
        RequestInfo requestInfo,
        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers,
        bool reloadOnCacheMiss
    )
    {
        DataStore? matchedInstance = null;

#pragma warning disable S3267 // Loops should be simplified with "LINQ" expressions - False positive: this loop has complex logic with early exits and side effects
        foreach (DataStoreId dataStoreId in requestInfo.ClientAuthorizations.DataStoreIds)
#pragma warning restore S3267
        {
            DataStore? dataStore = dataStoreProvider.GetById(
                dataStoreId.Value,
                requestInfo.FrontendRequest.Tenant
            );

            if (dataStore == null)
            {
                string logMessage = reloadOnCacheMiss
                    ? "data store {DataStoreId} still not found after cache reload - TraceId: {TraceId}"
                    : "data store {DataStoreId} not found in cache - TraceId: {TraceId}";

                logger.LogWarning(
                    logMessage,
                    LoggingSanitizer.SanitizeForLogging(dataStoreId.Value.ToString()),
                    requestInfo.FrontendRequest.TraceId.Value
                );
                continue;
            }

            // Check if this instance's route context matches the request qualifiers
            bool isMatch = IsRouteContextMatch(dataStore.RouteContext, requestQualifiers);

            if (isMatch)
            {
                if (matchedInstance != null)
                {
                    // Multiple matches - not supported
                    logger.LogError(
                        "Multiple data stores match route qualifiers (instances {FirstId} and {SecondId}) - TraceId: {TraceId}",
                        LoggingSanitizer.SanitizeForLogging(matchedInstance.Id.ToString()),
                        LoggingSanitizer.SanitizeForLogging(dataStore.Id.ToString()),
                        requestInfo.FrontendRequest.TraceId.Value
                    );

                    requestInfo.FrontendResponse = new FrontendResponse(
                        StatusCode: 400,
                        Body: FailureResponse.ForAmbiguousRouteResolution(
                            "Multiple database instances match the request route qualifiers - ambiguous routing not supported",
                            requestInfo.FrontendRequest.TraceId
                        ),
                        Headers: []
                    );
                    return Task.FromResult<DataStore?>(null);
                }

                matchedInstance = dataStore;
            }
        }

        return Task.FromResult(matchedInstance);
    }

    /// <summary>
    /// Checks if the data store's route context matches the request's route qualifiers
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
}
