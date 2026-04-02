// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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

        return [.. failures];
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

        // Validate request side
        failures.AddRange(
            ValidateRequestContract(
                context.Request,
                scopeCatalog,
                profileName,
                resourceName,
                method,
                operation
            )
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

            // Validate hidden member paths when scope is known
            if (
                storedScopeState.HiddenMemberPaths.Length > 0
                && catalogByJsonScope.TryGetValue(storedScopeState.Address.JsonScope, out var compiledScope)
            )
            {
                var invalidPaths = storedScopeState
                    .HiddenMemberPaths.Where(p =>
                        !compiledScope.CanonicalScopeRelativeMemberPaths.Contains(p)
                    )
                    .ToList();

                if (invalidPaths.Count > 0)
                {
                    failures.Add(
                        ProfileFailures.CanonicalMemberPathMismatch(
                            profileName,
                            resourceName,
                            method,
                            operation,
                            compiledScope,
                            storedScopeState.Address,
                            invalidPaths
                        )
                    );
                }
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

            // Validate hidden member paths when scope is known
            if (
                collectionRow.HiddenMemberPaths.Length > 0
                && catalogByJsonScope.TryGetValue(collectionRow.Address.JsonScope, out var compiledScope)
            )
            {
                var invalidPaths = collectionRow
                    .HiddenMemberPaths.Where(p =>
                        !compiledScope.CanonicalScopeRelativeMemberPaths.Contains(p)
                    )
                    .ToList();

                if (invalidPaths.Count > 0)
                {
                    failures.Add(
                        ProfileFailures.CanonicalMemberPathMismatch(
                            profileName,
                            resourceName,
                            method,
                            operation,
                            compiledScope,
                            collectionRow.Address,
                            invalidPaths
                        )
                    );
                }
            }
        }

        return [.. failures];
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
        }
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
        }
    }

    /// <summary>
    /// Returns true when the emitted ancestor instances align with the compiled
    /// <see cref="CompiledScopeDescriptor.CollectionAncestorsInOrder"/>.
    /// </summary>
    private static bool AncestorChainMatches(
        System.Collections.Immutable.ImmutableArray<AncestorCollectionInstance> emittedAncestors,
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
