// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The outcome of <see cref="IDmsDataStoreProvisioner.ResolveArtifactAsync"/>; no server was contacted either
/// way. Resolution stages the package privately and releases that directory before returning (6.2 step 7), so
/// both outcomes carry the cleanup report of that release (D-19): residue never changes the outcome.
/// </summary>
public abstract record DmsArtifactResolution
{
    /// <summary>The allowlisted package authenticated and matched configuration; persist the pin before creating.</summary>
    public sealed record Resolved(DmsTrustedArtifactPin Pin, DmsCleanupReport Cleanup)
        : DmsArtifactResolution;

    public sealed record Failure(DmsProvisioningFailure Detail, DmsCleanupReport Cleanup)
        : DmsArtifactResolution;
}
