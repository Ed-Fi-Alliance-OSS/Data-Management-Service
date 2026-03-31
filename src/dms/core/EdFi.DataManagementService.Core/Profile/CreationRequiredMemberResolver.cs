// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Result of creation-required member analysis for a single scope.
/// </summary>
/// <param name="AllCreationRequired">All creation-required member paths for the scope.</param>
/// <param name="HiddenByProfile">Creation-required members hidden by the writable profile.</param>
public sealed record CreationRequiredMemberResult(
    ImmutableArray<string> AllCreationRequired,
    ImmutableArray<string> HiddenByProfile
);

/// <summary>
/// Determines which members are creation-required for a compiled scope and
/// which of those are hidden by the writable profile. Used by the
/// creatability analyzer (C4) to decide whether a new visible instance
/// can be created.
/// </summary>
public static class CreationRequiredMemberResolver
{
    private static readonly HashSet<string> StorageManagedValues =
    [
        "id",
        "_etag",
        "documentId",
        "collectionItemId",
        "parentCollectionItemId",
        "lastModifiedDate",
        "createDate",
        "contentVersion",
    ];

    /// <summary>
    /// Resolves creation-required members for a scope and identifies which are hidden.
    /// </summary>
    /// <param name="scope">The compiled scope descriptor.</param>
    /// <param name="effectiveSchemaRequiredMembers">
    /// Required member names from the effective schema for this scope
    /// (non-nullable members without a default value).
    /// </param>
    /// <param name="memberFilter">
    /// The writable profile's member filter for this scope, from
    /// <see cref="ProfileVisibilityClassifier.GetMemberFilter"/>.
    /// </param>
    public static CreationRequiredMemberResult Resolve(
        CompiledScopeDescriptor scope,
        IReadOnlyList<string> effectiveSchemaRequiredMembers,
        ScopeMemberFilter memberFilter
    )
    {
        // 1. Start with effective schema required members
        // 2. Add semantic identity members for collection scopes
        // 3. Remove storage-managed values
        // 4. Deduplicate
        HashSet<string> creationRequired = [];

        foreach (string member in effectiveSchemaRequiredMembers)
        {
            if (!StorageManagedValues.Contains(member))
            {
                creationRequired.Add(member);
            }
        }

        // Add semantic identity members for persisted multi-item collections
        if (!scope.SemanticIdentityRelativePathsInOrder.IsEmpty)
        {
            foreach (string identityPath in scope.SemanticIdentityRelativePathsInOrder)
            {
                // For dotted paths like "schoolReference.schoolId",
                // the top-level member ("schoolReference") is what the profile
                // visibility rules check against.
                string topLevelMember = ExtractTopLevelMember(identityPath);
                if (!StorageManagedValues.Contains(topLevelMember))
                {
                    creationRequired.Add(topLevelMember);
                }
            }
        }

        // Determine which creation-required members are hidden by the profile
        ImmutableArray<string>.Builder hiddenBuilder = ImmutableArray.CreateBuilder<string>();
        foreach (string member in creationRequired)
        {
            if (!IsMemberVisible(memberFilter, member))
            {
                hiddenBuilder.Add(member);
            }
        }

        return new CreationRequiredMemberResult(
            AllCreationRequired: [.. creationRequired],
            HiddenByProfile: hiddenBuilder.ToImmutable()
        );
    }

    private static bool IsMemberVisible(ScopeMemberFilter filter, string name) =>
        filter.Mode switch
        {
            MemberSelection.IncludeOnly => filter.ExplicitNames.Contains(name),
            MemberSelection.ExcludeOnly => !filter.ExplicitNames.Contains(name),
            MemberSelection.IncludeAll => true,
            _ => true,
        };

    /// <summary>
    /// Extracts the top-level member name from a scope-relative path.
    /// For dotted paths like "schoolReference.schoolId", returns "schoolReference".
    /// For flat paths like "classPeriodName", returns the path unchanged.
    /// </summary>
    private static string ExtractTopLevelMember(string path)
    {
        int dotIndex = path.IndexOf('.');
        return dotIndex >= 0 ? path[..dotIndex] : path;
    }
}
