// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Naming;

/// <summary>
/// Identifies whether a name override targets a column or a collection table scope.
/// </summary>
public enum NameOverrideKind
{
    Column,
    Collection,
}

/// <summary>
/// Captures a normalized name override entry keyed by canonical JSONPath.
/// </summary>
/// <param name="RawKey">The original override key as authored in the schema.</param>
/// <param name="CanonicalPath">The canonical JSONPath form used for lookup.</param>
/// <param name="NormalizedName">The normalized PascalCase override name.</param>
/// <param name="Kind">Whether the override targets a column or collection scope.</param>
public sealed record NameOverrideEntry(
    string RawKey,
    string CanonicalPath,
    string NormalizedName,
    NameOverrideKind Kind
);
