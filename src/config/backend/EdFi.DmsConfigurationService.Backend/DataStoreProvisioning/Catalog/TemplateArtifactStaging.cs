// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;

/// <summary>
/// The outcome of <see cref="ITemplateArtifactCatalog.StageAsync"/>. A staged artifact is held open in its
/// private directory until its owner releases it; a refusal has already released that directory, and its
/// cleanup report says what the release could not remove (D-19).
/// </summary>
public abstract record TemplateArtifactStaging
{
    public sealed record Staged(TrustedTemplateArtifact Artifact) : TemplateArtifactStaging;

    public sealed record Failure(DmsProvisioningFailure Detail, DmsCleanupReport Cleanup)
        : TemplateArtifactStaging;
}
