// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Recognizes known lag or queued work within a caller-authorized publication wait.</summary>
internal static class CdcKnownCatchUp
{
    internal static bool CanCatchUp(
        CdcEstablishedValidationObservation observation,
        CdcControllerTargetStatus status
    ) =>
        observation is { Connector.IsRunning: true, PreStartEligible: true }
        && !status.Recovery.RequiresFreshPass
        && status.Status.PrimaryBlockingCategory
            is CdcBlockingCategory.ProjectionBacklog
                or CdcBlockingCategory.LagExceeded
        && status.Diagnostics.All(d =>
            d.Failure == CdcDeploymentFailure.ValidationFailed
            && d.Component is CdcDeploymentComponent.Projection or CdcDeploymentComponent.Metrics
        )
        && status.Status.ConnectorRuntime.State == CdcComponentState.Satisfied
        && CanCatchUp(status.Status.Projection, CdcBlockingCategory.ProjectionBacklog)
        && CanCatchUp(status.Status.Lag, CdcBlockingCategory.LagExceeded);

    private static bool CanCatchUp(CdcComponent component, CdcBlockingCategory temporaryBlocker) =>
        component.State == CdcComponentState.Satisfied
        || component.State == CdcComponentState.NotSatisfied && component.Category == temporaryBlocker;
}
