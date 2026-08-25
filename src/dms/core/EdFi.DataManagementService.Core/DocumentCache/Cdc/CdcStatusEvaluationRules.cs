// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

internal static class CdcStatusEvaluationRules
{
    private static readonly CdcBlockingCategory[] BlockingPrecedence =
    [
        CdcBlockingCategory.BindingMissing,
        CdcBlockingCategory.BindingMismatch,
        CdcBlockingCategory.SourceMismatch,
        CdcBlockingCategory.SourceHistoryLost,
        CdcBlockingCategory.ProjectionNonOperational,
        CdcBlockingCategory.ProviderSetupInvalid,
        CdcBlockingCategory.KafkaPolicyInvalid,
        CdcBlockingCategory.ConnectOffsetStoreInvalid,
        CdcBlockingCategory.ConnectorConfigInvalid,
        CdcBlockingCategory.ConnectorNotRunning,
        CdcBlockingCategory.SnapshotIncomplete,
        CdcBlockingCategory.ProjectionBacklog,
        CdcBlockingCategory.ProviderHistoryUnknown,
        CdcBlockingCategory.ProviderBarrierNotReached,
        CdcBlockingCategory.LagExceeded,
        CdcBlockingCategory.StatusObservationUnavailable,
    ];

    public static CdcReadiness DetermineTargetReadiness(IReadOnlyList<CdcComponentStatus> components)
    {
        if (components.Any(component => component.State == CdcComponentState.NotSatisfied))
        {
            return CdcReadiness.NotReady;
        }

        if (components.Any(component => component.State == CdcComponentState.Unknown))
        {
            return CdcReadiness.Unknown;
        }

        return CdcReadiness.Ready;
    }

    public static CdcBlockingCategory SelectTargetPrimaryBlockingCategory(
        IReadOnlyList<CdcComponentStatus> components
    )
    {
        CdcBlockingCategory notSatisfiedCategory = SelectCategory(components, CdcComponentState.NotSatisfied);
        if (notSatisfiedCategory != CdcBlockingCategory.None)
        {
            return notSatisfiedCategory;
        }

        return SelectCategory(components, CdcComponentState.Unknown);
    }

    public static IEnumerable<CdcBlockingCategory> EnumerateBlockingPrecedence() => BlockingPrecedence;

    private static CdcBlockingCategory SelectCategory(
        IReadOnlyList<CdcComponentStatus> components,
        CdcComponentState state
    ) =>
        BlockingPrecedence.FirstOrDefault(
            category =>
                components.Any(component => component.State == state && component.Category == category),
            CdcBlockingCategory.None
        );
}

internal sealed record CdcComponentStatus(CdcComponentState State, CdcBlockingCategory Category);
