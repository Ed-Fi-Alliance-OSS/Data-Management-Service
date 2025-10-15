// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that selects the appropriate DMS instance and connection string for the request
/// based on the client's application configuration
/// </summary>
internal class DmsInstanceSelectionMiddleware(
    IApplicationContextProvider applicationContextProvider,
    IDmsInstanceProvider dmsInstanceProvider,
    IDmsInstanceSelection dmsInstanceSelection,
    ILogger<DmsInstanceSelectionMiddleware> logger
) : IPipelineStep
{
    /// <summary>
    /// Selects the DMS instance for the current request based on client_id from JWT token
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        string clientId = requestInfo.ClientAuthorizations.ClientId;

        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogError(
                "Missing client_id in JWT token - TraceId: {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: Utility.ToJsonError(
                    "Service configuration error: Missing client identifier",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                LocationHeaderPath: null
            );
            return;
        }

        // Lookup application context by client_id
        ApplicationContext? applicationContext =
            await applicationContextProvider.GetApplicationByClientIdAsync(clientId);

        if (applicationContext == null)
        {
            logger.LogError(
                "Application not found for client_id: {ClientId} - TraceId: {TraceId}",
                clientId,
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: Utility.ToJsonError(
                    "Service configuration error: Application configuration not found",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                LocationHeaderPath: null
            );
            return;
        }

        // Check if application has any DMS instances configured
        if (applicationContext.DmsInstanceIds.Count == 0)
        {
            logger.LogError(
                "No DMS instances configured for client_id: {ClientId}, ApplicationId: {ApplicationId} - TraceId: {TraceId}",
                clientId,
                applicationContext.ApplicationId,
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: Utility.ToJsonError(
                    "Service configuration error: No database instances configured for this application",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                LocationHeaderPath: null
            );
            return;
        }

        // Select first DMS instance (future: implement routing logic)
        long selectedDmsInstanceId = applicationContext.DmsInstanceIds[0];

        logger.LogDebug(
            "Selected DMS instance {DmsInstanceId} for client_id: {ClientId} (from {TotalInstances} configured instances)",
            selectedDmsInstanceId,
            clientId,
            applicationContext.DmsInstanceIds.Count
        );

        // Get the DMS instance details
        DmsInstance? dmsInstance = dmsInstanceProvider.GetById(selectedDmsInstanceId);

        // If instance not found, reload from CMS in case it's a new instance
        if (dmsInstance == null)
        {
            logger.LogInformation(
                "DMS instance {DmsInstanceId} not found in cache, reloading instances from CMS",
                selectedDmsInstanceId
            );

            try
            {
                await dmsInstanceProvider.LoadDmsInstances();
                dmsInstance = dmsInstanceProvider.GetById(selectedDmsInstanceId);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to reload DMS instances from CMS for instance {DmsInstanceId}",
                    selectedDmsInstanceId
                );
            }

            // If still not found after reload, return error
            if (dmsInstance == null)
            {
                logger.LogError(
                    "DMS instance {DmsInstanceId} not found even after reload for client_id: {ClientId} - TraceId: {TraceId}",
                    selectedDmsInstanceId,
                    clientId,
                    requestInfo.FrontendRequest.TraceId.Value
                );

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 503,
                    Body: Utility.ToJsonError(
                        "Service configuration error: Database instance not available",
                        requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: [],
                    LocationHeaderPath: null
                );
                return;
            }

            logger.LogInformation(
                "Successfully reloaded DMS instance {DmsInstanceId} from CMS",
                selectedDmsInstanceId
            );
        }

        if (string.IsNullOrWhiteSpace(dmsInstance.ConnectionString))
        {
            logger.LogError(
                "DMS instance {DmsInstanceId} has no connection string configured for client_id: {ClientId} - TraceId: {TraceId}",
                selectedDmsInstanceId,
                clientId,
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: Utility.ToJsonError(
                    "Service configuration error: Database connection not configured",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                LocationHeaderPath: null
            );
            return;
        }

        // Set selected DMS instance in scoped provider for repository access
        dmsInstanceSelection.SetSelectedDmsInstance(dmsInstance);

        logger.LogInformation(
            "Request routed to DMS instance {DmsInstanceId} ('{InstanceName}') for client_id: {ClientId} - TraceId: {TraceId}",
            selectedDmsInstanceId,
            dmsInstance.InstanceName,
            clientId,
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Continue to next middleware
        await next();
    }
}
