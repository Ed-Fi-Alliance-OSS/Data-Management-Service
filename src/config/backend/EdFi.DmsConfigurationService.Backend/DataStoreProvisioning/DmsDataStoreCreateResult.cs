// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The outcome of <see cref="IDmsDataStoreProvisioner.CreateAsync"/>. Every case carries the cleanup
/// report; residue never changes the outcome class (D-19). No case carries a connection string, secret,
/// provider message, or process output.
/// </summary>
public abstract record DmsDataStoreCreateResult
{
    /// <summary>A validated database was renamed into the target name and marked with the expected identity.</summary>
    public sealed record Created(DmsTrustedArtifactPin Artifact, DmsCleanupReport Cleanup)
        : DmsDataStoreCreateResult;

    /// <summary>
    /// The target already existed, is marked with the expected identity, and passed full re-validation
    /// against the pinned artifact (6.5); nothing was mutated.
    /// </summary>
    public sealed record Reconciled(DmsTrustedArtifactPin Artifact, DmsCleanupReport Cleanup)
        : DmsDataStoreCreateResult;

    public sealed record Failure(DmsProvisioningFailure Detail, DmsCleanupReport Cleanup)
        : DmsDataStoreCreateResult;
}
