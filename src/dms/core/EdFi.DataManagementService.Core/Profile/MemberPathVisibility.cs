// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Shared visibility logic for scope-relative member paths. Profile member
/// filters operate at top-level member granularity: including a top-level
/// member (e.g., "schoolReference") transitively includes all its descendant
/// properties (e.g., "schoolReference.schoolId"). This class encodes that
/// rule as a single source of truth consumed by all pipeline stages.
/// </summary>
internal static class MemberPathVisibility
{
    /// <summary>
    /// Determines whether a scope-relative member path is visible under the
    /// given filter. For dotted paths the top-level member is extracted first.
    /// </summary>
    public static bool IsVisible(ScopeMemberFilter filter, string memberPath)
    {
        string topLevel = ExtractTopLevelMember(memberPath);
        return filter.Mode switch
        {
            MemberSelection.IncludeOnly => filter.ExplicitNames.Contains(topLevel),
            MemberSelection.ExcludeOnly => !filter.ExplicitNames.Contains(topLevel),
            MemberSelection.IncludeAll => true,
            _ => true,
        };
    }

    /// <summary>
    /// Extracts the top-level member name from a scope-relative path.
    /// For "schoolReference.schoolId" returns "schoolReference".
    /// For "classPeriodName" returns the path unchanged.
    /// </summary>
    public static string ExtractTopLevelMember(string path)
    {
        int dotIndex = path.IndexOf('.');
        return dotIndex >= 0 ? path[..dotIndex] : path;
    }
}
