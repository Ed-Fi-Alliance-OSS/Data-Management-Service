// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Shared canonical-JSON-path scope-matching helpers used by the profile binding
/// classifier and the key-unification core. Both consumers need the same
/// "longest-scope-with-segment-boundary" semantics; centralising the logic here
/// avoids byte-for-byte drift between the two copies.
/// </summary>
internal static class ProfileScopePathHelpers
{
    /// <summary>
    /// Returns the longest scope from <paramref name="candidateScopes"/> that
    /// <paramref name="bindingPath"/> equals or begins under (segment-boundary aware).
    /// Returns <c>null</c> when no candidate matches. Callers pass
    /// <paramref name="candidateScopes"/> already sorted longest-first.
    /// </summary>
    internal static string? TryMatchLongestScope(string bindingPath, ImmutableArray<string> candidateScopes)
    {
        foreach (var scope in candidateScopes)
        {
            if (string.Equals(bindingPath, scope, StringComparison.Ordinal))
            {
                return scope;
            }
            if (
                bindingPath.StartsWith(scope, StringComparison.Ordinal)
                && bindingPath.Length > scope.Length
                && bindingPath[scope.Length] == '.'
            )
            {
                return scope;
            }
        }
        return null;
    }

    /// <summary>
    /// Strips <paramref name="scope"/> (and the segment-separator dot) from the front
    /// of <paramref name="bindingPath"/>. When the binding path equals the scope
    /// exactly, returns the empty string. Caller is responsible for ensuring
    /// <paramref name="scope"/> is a prefix match produced by
    /// <see cref="TryMatchLongestScope"/>.
    /// </summary>
    internal static string StripScopePrefix(string bindingPath, string scope)
    {
        if (string.Equals(bindingPath, scope, StringComparison.Ordinal))
        {
            return string.Empty;
        }
        return bindingPath[(scope.Length + 1)..];
    }
}
