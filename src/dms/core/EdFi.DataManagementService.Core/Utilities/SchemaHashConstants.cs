// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Constants used in effective schema hash computation.
/// </summary>
public static class SchemaHashConstants
{
    /// <summary>
    /// Version identifier for the hash algorithm format.
    /// Changing this forces hash recalculation even with identical schema content.
    /// </summary>
    public const string HashVersion = "dms-effective-schema-hash:v1";

    /// <summary>
    /// Version identifier for the relational mapping conventions.
    /// Bump this when mapping rules change to force schema mismatch detection.
    /// This value MUST match the relational_mapping_version used in .mpack files.
    /// </summary>
    /// <remarks>
    /// v1 → v2: the removal of <c>dms.Document</c>, <c>dms.ReferentialIdentity</c> and
    /// <c>dms.DocumentCache</c>. That is the first destructively schema-incompatible mapping change —
    /// a v1 database cannot serve the v2 mapping and cannot be migrated in place — so the bump is what
    /// makes the fingerprint reject it instead of letting a stale database look provisioned.
    /// </remarks>
    public const string RelationalMappingVersion = "v2";

    /// <summary>
    /// Version identifier for the resource key seed hash algorithm format.
    /// Changing this forces hash recalculation even with identical seed content.
    /// </summary>
    public const string ResourceKeySeedHashVersion = "resource-key-seed-hash:v1";
}
