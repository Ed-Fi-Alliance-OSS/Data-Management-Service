// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

internal static class DocumentCacheStartupDiagnostics
{
    public static void Log(ILogger logger, DocumentCacheOptions options)
    {
        DocumentCacheStartupDiagnosticSnapshot snapshot = CreateSnapshot(options);

        logger.LogInformation(
            "DocumentCache configuration effective settings: TargetCount {TargetCount}, ConfiguredTargets {ConfiguredTargets}, ReadAccelerationEnabled {ReadAccelerationEnabled}, DirectFillTimeout {DirectFillTimeout}, PollInterval {PollInterval}, PageSize {PageSize}, MaxConcurrentTargets {MaxConcurrentTargets}, FailureBackoff {FailureBackoff}, BaselineHighWaterMark {BaselineHighWaterMark}, WorkflowTimeout {WorkflowTimeout}, StatusObservationTimeout {StatusObservationTimeout}, StatusEndpointTimeout {StatusEndpointTimeout}",
            snapshot.TargetCount,
            snapshot.ConfiguredTargets,
            snapshot.ReadAccelerationEnabled,
            snapshot.DirectFillTimeout,
            snapshot.PollInterval,
            snapshot.PageSize,
            snapshot.MaxConcurrentTargets,
            snapshot.FailureBackoff,
            snapshot.BaselineHighWaterMark,
            snapshot.WorkflowTimeout,
            snapshot.StatusObservationTimeout,
            snapshot.StatusEndpointTimeout
        );
    }

    internal static DocumentCacheStartupDiagnosticSnapshot CreateSnapshot(DocumentCacheOptions options)
    {
        IReadOnlyList<string> configuredTargets = options
            .GetTargetKeys()
            .Select(key => key.ToString())
            .ToList();

        return new DocumentCacheStartupDiagnosticSnapshot(
            configuredTargets.Count,
            configuredTargets,
            options.ReadAcceleration.Enabled,
            options.ReadAcceleration.DirectFillTimeout,
            options.Projector.PollInterval,
            options.Projector.PageSize,
            options.Projector.MaxConcurrentTargets,
            options.Projector.FailureBackoff,
            options.Projector.BaselineHighWaterMark,
            options.Administration.WorkflowTimeout,
            options.Status.StatusObservationTimeout,
            options.Status.EndpointTimeout
        );
    }
}

internal sealed record DocumentCacheStartupDiagnosticSnapshot(
    int TargetCount,
    IReadOnlyList<string> ConfiguredTargets,
    bool ReadAccelerationEnabled,
    TimeSpan DirectFillTimeout,
    TimeSpan PollInterval,
    int PageSize,
    int MaxConcurrentTargets,
    TimeSpan FailureBackoff,
    int BaselineHighWaterMark,
    TimeSpan WorkflowTimeout,
    TimeSpan StatusObservationTimeout,
    TimeSpan StatusEndpointTimeout
);
