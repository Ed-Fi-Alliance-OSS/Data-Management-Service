// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Decides which physical database this request uses, and records it once for every later step to read.
/// </summary>
/// <remarks>
/// It runs immediately before the database-validation phase, so fingerprint, resource-key, and
/// mapping-set validation all describe the database the request is actually served from, and a request
/// that asked for a database it cannot have is answered before anything opens a connection.
///
/// It has no provider dependency of any kind and reads no configuration beyond the resolved parent's
/// own derivative map, so composing it into a pipeline cannot introduce a database round trip.
/// </remarks>
internal class SelectEffectiveDataStoreTargetMiddleware(
    DerivativeRoutingPolicy policy,
    IEffectiveTargetSelectionResponseFactory responseFactory,
    ILogger<SelectEffectiveDataStoreTargetMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        IDataStoreSelection dataStoreSelection =
            requestInfo.ScopedServiceProvider.GetRequiredService<IDataStoreSelection>();
        DataStore parent = dataStoreSelection.GetSelectedDataStore();

        // ResolveDataStoreMiddleware answers 503 for a parent with no connection string before it
        // records the selection, and DataStoreSelection refuses to record one. Guarded here as well
        // so target selection returns that same configuration error rather than an argument
        // exception if upstream selection behavior ever changes.
        if (string.IsNullOrWhiteSpace(parent.ConnectionString))
        {
            logger.LogError(
                "Selected data store {DataStoreId} ({Name}) has no connection string configured during effective target selection. TraceId: {TraceId}",
                parent.Id,
                LoggingSanitizer.SanitizeForLogging(parent.Name),
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

        EffectiveTargetSelectionResult result = EffectiveTargetSelector.Select(
            policy,
            parent,
            UseSnapshotHeader.TryReadRequested(requestInfo.FrontendRequest)
        );

        // Recorded before it is acted on, so the verdict is observable on every path, both
        // rejections included.
        requestInfo.EffectiveTargetSelection = result;

        switch (result)
        {
            case EffectiveTargetSelectionResult.Selected selected:
                dataStoreSelection.SetEffectiveTarget(selected.Target);

                logger.LogDebug(
                    "Selected {TargetKind} target for data store {DataStoreId} - TraceId: {TraceId}",
                    selected.Target.Kind,
                    parent.Id,
                    requestInfo.FrontendRequest.TraceId.Value
                );

                await next();
                return;

            case EffectiveTargetSelectionResult.MissingSnapshot:
                logger.LogInformation(
                    "A snapshot was requested but data store {DataStoreId} ({Name}) has none configured - TraceId: {TraceId}",
                    parent.Id,
                    LoggingSanitizer.SanitizeForLogging(parent.Name),
                    requestInfo.FrontendRequest.TraceId.Value
                );

                requestInfo.FrontendResponse = responseFactory.ForMissingSnapshot(requestInfo);
                return;

            case EffectiveTargetSelectionResult.RejectedAsMutation:
                logger.LogInformation(
                    "A snapshot was requested on a request that would modify data store {DataStoreId} ({Name}) - TraceId: {TraceId}",
                    parent.Id,
                    LoggingSanitizer.SanitizeForLogging(parent.Name),
                    requestInfo.FrontendRequest.TraceId.Value
                );

                requestInfo.FrontendResponse = responseFactory.ForRejectedAsMutation(requestInfo);
                return;

            default:
                // Unreachable: EffectiveTargetSelectionResult has a private constructor, so the three
                // cases above are the whole hierarchy. Throwing rather than continuing keeps a new
                // case from silently reaching the database with no target assigned.
                throw new InvalidOperationException(
                    $"Unhandled effective target selection result '{result.GetType().Name}'."
                );
        }
    }
}
