// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The readiness singleton (D-2, AC 18). It starts as <c>NotReady</c> with the <see cref="StartupPendingProbe"/>
/// reason, so a call that arrives before startup has recorded a verdict is refused, and it accepts exactly
/// one recorded verdict for the life of the process: readiness never moves from <c>NotReady</c> back to
/// <c>Ready</c> without a restart, because a second record is refused outright.
/// </summary>
public sealed class DmsDataStoreProvisionerReadiness : IDmsDataStoreProvisionerReadiness
{
    public const string StartupPendingProbe = "StartupPending";

    private readonly Lock _gate = new();

    private DmsProvisionerReadiness _current = new DmsProvisionerReadiness.NotReady([
        new DmsReadinessReason(DmsProvisioningFailureCode.StartupValidationFailed, StartupPendingProbe),
    ]);

    private bool _recorded;

    public DmsProvisionerReadiness Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Records the startup verdict. Throws when a verdict was already recorded.</summary>
    public void Record(DmsProvisionerReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        lock (_gate)
        {
            if (_recorded)
            {
                throw new InvalidOperationException(
                    "DMS data store provisioning readiness was already recorded; it changes only with a restart."
                );
            }

            _recorded = true;
            _current = readiness;
        }
    }
}
