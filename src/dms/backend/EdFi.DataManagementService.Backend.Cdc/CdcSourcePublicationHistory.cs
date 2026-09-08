// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed record CdcSourcePublicationTransition(
    DocumentCacheDownstreamPublicationStatus Status,
    DateTimeOffset RecordedAt
);

/// <summary>
/// Source-lifetime evidence, retained independently of bindings and their generations. The initial
/// attestation is valid only alongside its original managed CREATE receipt and source association.
/// There is deliberately no reset or deletion API: retirement of a binding cannot erase exposure.
/// </summary>
public sealed record CdcSourcePublicationHistory(
    int Version,
    Guid WorkflowId,
    CdcTargetIdentity CreationTarget,
    CdcDatabaseCreationReceipt CreationReceipt,
    string PhysicalSourceFingerprint,
    ImmutableArray<CdcSourcePublicationTransition> Transitions
)
{
    public const int CurrentVersion = 1;
}
