// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;

/// <summary>
/// The artifact port the orchestrator calls before any server contact (6.2): template to allowlist entry,
/// package and attestation located by allowlist-derived names, private staging, trust verification, manifest
/// validation, configuration match, and the pin. <see cref="ResolveAsync"/> serves
/// <c>ResolveArtifactAsync</c> and the create path's pin comparison: it releases the private directory before
/// returning. <see cref="StageAsync"/> serves the create path once the pin matches: the artifact stays staged
/// for extraction (6.2 step 8) and the restore, and is released by its owner. <see cref="ValidateAsync"/> is
/// the readiness probe of 7.3 steps 2 and 3. No member contacts a database server.
/// </summary>
public interface ITemplateArtifactCatalog
{
    /// <summary>Resolves and pins the allowlisted artifact for <paramref name="template"/>; nothing is extracted and nothing is left on disk.</summary>
    Task<DmsArtifactResolution> ResolveAsync(
        DmsDatabaseTemplate template,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Stages the allowlisted artifact for <paramref name="template"/> in the private directory of
    /// <paramref name="operationId"/> and returns it with its pin, or the classified refusal together with
    /// the cleanup report of the directory the refusal released.
    /// </summary>
    Task<TemplateArtifactStaging> StageAsync(
        DmsDatabaseTemplate template,
        DmsOperationId operationId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// The startup probes (7.3 steps 2 and 3): the trust policy loads and names at least one
    /// detached-attestation producer, and both allowlisted packages resolve against the configuration.
    /// Returns one reason per failed probe; empty means every probe passed.
    /// </summary>
    Task<IReadOnlyList<DmsReadinessReason>> ValidateAsync(CancellationToken cancellationToken);
}
