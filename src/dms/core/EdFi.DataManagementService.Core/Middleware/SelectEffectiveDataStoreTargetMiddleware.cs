// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Pipeline;
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

        // A parent with no connection string never reaches here: ResolveDataStoreMiddleware answers
        // 503 before recording the selection, and the selection itself refuses to record one. There is
        // deliberately no guard, because a fourth way to leave this step would mean returning without
        // a recorded outcome.
        EffectiveTargetSelectionResult result = EffectiveTargetSelector.Select(
            policy,
            parent,
            UseSnapshotHeader.TryReadRequested(requestInfo.FrontendRequest)
        );

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
