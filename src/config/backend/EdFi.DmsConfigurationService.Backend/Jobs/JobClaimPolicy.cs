// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>Restart-scoped eligibility for the two managed operations. All other job types remain eligible.</summary>
public sealed class JobClaimPolicy
{
    public static JobClaimPolicy All { get; } = new(false);
    public static JobClaimPolicy ExcludeManagedDataStores { get; } = new(true);

    private JobClaimPolicy(bool excludesManagedDataStores) =>
        ExcludesManagedDataStores = excludesManagedDataStores;

    public bool ExcludesManagedDataStores { get; }

    public bool Allows(string jobType) =>
        !ExcludesManagedDataStores || jobType is not (ManagedJobTypes.Create or ManagedJobTypes.Delete);

    /// <summary>
    /// The worker's claim and exhaustion eligibility. Managed work also waits while provisioning readiness is not
    /// <c>Ready</c>: that verdict is restart-scoped, so accepted operations stay nonterminal for a healthy restart
    /// instead of being refused, attempted or exhausted. Routes and enqueue keep using the feature policy itself.
    /// </summary>
    public JobClaimPolicy ForWorker(DmsProvisionerReadiness readiness) =>
        readiness is DmsProvisionerReadiness.Ready ? this : ExcludeManagedDataStores;
}
