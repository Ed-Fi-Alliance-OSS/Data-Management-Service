// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// Envelope inputs every control-plane observation carries. The adapters and mappers never invent an
/// operation, target, or physical-source fingerprint of their own; each is supplied by the operation
/// that collected the evidence.
/// </summary>
public sealed record CdcObservationContext(
    string OperationId,
    CdcTargetIdentity TargetIdentity,
    string? PhysicalSourceFingerprint
)
{
    /// <summary>The validation context an observation composed under this envelope is checked against.</summary>
    public CdcObservationValidationContext ToValidationContext(DateTimeOffset observedAt) =>
        new(OperationId, TargetIdentity, PhysicalSourceFingerprint, observedAt);
}
