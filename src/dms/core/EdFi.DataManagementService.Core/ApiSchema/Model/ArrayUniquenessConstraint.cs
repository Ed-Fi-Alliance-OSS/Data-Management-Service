// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ApiSchema.Model;

/// <summary>
/// Represents an array uniqueness constraint with support for nested constraints.
/// An array element in an API document is unique iff no other element has
/// the same values in the constraint paths
/// </summary>
public record ArrayUniquenessConstraint(
    /// <summary>
    /// Present only when this ArrayUniquenessConstraint is a nestedConstraint.
    /// This is its parent ArrayUniquenessConstraint array path, and always of
    /// the form $.XYZ[*]
    /// </summary>
    JsonPath? BasePath,
    /// <summary>
    /// A list of scalar paths on the array, always of the form $.XYZ[*].something
    /// </summary>
    IReadOnlyList<JsonPath>? Paths,
    /// <summary>
    /// ArrayUniquenessConstraints for arrays nested under this one
    /// </summary>
    IReadOnlyList<ArrayUniquenessConstraint>? NestedConstraints
);
