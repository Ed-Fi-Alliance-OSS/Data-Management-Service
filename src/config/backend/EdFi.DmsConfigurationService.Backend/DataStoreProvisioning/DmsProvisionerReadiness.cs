// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>The provisioner's readiness state (D-2).</summary>
public abstract record DmsProvisionerReadiness
{
    /// <summary><c>DmsDataStoreProvisioning:Enabled</c> is false; nothing was validated or registered.</summary>
    public sealed record Disabled() : DmsProvisionerReadiness;

    /// <summary>Static and environmental validation (7.3) passed for the named provider.</summary>
    public sealed record Ready(string Provider, DateTimeOffset ValidatedAtUtc) : DmsProvisionerReadiness;

    /// <summary>At least one probe failed; the provisioner refuses every call until restart.</summary>
    public sealed record NotReady : DmsProvisionerReadiness
    {
        public NotReady(IReadOnlyList<DmsReadinessReason> reasons)
        {
            Reasons = DmsReadinessReason.RequireAtLeastOne(reasons);
        }

        public IReadOnlyList<DmsReadinessReason> Reasons { get; }
    }
}
