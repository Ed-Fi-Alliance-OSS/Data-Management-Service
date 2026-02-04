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
    public const string RelationalMappingVersion = "v1";
}
