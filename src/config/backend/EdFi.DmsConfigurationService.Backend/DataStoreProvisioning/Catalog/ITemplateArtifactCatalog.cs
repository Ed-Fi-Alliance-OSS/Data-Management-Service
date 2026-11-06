// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;

/// <summary>
/// The artifact-resolution port the orchestrator calls before any server contact (6.2 steps 1 to 7):
/// template to allowlist entry, package and attestation located by allowlist-derived names, trust
/// verification, manifest validation, configuration match, and the pin. Created minimally here so the
/// orchestrator shell has a port to refuse ahead of and to resolve through; Step 2.4 delivers the
/// implementation and extends the port for the create path's artifact extraction (6.2 step 8).
/// </summary>
public interface ITemplateArtifactCatalog
{
    /// <summary>Resolves and pins the allowlisted artifact for <paramref name="template"/>; no server is contacted.</summary>
    Task<DmsArtifactResolution> ResolveAsync(
        DmsDatabaseTemplate template,
        CancellationToken cancellationToken
    );
}
