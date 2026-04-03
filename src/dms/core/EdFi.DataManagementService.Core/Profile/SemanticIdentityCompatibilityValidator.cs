// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Pre-runtime validation gate that rejects writable profile definitions hiding
/// compiled semantic-identity fields for persisted multi-item collection scopes.
/// Implements Core responsibility #12 per profiles.md.
/// </summary>
/// <remarks>
/// <para>
/// This validator consumes the compiled-scope adapter contract from C1 and the
/// typed error contract from C8. It is a pure function: callers provide compiled
/// scope descriptors and a profile definition, and receive back a list of
/// structured failures (empty if the profile is valid).
/// </para>
/// <para>
/// Integration: This gate runs before request-time merge execution. C5 (pipeline
/// orchestration) or the production adapter factory (DMS-1106) calls this when
/// a writable profile is first associated with a resource's compiled scope catalog.
/// </para>
/// </remarks>
internal static class SemanticIdentityCompatibilityValidator
{
    /// <summary>
    /// Validates that a writable profile definition does not hide semantic identity
    /// members for any persisted multi-item collection scope in the compiled adapter.
    /// </summary>
    /// <param name="profileDefinition">The profile definition to validate.</param>
    /// <param name="resourceName">The resource name whose compiled scopes are being checked.</param>
    /// <param name="compiledScopes">The compiled scope descriptors for the resource.</param>
    /// <returns>
    /// An empty list if the profile is valid; one
    /// <see cref="HiddenSemanticIdentityMembersProfileDefinitionFailure"/> per
    /// incompatible collection scope otherwise.
    /// </returns>
    public static IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> Validate(
        ProfileDefinition profileDefinition,
        string resourceName,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopes
    )
    {
        ArgumentNullException.ThrowIfNull(profileDefinition);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(compiledScopes);

        ContentTypeDefinition? writeContent = profileDefinition
            .Resources.FirstOrDefault(r =>
                r.ResourceName.Equals(resourceName, StringComparison.OrdinalIgnoreCase)
            )
            ?.WriteContentType;

        if (writeContent == null)
        {
            return [];
        }

        var navigator = new ProfileTreeNavigator(writeContent);

        List<HiddenSemanticIdentityMembersProfileDefinitionFailure>? failures = null;

        foreach (CompiledScopeDescriptor scope in compiledScopes)
        {
            if (scope.ScopeKind != ScopeKind.Collection)
            {
                continue;
            }

            if (scope.SemanticIdentityRelativePathsInOrder.IsDefaultOrEmpty)
            {
                continue;
            }

            ProfileTreeNode? node = navigator.Navigate(scope.JsonScope);

            // Scope is hidden by the profile — skip it
            if (node == null)
            {
                continue;
            }

            // All members visible — no identity members can be hidden
            if (node.Value.MemberSelection == MemberSelection.IncludeAll)
            {
                continue;
            }

            ImmutableArray<string> hiddenMembers = GetHiddenSemanticIdentityMembers(
                node.Value,
                scope.SemanticIdentityRelativePathsInOrder
            );

            if (!hiddenMembers.IsEmpty)
            {
                failures ??= [];
                failures.Add(
                    ProfileFailures.HiddenSemanticIdentityMembers(
                        profileName: profileDefinition.ProfileName,
                        resourceName: resourceName,
                        jsonScope: scope.JsonScope,
                        hiddenCanonicalMemberPaths: hiddenMembers
                    )
                );
            }
        }

        return (IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure>?)failures ?? [];
    }

    // -----------------------------------------------------------------------
    //  Member visibility
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the subset of semantic identity paths that are hidden by the
    /// profile tree node's member selection.
    /// </summary>
    private static ImmutableArray<string> GetHiddenSemanticIdentityMembers(
        ProfileTreeNode node,
        ImmutableArray<string> semanticIdentityPaths
    )
    {
        ImmutableArray<string>.Builder? hidden = null;

        ScopeMemberFilter filter = new(node.MemberSelection, node.ExplicitPropertyNames);

        foreach (string path in semanticIdentityPaths)
        {
            bool isHidden = !MemberPathVisibility.IsVisible(filter, path);

            if (isHidden)
            {
                hidden ??= ImmutableArray.CreateBuilder<string>();
                hidden.Add(path);
            }
        }

        return hidden?.ToImmutable() ?? [];
    }
}
