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

        // Phase 1: structural duplicate detection across request streams.
        // If any duplicates are found, emit one cluster failure per duplicate
        // and short-circuit — skip phase 2 entirely.
        DetectDuplicateScopeAddresses(
            request.RequestScopeStates.Select(s => s.Address),
            streamName: "RequestScopeStates",
            profileName,
            resourceName,
            method,
            operation,
            failures
        );
        DetectDuplicateCollectionRowAddresses(
            request.VisibleRequestCollectionItems.Select(i => i.Address),
            streamName: "VisibleRequestCollectionItems",
            profileName,
            resourceName,
            method,
            operation,
            failures
        );
        if (failures.Count > 0)
        {
            return [.. failures];
        }

        // Phase 2: per-entry validation.
        var catalogByJsonScope = BuildCatalogLookup(scopeCatalog);
        ValidateRequiredScopeStateCoverage(
            request.RequestScopeStates.Select(s => s.Address),
            GetRequiredRootedNonCollectionScopes(scopeCatalog),
            streamName: "RequestScopeStates",
            profileName,
            resourceName,
            method,
            operation,
            failures
        );
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

        // Phase 1: structural duplicate detection across all four streams.
        DetectDuplicateScopeAddresses(
            context.Request.RequestScopeStates.Select(s => s.Address),
            streamName: "RequestScopeStates",
            profileName,
            resourceName,
            method,
            operation,
            failures
        );
        DetectDuplicateCollectionRowAddresses(
            context.Request.VisibleRequestCollectionItems.Select(i => i.Address),
            streamName: "VisibleRequestCollectionItems",
            profileName,
            resourceName,
            method,
            operation,
            failures
        );
        DetectDuplicateScopeAddresses(
            context.StoredScopeStates.Select(s => s.Address),
            streamName: "StoredScopeStates",
            profileName,
            resourceName,
            method,
            operation,
            failures
        );
        DetectDuplicateCollectionRowAddresses(
            context.VisibleStoredCollectionRows.Select(r => r.Address),
            streamName: "VisibleStoredCollectionRows",
            profileName,
            resourceName,
            method,
            operation,
            failures
        );
        if (failures.Count > 0)
        {
            return [.. failures];
        }

        // Phase 2: per-entry validation (existing behavior).
        var catalogByJsonScope = BuildCatalogLookup(scopeCatalog);
        var requiredRootedNonCollectionScopes = GetRequiredRootedNonCollectionScopes(scopeCatalog);

        ValidateRequiredScopeStateCoverage(
            context.Request.RequestScopeStates.Select(s => s.Address),
            requiredRootedNonCollectionScopes,
            streamName: "RequestScopeStates",
            profileName,
            resourceName,
            method,
            operation,
            failures
        );
        ValidateRequiredScopeStateCoverage(
            context.StoredScopeStates.Select(s => s.Address),
            requiredRootedNonCollectionScopes,
            streamName: "StoredScopeStates",
            profileName,
            resourceName,
            method,
            operation,
            failures
        );

        ValidateRequestContractCore(
            context.Request,
            catalogByJsonScope,
            profileName,
            resourceName,
            method,
            operation,
            failures
        );

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

    /// <summary>
    /// Scope-state completeness check for root-rooted non-collection scopes. These scopes
    /// are the ones whose visibility can directly drive root-row overlay/clear behavior and
    /// executor slice routing without any collection-row identity context. Missing metadata
    /// for one of these scopes must fail closed as a contract mismatch rather than falling
    /// through to a default-visible interpretation.
    /// </summary>
    /// <remarks>
    /// Scopes that reach collection rows through a shared base collection (e.g. a
    /// <c>CollectionExtensionScope</c> whose own walk-up ancestors can't see the base
    /// <c>Collection</c> table, because the base collection is stored under a sibling
    /// scope path) can surface with an empty <c>CollectionAncestorsInOrder</c> even
    /// though their data semantically lives inside a collection row. The <c>[*]</c>
    /// literal in the JSON-scope path is a structural marker of collection-membership,
    /// so exclude any scope containing <c>[*]</c> from the required set regardless of
    /// the ancestor walk's result — those scopes need a collection-row identity
    /// context and can't be validated as root-rooted.
    /// </remarks>
    private static ImmutableArray<CompiledScopeDescriptor> GetRequiredRootedNonCollectionScopes(
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    ) =>
        [
            .. scopeCatalog
                .Where(scope =>
                    scope.ScopeKind != ScopeKind.Collection
                    && scope.CollectionAncestorsInOrder.Length == 0
                    && !scope.JsonScope.Contains("[*]", StringComparison.Ordinal)
                )
                .OrderBy(scope => scope.JsonScope.Length)
                .ThenBy(scope => scope.JsonScope, StringComparer.Ordinal),
        ];

    private static void ValidateRequiredScopeStateCoverage(
        IEnumerable<ScopeInstanceAddress> addresses,
        ImmutableArray<CompiledScopeDescriptor> requiredScopes,
        string streamName,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        var emittedJsonScopes = addresses
            .Select(address => address.JsonScope)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var requiredScope in requiredScopes)
        {
            if (emittedJsonScopes.Contains(requiredScope.JsonScope))
            {
                continue;
            }

            failures.Add(
                ProfileFailures.CoreBackendContractMismatch(
                    ProfileFailureEmitter.BackendProfileWriteContext,
                    $"Core omitted required {streamName} metadata for compiled non-collection scope "
                        + $"'{requiredScope.JsonScope}'.",
                    new ProfileFailureContext(profileName, resourceName, method, operation),
                    new ProfileFailureDiagnostic.Scope(requiredScope.JsonScope, requiredScope.ScopeKind),
                    new ProfileFailureDiagnostic.CompiledScope(requiredScope)
                )
            );
        }
    }

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
        // 1. Catalog lookup
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

        // 2. Kind check
        if (compiledScope.ScopeKind != ScopeKind.Collection)
        {
            failures.Add(
                ProfileFailures.ScopeKindMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    emittedAddressKind: ScopeKind.Collection,
                    compiledScope,
                    address
                )
            );
            return;
        }

        // 3. Parent-in-catalog
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

        // 4. Immediate-parent equality
        if (address.ParentAddress.JsonScope != compiledScope.ImmediateParentJsonScope)
        {
            failures.Add(
                ProfileFailures.ParentScopeMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    compiledScope,
                    address,
                    expectedParentJsonScope: compiledScope.ImmediateParentJsonScope
                )
            );
            return;
        }

        // 5. Ancestor chain
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

        // 6. Semantic identity
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

        // 7. Ancestor semantic identity
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

    /// <summary>
    /// Emits one <see cref="DuplicateScopeAddressCoreBackendContractMismatchFailure"/>
    /// per duplicate cluster in a <see cref="ScopeInstanceAddress"/> stream. Determinism:
    /// failures are emitted in the order duplicates are first detected.
    /// </summary>
    private static void DetectDuplicateScopeAddresses(
        IEnumerable<ScopeInstanceAddress> addresses,
        string streamName,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        var counts = new Dictionary<ScopeInstanceAddress, int>(ScopeInstanceAddressComparer.Instance);
        var firstSeen = new Dictionary<ScopeInstanceAddress, ScopeInstanceAddress>(
            ScopeInstanceAddressComparer.Instance
        );
        var emitOrder = new List<ScopeInstanceAddress>();

        foreach (var address in addresses)
        {
            if (counts.TryGetValue(address, out var existing))
            {
                counts[address] = existing + 1;
                if (existing == 1)
                {
                    emitOrder.Add(firstSeen[address]);
                }
            }
            else
            {
                counts[address] = 1;
                firstSeen[address] = address;
            }
        }

        foreach (var duplicateAddress in emitOrder)
        {
            failures.Add(
                ProfileFailures.DuplicateScopeAddress(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    streamName,
                    counts[duplicateAddress],
                    duplicateAddress
                )
            );
        }
    }

    /// <summary>
    /// Emits one <see cref="DuplicateScopeAddressCoreBackendContractMismatchFailure"/>
    /// per duplicate cluster in a <see cref="CollectionRowAddress"/> stream.
    /// </summary>
    private static void DetectDuplicateCollectionRowAddresses(
        IEnumerable<CollectionRowAddress> addresses,
        string streamName,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        var counts = new Dictionary<CollectionRowAddress, int>(CollectionRowAddressComparer.Instance);
        var firstSeen = new Dictionary<CollectionRowAddress, CollectionRowAddress>(
            CollectionRowAddressComparer.Instance
        );
        var emitOrder = new List<CollectionRowAddress>();

        foreach (var address in addresses)
        {
            if (counts.TryGetValue(address, out var existing))
            {
                counts[address] = existing + 1;
                if (existing == 1)
                {
                    emitOrder.Add(firstSeen[address]);
                }
            }
            else
            {
                counts[address] = 1;
                firstSeen[address] = address;
            }
        }

        foreach (var duplicateAddress in emitOrder)
        {
            failures.Add(
                ProfileFailures.DuplicateScopeAddress(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    streamName,
                    counts[duplicateAddress],
                    duplicateAddress
                )
            );
        }
    }
}
