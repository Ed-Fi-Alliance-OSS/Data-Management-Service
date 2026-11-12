// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// Creates and deletes DMS target databases from the allowlisted template packages (AC 1). Every call
/// first consults readiness and returns a <c>NotReady</c> failure without touching anything when the
/// feature is disabled or startup validation failed (D-2). Cancellation surfaces as
/// <see cref="OperationCanceledException"/> only after the outcome protocol and cleanup have run;
/// a marker found committed is reported as success even under cancellation.
/// </summary>
public interface IDmsDataStoreProvisioner
{
    /// <summary>
    /// Authenticates the allowlisted package for the template and returns its pin with no server contact
    /// and no files left behind. DMS-1439 persists the pin and presents it on create and every retry.
    /// </summary>
    Task<DmsArtifactResolution> ResolveArtifactAsync(
        DmsDatabaseTemplate template,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Stage, validate, rename, mark (D-6, D-7): the selected name is bound only to an already validated
    /// database, and the expected identity is assigned last. A target that already exists is reconciled
    /// against the pin, never restored over or dropped.
    /// </summary>
    Task<DmsDataStoreCreateResult> CreateAsync(
        DmsDataStoreCreateRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Fence, verify exclusivity, re-verify ownership and instance, drop. An absent target is
    /// idempotent success; a mismatched or unverifiable target is reported without mutation.
    /// </summary>
    Task<DmsDataStoreDeleteResult> DeleteAsync(
        DmsDataStoreDeleteRequest request,
        CancellationToken cancellationToken
    );
}
