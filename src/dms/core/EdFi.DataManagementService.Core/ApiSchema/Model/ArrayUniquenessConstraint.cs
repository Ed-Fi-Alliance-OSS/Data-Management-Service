// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ApiSchema.Model;

/// <summary>
/// Represents an array uniqueness constraint with support for nested arrays.
/// This matches the new MetaEd-js ArrayUniquenessConstraint structure.
/// </summary>
public record ArrayUniquenessConstraint
{
    /// <summary>
    /// Present only when this ArrayUniquenessConstraint is a nestedConstraint,
    /// this is its parent ArrayUniquenessConstraint array path, and always of
    /// the form $.XYZ[*]
    /// </summary>
    public JsonPath? BasePath { get; init; }

    /// <summary>
    /// A list of scalar paths on an array, always of the form $.XYZ[*].something
    /// </summary>
    public IReadOnlyList<JsonPath>? Paths { get; init; }

    /// <summary>
    /// Nested ArrayUniquenessConstraints for nested arrays
    /// </summary>
    public IReadOnlyList<ArrayUniquenessConstraint>? NestedConstraints { get; init; }

    /// <summary>
    /// Constructor for creating constraints with paths only
    /// </summary>
    public ArrayUniquenessConstraint(IEnumerable<JsonPath> paths)
    {
        Paths = paths.ToList().AsReadOnly();
    }

    /// <summary>
    /// Constructor for creating constraints with nested constraints only
    /// </summary>
    public ArrayUniquenessConstraint(IEnumerable<ArrayUniquenessConstraint> nestedConstraints)
    {
        NestedConstraints = nestedConstraints.ToList().AsReadOnly();
    }

    /// <summary>
    /// Default constructor for record initialization
    /// </summary>
    public ArrayUniquenessConstraint() { }

    /// <summary>
    /// Gets all paths in this constraint, including nested paths with their base paths combined
    /// </summary>
    public IEnumerable<JsonPath> GetAllPaths()
    {
        if (Paths != null)
        {
            foreach (var path in Paths)
            {
                yield return path;
            }
        }

        if (NestedConstraints != null)
        {
            foreach (var nestedConstraint in NestedConstraints)
            {
                foreach (var path in nestedConstraint.GetAllPaths())
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>
    /// Converts legacy constraint groups (string arrays) to new format
    /// </summary>
    public static ArrayUniquenessConstraint FromLegacyConstraintGroup(IEnumerable<JsonPath> constraintPaths)
    {
        return new ArrayUniquenessConstraint(constraintPaths);
    }
}
