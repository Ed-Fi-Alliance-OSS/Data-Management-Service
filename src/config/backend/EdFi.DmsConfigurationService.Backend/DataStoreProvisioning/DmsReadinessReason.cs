// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// One reason readiness failed (7.3): the failure code, the fixed name of the probe that raised it, and
/// an optional value-free diagnostic.
/// </summary>
public sealed record DmsReadinessReason(
    DmsProvisioningFailureCode Code,
    string Probe,
    DmsProvisioningDiagnostic? Diagnostic = null
)
{
    internal static IReadOnlyList<DmsReadinessReason> RequireAtLeastOne(
        IReadOnlyList<DmsReadinessReason> reasons
    )
    {
        ArgumentNullException.ThrowIfNull(reasons);
        if (reasons.Count == 0)
        {
            throw new ArgumentException("At least one readiness reason is required.", nameof(reasons));
        }

        return reasons;
    }
}
