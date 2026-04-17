// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Validates Core-emitted profile contract addresses against the compiled scope catalog.
/// Emits deterministic category-5 contract-mismatch diagnostics when addresses don't align.
/// </summary>
internal static class ProfileWriteContractValidator
{
    /// <summary>
    /// Validates a <see cref="ProfileAppliedWriteRequest"/> against the compiled scope catalog.
    /// Returns an empty array when the contract is valid.
    /// </summary>
    public static ProfileFailure[] ValidateRequestContract(
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        string profileName,
        string resourceName,
        string method,
        string operation
    )
    {
        var failures = new List<ProfileFailure>();
        var catalogByJsonScope = BuildCatalogLookup(scopeCatalog);
        ValidateRequestContractCore(
            request,
            catalogByJsonScope,
            profileName,
            resourceName,
            method,
            operation,
            failures
        );
        return [.. failures];
    }

    private static void ValidateRequestContractCore(
        ProfileAppliedWriteRequest request,
        Dictionary<string, CompiledScopeDescriptor> catalogByJsonScope,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        foreach (var scopeState in request.RequestScopeStates)
        {
            ValidateScopeInstanceAddress(
                scopeState.Address,
                catalogByJsonScope,
                profileName,
                resourceName,
                method,
                operation,
                failures
            );
        }

        foreach (var collectionItem in request.VisibleRequestCollectionItems)
        {
            ValidateCollectionRowAddress(
                collectionItem.Address,
                catalogByJsonScope,
                profileName,
                resourceName,
                method,
                operation,
                failures
            );
        }
    }

    /// <summary>
    /// Validates a <see cref="ProfileAppliedWriteContext"/> against the compiled scope catalog.
    /// Returns an empty array when the contract is valid.
    /// </summary>
    public static ProfileFailure[] ValidateWriteContext(
        ProfileAppliedWriteContext context,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        string profileName,
        string resourceName,
        string method,
        string operation
    )
    {
        var failures = new List<ProfileFailure>();
        var catalogByJsonScope = BuildCatalogLookup(scopeCatalog);

        // Validate request side (reuse the already-built catalog lookup)
        ValidateRequestContractCore(
            context.Request,
            catalogByJsonScope,
            profileName,
            resourceName,
            method,
            operation,
            failures
        );

        // Validate stored scope states
        foreach (var storedScopeState in context.StoredScopeStates)
        {
            ValidateScopeInstanceAddress(
                storedScopeState.Address,
                catalogByJsonScope,
                profileName,
                resourceName,
                method,
                operation,
                failures
            );

            if (
                FindInvalidHiddenMemberPaths(
                    storedScopeState.Address.JsonScope,
                    storedScopeState.HiddenMemberPaths,
                    catalogByJsonScope
                ) is
                { } invalidScopePaths
            )
            {
                failures.Add(
                    ProfileFailures.CanonicalMemberPathMismatch(
                        profileName,
                        resourceName,
                        method,
                        operation,
                        catalogByJsonScope[storedScopeState.Address.JsonScope],
                        storedScopeState.Address,
                        invalidScopePaths
                    )
                );
            }
        }

        // Validate visible stored collection rows
        foreach (var collectionRow in context.VisibleStoredCollectionRows)
        {
            ValidateCollectionRowAddress(
                collectionRow.Address,
                catalogByJsonScope,
                profileName,
                resourceName,
                method,
                operation,
                failures
            );

            if (
                FindInvalidHiddenMemberPaths(
                    collectionRow.Address.JsonScope,
                    collectionRow.HiddenMemberPaths,
                    catalogByJsonScope
                ) is
                { } invalidRowPaths
            )
            {
                failures.Add(
                    ProfileFailures.CanonicalMemberPathMismatch(
                        profileName,
                        resourceName,
                        method,
                        operation,
                        catalogByJsonScope[collectionRow.Address.JsonScope],
                        collectionRow.Address,
                        invalidRowPaths
                    )
                );
            }
        }

        return [.. failures];
    }

    /// <summary>
    /// Returns the list of hidden member paths that are not in the compiled scope's canonical
    /// member paths, or null when all paths are valid or the scope is not in the catalog.
    /// </summary>
    private static List<string>? FindInvalidHiddenMemberPaths(
        string jsonScope,
        ImmutableArray<string> hiddenMemberPaths,
        Dictionary<string, CompiledScopeDescriptor> catalogByJsonScope
    )
    {
        if (hiddenMemberPaths.Length == 0)
        {
            return null;
        }

        if (!catalogByJsonScope.TryGetValue(jsonScope, out var compiledScope))
        {
            return null;
        }

        var invalidPaths = hiddenMemberPaths
            .Where(p => !compiledScope.CanonicalScopeRelativeMemberPaths.Contains(p))
            .ToList();

        return invalidPaths.Count > 0 ? invalidPaths : null;
    }

    private static Dictionary<string, CompiledScopeDescriptor> BuildCatalogLookup(
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    ) => scopeCatalog.ToDictionary(d => d.JsonScope);

    private static void ValidateScopeInstanceAddress(
        ScopeInstanceAddress address,
        Dictionary<string, CompiledScopeDescriptor> catalogByJsonScope,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        if (!catalogByJsonScope.TryGetValue(address.JsonScope, out var compiledScope))
        {
            failures.Add(
                ProfileFailures.UnknownJsonScope(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    address.JsonScope,
                    ScopeKind.NonCollection
                )
            );
            return;
        }

        // Kind check: a ScopeInstanceAddress must target Root or NonCollection.
        if (compiledScope.ScopeKind == ScopeKind.Collection)
        {
            failures.Add(
                ProfileFailures.ScopeKindMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    emittedAddressKind: address.JsonScope == "$" ? ScopeKind.Root : ScopeKind.NonCollection,
                    compiledScope,
                    address
                )
            );
            return;
        }

        if (!AncestorChainMatches(address.AncestorCollectionInstances, compiledScope))
        {
            failures.Add(
                ProfileFailures.AncestorChainMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    compiledScope,
                    address
                )
            );
            return;
        }

        // Validate semantic identity on each ancestor collection instance
        ValidateAncestorSemanticIdentity(
            address.AncestorCollectionInstances,
            catalogByJsonScope,
            profileName,
            resourceName,
            method,
            operation,
            new ProfileFailureDiagnostic.ScopeAddress(address),
            failures
        );
    }

    private static void ValidateCollectionRowAddress(
        CollectionRowAddress address,
        Dictionary<string, CompiledScopeDescriptor> catalogByJsonScope,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        if (!catalogByJsonScope.TryGetValue(address.JsonScope, out var compiledScope))
        {
            failures.Add(
                ProfileFailures.UnknownJsonScope(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    address.JsonScope,
                    ScopeKind.Collection
                )
            );
            return;
        }

        // Validate ParentAddress.JsonScope is a known scope
        if (!catalogByJsonScope.ContainsKey(address.ParentAddress.JsonScope))
        {
            failures.Add(
                ProfileFailures.ParentScopeMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    compiledScope,
                    address
                )
            );
            return;
        }

        if (!AncestorChainMatches(address.ParentAddress.AncestorCollectionInstances, compiledScope))
        {
            failures.Add(
                ProfileFailures.AncestorChainMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    compiledScope,
                    address
                )
            );
            return;
        }

        // Validate semantic identity part count and paths for this collection row
        ValidateSemanticIdentity(
            address.SemanticIdentityInOrder,
            compiledScope,
            profileName,
            resourceName,
            method,
            operation,
            address,
            failures
        );

        // Validate semantic identity on each ancestor collection instance
        ValidateAncestorSemanticIdentity(
            address.ParentAddress.AncestorCollectionInstances,
            catalogByJsonScope,
            profileName,
            resourceName,
            method,
            operation,
            new ProfileFailureDiagnostic.CollectionRow(address),
            failures
        );
    }

    /// <summary>
    /// Validates that the emitted semantic identity matches the compiled scope's
    /// expected identity paths in count and path values.
    /// </summary>
    private static void ValidateSemanticIdentity(
        ImmutableArray<SemanticIdentityPart> emittedIdentity,
        CompiledScopeDescriptor compiledScope,
        string profileName,
        string resourceName,
        string method,
        string operation,
        CollectionRowAddress address,
        List<ProfileFailure> failures
    )
    {
        var expectedPaths = compiledScope.SemanticIdentityRelativePathsInOrder;

        // Check part count
        if (emittedIdentity.Length != expectedPaths.Length)
        {
            failures.Add(
                ProfileFailures.SemanticIdentityMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    compiledScope,
                    address
                )
            );
            return;
        }

        // Check each path matches in order
        for (int i = 0; i < expectedPaths.Length; i++)
        {
            if (emittedIdentity[i].RelativePath != expectedPaths[i])
            {
                failures.Add(
                    ProfileFailures.SemanticIdentityMismatch(
                        profileName,
                        resourceName,
                        method,
                        operation,
                        compiledScope,
                        address
                    )
                );
                return;
            }
        }
    }

    /// <summary>
    /// Validates semantic identity on each ancestor collection instance against
    /// the compiled scope catalog.
    /// </summary>
    private static void ValidateAncestorSemanticIdentity(
        ImmutableArray<AncestorCollectionInstance> ancestors,
        Dictionary<string, CompiledScopeDescriptor> catalogByJsonScope,
        string profileName,
        string resourceName,
        string method,
        string operation,
        ProfileFailureDiagnostic addressDiagnostic,
        List<ProfileFailure> failures
    )
    {
        foreach (var ancestor in ancestors)
        {
            if (!catalogByJsonScope.TryGetValue(ancestor.JsonScope, out var ancestorScope))
            {
                // Already caught by AncestorChainMatches — skip to avoid double-reporting
                continue;
            }

            var expectedPaths = ancestorScope.SemanticIdentityRelativePathsInOrder;

            if (ancestor.SemanticIdentityInOrder.Length != expectedPaths.Length)
            {
                failures.Add(
                    ProfileFailures.AncestorSemanticIdentityMismatch(
                        profileName,
                        resourceName,
                        method,
                        operation,
                        ancestorScope,
                        ancestor,
                        addressDiagnostic
                    )
                );
                continue;
            }

            for (int i = 0; i < expectedPaths.Length; i++)
            {
                if (ancestor.SemanticIdentityInOrder[i].RelativePath != expectedPaths[i])
                {
                    failures.Add(
                        ProfileFailures.AncestorSemanticIdentityMismatch(
                            profileName,
                            resourceName,
                            method,
                            operation,
                            ancestorScope,
                            ancestor,
                            addressDiagnostic
                        )
                    );
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Returns true when the emitted ancestor instances align with the compiled
    /// <see cref="CompiledScopeDescriptor.CollectionAncestorsInOrder"/>.
    /// </summary>
    private static bool AncestorChainMatches(
        ImmutableArray<AncestorCollectionInstance> emittedAncestors,
        CompiledScopeDescriptor compiledScope
    )
    {
        var expected = compiledScope.CollectionAncestorsInOrder;

        if (emittedAncestors.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (emittedAncestors[i].JsonScope != expected[i])
            {
                return false;
            }
        }

        return true;
    }
}
