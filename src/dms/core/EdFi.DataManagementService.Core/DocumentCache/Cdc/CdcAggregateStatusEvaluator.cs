// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public sealed record CdcAggregateStatusEvaluationInput(
    DateTimeOffset ObservedAt,
    IReadOnlyList<CdcTargetStatus> Targets
);

public static class CdcAggregateStatusEvaluator
{
    public static CdcStatus Evaluate(CdcAggregateStatusEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Targets);

        CdcTargetStatus[] targets = NormalizeTargetOrder(input.Targets);

        CdcReadiness readiness = DetermineAggregateReadiness(targets);

        return new(
            CdcJsonContract.CurrentContractVersion,
            input.ObservedAt.ToUniversalTime(),
            readiness,
            SelectPrimaryBlockingCategory(targets, readiness),
            targets
        );
    }

    private static CdcReadiness DetermineAggregateReadiness(IReadOnlyList<CdcTargetStatus> targets)
    {
        if (targets.Count == 0)
        {
            return CdcReadiness.Unknown;
        }

        if (targets.Any(target => target.Readiness == CdcReadiness.NotReady))
        {
            return CdcReadiness.NotReady;
        }

        if (targets.Any(target => target.Readiness == CdcReadiness.Unknown))
        {
            return CdcReadiness.Unknown;
        }

        return CdcReadiness.Ready;
    }

    private static CdcBlockingCategory SelectPrimaryBlockingCategory(
        IReadOnlyList<CdcTargetStatus> targets,
        CdcReadiness readiness
    )
    {
        if (targets.Count == 0)
        {
            return CdcBlockingCategory.StatusObservationUnavailable;
        }

        if (readiness == CdcReadiness.Ready)
        {
            return CdcBlockingCategory.None;
        }

        return CdcStatusEvaluationRules
            .EnumerateBlockingPrecedence()
            .FirstOrDefault(
                category => targets.Any(target => target.PrimaryBlockingCategory == category),
                CdcBlockingCategory.StatusObservationUnavailable
            );
    }

    private static CdcTargetStatus[] NormalizeTargetOrder(IReadOnlyList<CdcTargetStatus> targets)
    {
        foreach (CdcTargetStatus target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(target.TargetIdentity);
        }

        return
        [
            .. targets
                .OrderBy(target => target.TargetIdentity.DeploymentKey, StringComparer.Ordinal)
                .ThenBy(target => target.TargetIdentity.TenantKey, StringComparer.Ordinal)
                .ThenBy(target => target.TargetIdentity.DataStoreId, StringComparer.Ordinal)
                .ThenBy(target => target.TargetIdentity.InstanceKey, StringComparer.Ordinal)
                .ThenBy(target => target.TargetIdentity.Generation)
                .ThenBy(target => target.TargetIdentity.Provider),
        ];
    }
}
