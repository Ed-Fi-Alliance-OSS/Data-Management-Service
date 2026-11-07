// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

public enum DmsEffectiveSchemaReadProblem
{
    TableMissing,
    NoRows,
    MultipleRows,
}

/// <summary>The single <c>dms.EffectiveSchema</c> row (F-D2), compared with the manifest (6.9c item 1).</summary>
public abstract record DmsEffectiveSchemaRead
{
    /// <param name="ResourceKeySeedHashB64">The 32-byte seed hash as base64, the manifest's representation.</param>
    public sealed record Found(
        string ApiSchemaFormatVersion,
        string EffectiveSchemaHash,
        int ResourceKeyCount,
        string ResourceKeySeedHashB64
    ) : DmsEffectiveSchemaRead;

    public sealed record Invalid(DmsEffectiveSchemaReadProblem Problem) : DmsEffectiveSchemaRead;
}
