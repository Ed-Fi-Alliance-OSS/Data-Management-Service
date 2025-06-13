// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Helper utilities for array path manipulation and analysis
/// </summary>
internal static class ArrayPathHelper
{
    /// <summary>
    /// Extracts the common array root path from a collection of JsonPaths
    /// </summary>
    /// <param name="paths">Collection of JsonPaths that should share a common array root</param>
    /// <returns>The array root path ending with [*]</returns>
    /// <example>
    /// Input: ["$.requiredImmunizations[*].dates[*].immunizationDate", "$.requiredImmunizations[*].immunizationTypeDescriptor"]
    /// Output: "$.requiredImmunizations[*]"
    /// </example>
    public static string GetArrayRootPath(IEnumerable<JsonPath> paths)
    {
        // Find the common path until the first [*]
        return paths.First().Value.Split(["[*]"], StringSplitOptions.None)[0] + "[*]";
    }

    /// <summary>
    /// Converts a full JsonPath to a relative path from the given root
    /// </summary>
    /// <param name="root">The root path to make relative from</param>
    /// <param name="fullPath">The full JsonPath</param>
    /// <returns>The relative path portion</returns>
    /// <example>
    /// Root: "$.requiredImmunizations[*]"
    /// FullPath: "$.requiredImmunizations[*].dates[*].immunizationDate"
    /// Result: "dates[*].immunizationDate"
    /// </example>
    public static string GetRelativePath(string root, string fullPath)
    {
        if (fullPath.StartsWith(root))
        {
            return fullPath.Substring(root.Length).TrimStart('.');
        }
        return fullPath;
    }

    /// <summary>
    /// Extracts parent paths from ArrayUniquenessConstraints for overlap detection
    /// This method replicates the exact logic from the original middleware (lines 57-65)
    /// to determine which reference arrays are already covered by uniqueness constraints
    /// </summary>
    /// <param name="arrayUniquenessConstraints">The array uniqueness constraints from ResourceSchema</param>
    /// <returns>A HashSet of parent paths that are covered by uniqueness constraints</returns>
    public static HashSet<string> GetUniquenessParentPaths(
        IReadOnlyList<IReadOnlyList<JsonPath>> arrayUniquenessConstraints
    )
    {
        return arrayUniquenessConstraints
            .SelectMany(group => group)
            .Select(jsonPath =>
            {
                int lastDot = jsonPath.Value.LastIndexOf('.');
                return lastDot > 0 ? jsonPath.Value.Substring(0, lastDot) : jsonPath.Value;
            })
            .ToHashSet();
    }
}
