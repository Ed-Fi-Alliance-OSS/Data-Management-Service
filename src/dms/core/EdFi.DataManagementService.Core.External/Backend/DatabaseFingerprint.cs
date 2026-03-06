// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Immutable snapshot of the four fingerprint fields from the dms.EffectiveSchema singleton row.
/// </summary>
/// <param name="ApiSchemaFormatVersion">The ApiSchema.json format version.</param>
/// <param name="EffectiveSchemaHash">The effective schema hash (lowercase hex).</param>
/// <param name="ResourceKeyCount">The number of resource keys in the effective schema.</param>
/// <param name="ResourceKeySeedHash">The SHA-256 hash of the resource key seed list (32 bytes).</param>
public sealed record DatabaseFingerprint(
    string ApiSchemaFormatVersion,
    string EffectiveSchemaHash,
    int ResourceKeyCount,
    byte[] ResourceKeySeedHash
);
