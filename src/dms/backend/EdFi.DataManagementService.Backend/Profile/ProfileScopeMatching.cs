// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

internal static class ProfileScopeMatching
{
    internal static ImmutableArray<string> BuildCandidateScopeSet(
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    )
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var state in profileRequest.RequestScopeStates)
        {
            set.Add(state.Address.JsonScope);
        }

        if (profileAppliedContext is not null)
        {
            foreach (var state in profileAppliedContext.StoredScopeStates)
            {
                set.Add(state.Address.JsonScope);
            }
        }

        return [.. set.OrderByDescending(s => s.Length).ThenBy(s => s, StringComparer.Ordinal)];
    }

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

    internal static string StripScopePrefix(string bindingPath, string scope)
    {
        if (string.Equals(bindingPath, scope, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return bindingPath[(scope.Length + 1)..];
    }

    internal static string NormalizeGovernancePath(string bindingPath, string scope)
    {
        var strippedPath = StripScopePrefix(bindingPath, scope);
        if (strippedPath.Length > 0)
        {
            return strippedPath;
        }

        if (scope == "$")
        {
            return string.Empty;
        }

        return scope[2..];
    }

    internal static string NormalizeReferenceGovernancePath(
        DocumentReferenceBinding documentReferenceBinding,
        string containingScope
    ) =>
        NormalizeGovernancePath(
            containingScope == "$" || documentReferenceBinding.IdentityBindings.Count == 0
                ? documentReferenceBinding.ReferenceObjectPath.Canonical
                : documentReferenceBinding.IdentityBindings[0].ReferenceJsonPath.Canonical,
            containingScope
        );
}
