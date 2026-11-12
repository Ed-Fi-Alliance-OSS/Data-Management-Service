// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>The outcome of <see cref="IDmsDataStoreProvisioner.DeleteAsync"/>.</summary>
public abstract record DmsDataStoreDeleteResult
{
    /// <summary>The owned target was fenced, verified exclusive, re-verified, and dropped.</summary>
    public sealed record Dropped(DmsCleanupReport Cleanup) : DmsDataStoreDeleteResult;

    /// <summary>No database holds the target name; idempotent success, nothing was mutated.</summary>
    public sealed record AlreadyAbsent() : DmsDataStoreDeleteResult;

    public sealed record Failure(DmsProvisioningFailure Detail, DmsCleanupReport Cleanup)
        : DmsDataStoreDeleteResult;
}
