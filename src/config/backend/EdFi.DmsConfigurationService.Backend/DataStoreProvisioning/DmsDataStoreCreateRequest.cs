// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// A request to create, or on retry reconcile, a managed DMS data store (AC 5). It carries no artifact
/// path, feed, package, server, or connection detail: the template selects an allowlisted package, the
/// provisioner derives everything else from configuration.
/// </summary>
/// <param name="Template">The allowlisted template.</param>
/// <param name="TargetName">The validated selected target name.</param>
/// <param name="ExpectedSourceIdentity">The <c>dms.DataStoreIdentity.SourceIdentity</c> the caller owns the
/// target under; the commit marker (D-7). <see cref="Guid.Empty"/> is rejected at the boundary.</param>
/// <param name="PinnedArtifact">The pin <see cref="IDmsDataStoreProvisioner.ResolveArtifactAsync"/> returned
/// and DMS-1439 persisted (D-8). Required; a missing pin is rejected at the boundary.</param>
public sealed record DmsDataStoreCreateRequest(
    DmsDatabaseTemplate Template,
    DmsTargetDatabaseName TargetName,
    Guid ExpectedSourceIdentity,
    DmsTrustedArtifactPin PinnedArtifact
);
