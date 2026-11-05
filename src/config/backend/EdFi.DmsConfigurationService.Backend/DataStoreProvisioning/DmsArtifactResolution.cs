// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>The outcome of <see cref="IDmsDataStoreProvisioner.ResolveArtifactAsync"/>; no server was contacted either way.</summary>
public abstract record DmsArtifactResolution
{
    /// <summary>The allowlisted package authenticated and matched configuration; persist the pin before creating.</summary>
    public sealed record Resolved(DmsTrustedArtifactPin Pin) : DmsArtifactResolution;

    public sealed record Failure(DmsProvisioningFailure Detail) : DmsArtifactResolution;
}
