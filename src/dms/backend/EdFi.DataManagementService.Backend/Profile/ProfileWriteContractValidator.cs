// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text;
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
        var catalogByJsonScope = BuildCatalogLookup(
            scopeCatalog,
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
        // Scope completeness is not checked here because stored scope states are not yet
        // available (C6 projection has not run); ValidateWriteContext performs that check
        // after stored state is projected so it can correctly skip Hidden scopes.
        // On CREATE there is no stored state, so hidden scopes are invisible to the
        // flattener and merge. Any visible-scope gaps that reach the merge surface as a
        // deterministic ContractMismatch outcome (mapped to a category-5 UnknownFailure
        // by the executor) instead of an unstructured generic 500.
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
        ValidateDuplicateRequestMetadata(request, profileName, resourceName, method, operation, failures);

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
        var catalogByJsonScope = BuildCatalogLookup(
            scopeCatalog,
            profileName,
            resourceName,
            method,
            operation,
            failures
        );

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

        ValidateDuplicateStoredMetadata(context, profileName, resourceName, method, operation, failures);

        // Validate completeness: every non-root, non-collection scope has a RequestScopeState.
        // Pass stored scope states so scopes with Hidden stored visibility can be skipped —
        // hidden scopes do not emit RequestScopeStates because the merge handles them via
        // StoredScopeState, not RequestScopeState.
        ValidateRequestScopeCompleteness(
            context.Request,
            scopeCatalog,
            context.StoredScopeStates,
            profileName,
            resourceName,
            method,
            operation,
            failures
        );

        // Validate completeness on the stored side: every compiled scope reachable through
        // a visible stored collection-ancestor instance (plus the root scope) must have a
        // corresponding StoredScopeState. Without this, a dropped StoredScopeState would
        // silently default to "no hidden members" inside the merge's hidden-member overlay
        // path and could overwrite hidden columns or delete existing rows on VisibleAbsent
        // instead of surfacing a deterministic failure.
        ValidateStoredScopeCompleteness(
            context,
            scopeCatalog,
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
    /// Validates that request-side metadata is unique per compiled address so the merge does not
    /// silently resolve duplicates via first-wins lookup behavior.
    /// </summary>
    private static void ValidateDuplicateRequestMetadata(
        ProfileAppliedWriteRequest request,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        var context = new ProfileFailureContext(profileName, resourceName, method, operation);

        ValidateDuplicateScopeStates(
            request.RequestScopeStates,
            metadataKind: nameof(RequestScopeState),
            context,
            failures
        );
        ValidateDuplicateCollectionItems(
            request.VisibleRequestCollectionItems,
            metadataKind: nameof(VisibleRequestCollectionItem),
            context,
            failures
        );
    }

    /// <summary>
    /// Validates that stored-side metadata is unique per compiled address so hidden-member
    /// preservation and second-pass resolution never depend on first-wins lookup behavior.
    /// </summary>
    private static void ValidateDuplicateStoredMetadata(
        ProfileAppliedWriteContext context,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        var failureContext = new ProfileFailureContext(profileName, resourceName, method, operation);

        ValidateDuplicateStoredScopeStates(
            context.StoredScopeStates,
            metadataKind: nameof(StoredScopeState),
            failureContext,
            failures
        );
        ValidateDuplicateStoredCollectionRows(
            context.VisibleStoredCollectionRows,
            metadataKind: nameof(VisibleStoredCollectionRow),
            failureContext,
            failures
        );
    }

    private static void ValidateDuplicateScopeStates(
        ImmutableArray<RequestScopeState> scopeStates,
        string metadataKind,
        ProfileFailureContext context,
        List<ProfileFailure> failures
    )
    {
        HashSet<string> seenKeys = new(StringComparer.Ordinal);
        HashSet<string> reportedKeys = new(StringComparer.Ordinal);

        foreach (var address in scopeStates.Select(scopeState => scopeState.Address))
        {
            var instanceKey = BuildScopeInstanceKey(address.JsonScope, address.AncestorCollectionInstances);

            if (!seenKeys.Add(instanceKey) && reportedKeys.Add(instanceKey))
            {
                failures.Add(
                    ProfileFailures.CoreBackendContractMismatch(
                        ProfileFailureEmitter.BackendProfileWriteContext,
                        $"Core emitted duplicate {metadataKind} metadata for scope "
                            + $"'{address.JsonScope}' at the same compiled address. "
                            + "Backend requires unique scope metadata and will not apply "
                            + "first-wins lookup behavior.",
                        context,
                        new ProfileFailureDiagnostic.ScopeAddress(address)
                    )
                );
            }
        }
    }

    private static void ValidateDuplicateStoredScopeStates(
        ImmutableArray<StoredScopeState> scopeStates,
        string metadataKind,
        ProfileFailureContext context,
        List<ProfileFailure> failures
    )
    {
        HashSet<string> seenKeys = new(StringComparer.Ordinal);
        HashSet<string> reportedKeys = new(StringComparer.Ordinal);

        foreach (var address in scopeStates.Select(scopeState => scopeState.Address))
        {
            var instanceKey = BuildScopeInstanceKey(address.JsonScope, address.AncestorCollectionInstances);

            if (!seenKeys.Add(instanceKey) && reportedKeys.Add(instanceKey))
            {
                failures.Add(
                    ProfileFailures.CoreBackendContractMismatch(
                        ProfileFailureEmitter.BackendProfileWriteContext,
                        $"Core emitted duplicate {metadataKind} metadata for scope "
                            + $"'{address.JsonScope}' at the same compiled address. "
                            + "Backend requires unique scope metadata and will not apply "
                            + "first-wins lookup behavior.",
                        context,
                        new ProfileFailureDiagnostic.ScopeAddress(address)
                    )
                );
            }
        }
    }

    private static void ValidateDuplicateCollectionItems(
        ImmutableArray<VisibleRequestCollectionItem> collectionItems,
        string metadataKind,
        ProfileFailureContext context,
        List<ProfileFailure> failures
    )
    {
        HashSet<string> seenKeys = new(StringComparer.Ordinal);
        HashSet<string> reportedKeys = new(StringComparer.Ordinal);

        foreach (var address in collectionItems.Select(collectionItem => collectionItem.Address))
        {
            var instanceKey = BuildCollectionRowInstanceKey(address);

            if (!seenKeys.Add(instanceKey) && reportedKeys.Add(instanceKey))
            {
                failures.Add(
                    ProfileFailures.CoreBackendContractMismatch(
                        ProfileFailureEmitter.BackendProfileWriteContext,
                        $"Core emitted duplicate {metadataKind} metadata for collection scope "
                            + $"'{address.JsonScope}' at the same compiled address. "
                            + "Backend requires unique collection metadata and will not apply "
                            + "first-wins lookup behavior.",
                        context,
                        new ProfileFailureDiagnostic.CollectionRow(address)
                    )
                );
            }
        }
    }

    private static void ValidateDuplicateStoredCollectionRows(
        ImmutableArray<VisibleStoredCollectionRow> collectionRows,
        string metadataKind,
        ProfileFailureContext context,
        List<ProfileFailure> failures
    )
    {
        HashSet<string> seenKeys = new(StringComparer.Ordinal);
        HashSet<string> reportedKeys = new(StringComparer.Ordinal);

        foreach (var address in collectionRows.Select(collectionRow => collectionRow.Address))
        {
            var instanceKey = BuildCollectionRowInstanceKey(address);

            if (!seenKeys.Add(instanceKey) && reportedKeys.Add(instanceKey))
            {
                failures.Add(
                    ProfileFailures.CoreBackendContractMismatch(
                        ProfileFailureEmitter.BackendProfileWriteContext,
                        $"Core emitted duplicate {metadataKind} metadata for collection scope "
                            + $"'{address.JsonScope}' at the same compiled address. "
                            + "Backend requires unique collection metadata and will not apply "
                            + "first-wins lookup behavior.",
                        context,
                        new ProfileFailureDiagnostic.CollectionRow(address)
                    )
                );
            }
        }
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

    /// <summary>
    /// Validates that every non-root, non-collection compiled scope has a corresponding
    /// <see cref="RequestScopeState"/> for each visible collection-ancestor instance
    /// it is nested under, unless that specific instance is Hidden in stored state.
    /// </summary>
    /// <remarks>
    /// Per-instance (not scope-name-only) completeness: a scope nested under a collection
    /// must have one <see cref="RequestScopeState"/> per visible collection item. Without
    /// this, Core can emit a <see cref="RequestScopeState"/> for one instance and silently
    /// drop another, and the backend merge's per-instance lookup would surface only as a
    /// <see cref="RelationalWriteMergeSynthesisOutcome.ContractMismatch"/> later in synthesis.
    /// Catching the gap here provides a cleaner category-5 diagnostic with full profile
    /// and resource context before the merge runs.
    ///
    /// Hidden visibility is also checked per-instance: a scope may be VisiblePresent for
    /// one collection item and Hidden for another. A missing <see cref="RequestScopeState"/>
    /// is only tolerated when that specific (scope, ancestor-chain) is Hidden in stored state.
    /// </remarks>
    private static void ValidateRequestScopeCompleteness(
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        ImmutableArray<StoredScopeState> storedScopeStates,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        // Build the set of emitted request-scope instance keys: (JsonScope, ancestorKey).
        var existingRequestKeys = new HashSet<string>(
            request.RequestScopeStates.Select(scopeState =>
                BuildScopeInstanceKey(
                    scopeState.Address.JsonScope,
                    scopeState.Address.AncestorCollectionInstances
                )
            ),
            StringComparer.Ordinal
        );

        // Build the set of Hidden stored-scope instance keys. Only the specific
        // (scope, ancestor-chain) marked Hidden is exempt from requiring a RequestScopeState.
        var hiddenStoredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var storedState in storedScopeStates)
        {
            if (storedState.Visibility == ProfileVisibilityKind.Hidden)
            {
                hiddenStoredKeys.Add(
                    BuildScopeInstanceKey(
                        storedState.Address.JsonScope,
                        storedState.Address.AncestorCollectionInstances
                    )
                );
            }
        }

        // Group visible request collection items by their JsonScope so we can quickly
        // enumerate the instance tuples for any non-collection scope's innermost
        // collection ancestor.
        var visibleItemsByCollectionScope = new Dictionary<string, List<VisibleRequestCollectionItem>>(
            StringComparer.Ordinal
        );
        foreach (var item in request.VisibleRequestCollectionItems)
        {
            if (!visibleItemsByCollectionScope.TryGetValue(item.Address.JsonScope, out var list))
            {
                list = [];
                visibleItemsByCollectionScope[item.Address.JsonScope] = list;
            }
            list.Add(item);
        }

        foreach (var scope in scopeCatalog)
        {
            if (scope.ScopeKind is ScopeKind.Root or ScopeKind.Collection)
            {
                continue;
            }

            foreach (
                var expectedAncestors in EnumerateExpectedAncestorInstances(
                    scope,
                    visibleItemsByCollectionScope
                )
            )
            {
                var key = BuildScopeInstanceKey(scope.JsonScope, expectedAncestors);

                if (hiddenStoredKeys.Contains(key))
                {
                    continue;
                }

                if (!existingRequestKeys.Contains(key))
                {
                    failures.Add(
                        ProfileFailures.CoreBackendContractMismatch(
                            ProfileFailureEmitter.BackendProfileWriteContext,
                            $"Compiled non-collection scope '{scope.JsonScope}' has no corresponding "
                                + $"RequestScopeState for collection-ancestor instance '{key}'.",
                            new ProfileFailureContext(profileName, resourceName, method, operation)
                        )
                    );
                }
            }
        }
    }

    /// <summary>
    /// Validates that every non-root, non-collection compiled scope has a corresponding
    /// <see cref="StoredScopeState"/> for each visible stored collection-ancestor instance
    /// it is nested under.
    /// </summary>
    /// <remarks>
    /// Stored-side mirror of <see cref="ValidateRequestScopeCompleteness"/>. The merge
    /// reads <see cref="StoredScopeState.HiddenMemberPaths"/> via a null-coalescing fallback
    /// to <c>[]</c>, so a missing entry silently degrades to "no hidden members" and can
    /// overwrite preserved columns or drive a delete on VisibleAbsent against real data.
    /// Surfacing the gap here as a category-5 contract mismatch keeps the failure
    /// deterministic instead of letting it manifest as silent data corruption.
    /// </remarks>
    private static void ValidateStoredScopeCompleteness(
        ProfileAppliedWriteContext context,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        // Build the set of emitted stored-scope instance keys: (JsonScope, ancestorKey).
        var existingStoredKeys = new HashSet<string>(
            context.StoredScopeStates.Select(scopeState =>
                BuildScopeInstanceKey(
                    scopeState.Address.JsonScope,
                    scopeState.Address.AncestorCollectionInstances
                )
            ),
            StringComparer.Ordinal
        );

        // Group visible stored collection rows by their JsonScope so we can quickly
        // enumerate the instance tuples for any non-collection scope's innermost
        // collection ancestor on the stored side.
        var visibleStoredByCollectionScope = new Dictionary<string, List<VisibleStoredCollectionRow>>(
            StringComparer.Ordinal
        );
        foreach (var row in context.VisibleStoredCollectionRows)
        {
            if (!visibleStoredByCollectionScope.TryGetValue(row.Address.JsonScope, out var list))
            {
                list = [];
                visibleStoredByCollectionScope[row.Address.JsonScope] = list;
            }
            list.Add(row);
        }

        ValidateTopLevelCollectionRowCoverageFromStoredScopes(
            context.StoredScopeStates,
            scopeCatalog,
            visibleStoredByCollectionScope,
            profileName,
            resourceName,
            method,
            operation,
            failures
        );

        // Root scope is validated explicitly — unlike request-side completeness (where the
        // request body itself signals the root is present), the merge reads root stored
        // hidden-member paths at the very start of synthesis and a missing entry silently
        // defaults to empty, potentially overwriting hidden root columns.
        var rootScope = scopeCatalog.FirstOrDefault(s => s.ScopeKind == ScopeKind.Root);
        if (rootScope is not null)
        {
            var rootKey = BuildScopeInstanceKey(rootScope.JsonScope, []);
            if (!existingStoredKeys.Contains(rootKey))
            {
                failures.Add(
                    ProfileFailures.CoreBackendContractMismatch(
                        ProfileFailureEmitter.BackendProfileWriteContext,
                        $"Compiled root scope '{rootScope.JsonScope}' has no corresponding StoredScopeState.",
                        new ProfileFailureContext(profileName, resourceName, method, operation)
                    )
                );
            }
        }

        foreach (var scope in scopeCatalog)
        {
            if (scope.ScopeKind is ScopeKind.Root or ScopeKind.Collection)
            {
                continue;
            }

            foreach (
                var expectedAncestors in EnumerateExpectedStoredAncestorInstances(
                    scope,
                    visibleStoredByCollectionScope
                )
            )
            {
                var key = BuildScopeInstanceKey(scope.JsonScope, expectedAncestors);

                if (!existingStoredKeys.Contains(key))
                {
                    failures.Add(
                        ProfileFailures.CoreBackendContractMismatch(
                            ProfileFailureEmitter.BackendProfileWriteContext,
                            $"Compiled non-collection scope '{scope.JsonScope}' has no corresponding "
                                + $"StoredScopeState for collection-ancestor instance '{key}'.",
                            new ProfileFailureContext(profileName, resourceName, method, operation)
                        )
                    );
                }
            }
        }
    }

    /// <summary>
    /// Stored-side analog of <see cref="EnumerateExpectedAncestorInstances"/>. Yields a
    /// single empty chain for top-level scopes; yields one chain per visible STORED row of
    /// the innermost collection ancestor for nested scopes.
    /// </summary>
    private static IEnumerable<
        ImmutableArray<AncestorCollectionInstance>
    > EnumerateExpectedStoredAncestorInstances(
        CompiledScopeDescriptor scope,
        Dictionary<string, List<VisibleStoredCollectionRow>> visibleStoredByCollectionScope
    )
    {
        if (scope.CollectionAncestorsInOrder.IsDefaultOrEmpty)
        {
            yield return [];
            yield break;
        }

        var innermostCollection = scope.CollectionAncestorsInOrder[^1];

        if (!visibleStoredByCollectionScope.TryGetValue(innermostCollection, out var rows) || rows.Count == 0)
        {
            // Innermost collection has no visible stored rows: the collection is empty
            // in storage or entirely Hidden, so the merge will not consult StoredScopeStates
            // for descendants and there is nothing to validate.
            yield break;
        }

        foreach (
            var ancestors in rows.Select(row =>
                row.Address.ParentAddress.AncestorCollectionInstances.Add(
                    new AncestorCollectionInstance(row.Address.JsonScope, row.Address.SemanticIdentityInOrder)
                )
            )
        )
        {
            yield return ancestors;
        }
    }

    /// <summary>
    /// Enumerates the expected ancestor-collection-instance chains for a non-collection
    /// scope given the request's visible collection items. Yields a single empty chain
    /// for top-level scopes; yields one chain per visible item of the innermost collection
    /// ancestor for nested scopes.
    /// </summary>
    private static IEnumerable<ImmutableArray<AncestorCollectionInstance>> EnumerateExpectedAncestorInstances(
        CompiledScopeDescriptor scope,
        Dictionary<string, List<VisibleRequestCollectionItem>> visibleItemsByCollectionScope
    )
    {
        if (scope.CollectionAncestorsInOrder.IsDefaultOrEmpty)
        {
            yield return [];
            yield break;
        }

        var innermostCollection = scope.CollectionAncestorsInOrder[^1];

        if (
            !visibleItemsByCollectionScope.TryGetValue(innermostCollection, out var items)
            || items.Count == 0
        )
        {
            // No visible items for the innermost collection → no instances to validate.
            // Either the collection is empty in the request or entirely Hidden; in either
            // case the merge won't materialize these descendants.
            yield break;
        }

        foreach (
            var ancestors in items.Select(item =>
                item.Address.ParentAddress.AncestorCollectionInstances.Add(
                    new AncestorCollectionInstance(
                        item.Address.JsonScope,
                        item.Address.SemanticIdentityInOrder
                    )
                )
            )
        )
        {
            yield return ancestors;
        }
    }

    /// <summary>
    /// Builds a stable string key combining a scope's JsonScope and its ancestor-collection-instance
    /// chain. Encodes IsPresent to distinguish absent from present-null identity members so keys
    /// match candidates keyed elsewhere in the backend merge.
    /// </summary>
    private static string BuildScopeInstanceKey(
        string jsonScope,
        ImmutableArray<AncestorCollectionInstance> ancestors
    ) => $"{jsonScope}|{AncestorKeyHelpers.BuildAncestorKeyFromInstances(ancestors)}";

    private static string BuildCollectionRowInstanceKey(CollectionRowAddress address)
    {
        var sb = new StringBuilder();
        sb.Append(address.JsonScope);
        sb.Append('|');
        sb.Append(address.ParentAddress.JsonScope);
        sb.Append('|');
        sb.Append(
            AncestorKeyHelpers.BuildAncestorKeyFromInstances(
                address.ParentAddress.AncestorCollectionInstances
            )
        );

        foreach (var part in address.SemanticIdentityInOrder)
        {
            sb.Append('\0');
            sb.Append(part.RelativePath);
            sb.Append('\0');
            sb.Append(part.IsPresent ? '1' : '0');
            sb.Append('\0');
            sb.Append(AncestorKeyHelpers.ExtractJsonNodeStringValue(part.Value));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a JsonScope-keyed lookup from the compiled scope catalog. When Core/backend
    /// ever emit duplicate compiled scopes (a design-doc MUST-violation — see profiles.md
    /// §"Scope and Row Address Derivation", "exactly one compiled scope"), the duplicate
    /// is reported as a deterministic category-5 contract mismatch and the first-seen
    /// descriptor is retained so downstream validation can still run. A silent
    /// <c>ToDictionary</c> throw would escape as an unstructured 500 instead.
    /// </summary>
    private static Dictionary<string, CompiledScopeDescriptor> BuildCatalogLookup(
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        var lookup = new Dictionary<string, CompiledScopeDescriptor>(StringComparer.Ordinal);
        HashSet<string> reportedDuplicates = new(StringComparer.Ordinal);

        foreach (var descriptor in scopeCatalog)
        {
            if (
                !lookup.TryAdd(descriptor.JsonScope, descriptor)
                && reportedDuplicates.Add(descriptor.JsonScope)
            )
            {
                failures.Add(
                    ProfileFailures.CoreBackendContractMismatch(
                        ProfileFailureEmitter.BackendProfileWriteContext,
                        $"Compiled scope catalog contains duplicate JsonScope '{descriptor.JsonScope}'. "
                            + "Backend requires exactly one compiled scope per JsonScope and will not "
                            + "apply first-wins lookup behavior.",
                        new ProfileFailureContext(profileName, resourceName, method, operation)
                    )
                );
            }
        }

        return lookup;
    }

    private static void ValidateTopLevelCollectionRowCoverageFromStoredScopes(
        ImmutableArray<StoredScopeState> storedScopeStates,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        Dictionary<string, List<VisibleStoredCollectionRow>> visibleStoredByCollectionScope,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        var topLevelCollectionScopes = new HashSet<string>(
            scopeCatalog
                .Where(scope =>
                    scope.ScopeKind == ScopeKind.Collection
                    && scope.CollectionAncestorsInOrder.IsDefaultOrEmpty
                )
                .Select(scope => scope.JsonScope),
            StringComparer.Ordinal
        );

        if (topLevelCollectionScopes.Count == 0)
        {
            return;
        }

        HashSet<string> visibleTopLevelRows = new(StringComparer.Ordinal);

        foreach (var collectionScope in topLevelCollectionScopes)
        {
            if (
                !visibleStoredByCollectionScope.TryGetValue(collectionScope, out var visibleRows)
                || visibleRows.Count == 0
            )
            {
                continue;
            }

            foreach (var row in visibleRows)
            {
                visibleTopLevelRows.Add(BuildCollectionRowInstanceKey(row.Address));
            }
        }

        foreach (var storedState in storedScopeStates)
        {
            if (
                storedState.Visibility != ProfileVisibilityKind.VisiblePresent
                || storedState.Address.AncestorCollectionInstances.IsEmpty
            )
            {
                continue;
            }

            var topLevelAncestor = storedState.Address.AncestorCollectionInstances[0];

            if (!topLevelCollectionScopes.Contains(topLevelAncestor.JsonScope))
            {
                continue;
            }

            var rowKey = BuildCollectionRowInstanceKey(
                new CollectionRowAddress(
                    topLevelAncestor.JsonScope,
                    new ScopeInstanceAddress("$", []),
                    topLevelAncestor.SemanticIdentityInOrder
                )
            );

            if (!visibleTopLevelRows.Contains(rowKey))
            {
                failures.Add(
                    ProfileFailures.CoreBackendContractMismatch(
                        ProfileFailureEmitter.BackendProfileWriteContext,
                        $"StoredScopeState for top-level collection scope '{topLevelAncestor.JsonScope}' "
                            + $"references collection instance '{rowKey}' without a corresponding "
                            + "VisibleStoredCollectionRow. Backend requires visible stored row "
                            + "coverage for visible top-level collection instances.",
                        new ProfileFailureContext(profileName, resourceName, method, operation)
                    )
                );
            }
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

        // Per profiles.md §"Scope and Row Address Derivation" (line 425): backend MUST
        // fail deterministically when an emitted address does not map to a compiled scope
        // of the expected kind. A ScopeInstanceAddress targets a Root or NonCollection
        // scope; pointing it at a Collection scope would let the merge consume the state
        // via TryGetStoredScopeStateForInstance and treat it as a whole-collection hidden
        // override, which is outside the design contract.
        if (compiledScope.ScopeKind == ScopeKind.Collection)
        {
            failures.Add(
                ProfileFailures.CoreBackendContractMismatch(
                    ProfileFailureEmitter.BackendProfileWriteContext,
                    $"ScopeInstanceAddress targets JsonScope '{address.JsonScope}' which is "
                        + $"compiled as {compiledScope.ScopeKind}. ScopeInstanceAddress requires "
                        + "a Root or NonCollection compiled scope.",
                    new ProfileFailureContext(profileName, resourceName, method, operation),
                    new ProfileFailureDiagnostic.ScopeAddress(address)
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

        // Per profiles.md §"Scope and Row Address Derivation" (line 425): backend MUST
        // fail deterministically when an emitted address does not map to a compiled scope
        // of the expected kind. A CollectionRowAddress targets a Collection scope; pointing
        // it at a Root or NonCollection scope would let the downstream collection merge
        // operate on a scope that has no CollectionMergePlan and misbind row identity.
        if (compiledScope.ScopeKind != ScopeKind.Collection)
        {
            failures.Add(
                ProfileFailures.CoreBackendContractMismatch(
                    ProfileFailureEmitter.BackendProfileWriteContext,
                    $"CollectionRowAddress targets JsonScope '{address.JsonScope}' which is "
                        + $"compiled as {compiledScope.ScopeKind}. CollectionRowAddress requires "
                        + "a Collection compiled scope.",
                    new ProfileFailureContext(profileName, resourceName, method, operation),
                    new ProfileFailureDiagnostic.CollectionRow(address)
                )
            );
            return;
        }

        // Validate ParentAddress.JsonScope is a known scope AND matches the compiled
        // scope's ImmediateParentJsonScope. A wrong-but-known parent would produce
        // incorrect row matching during merge.
        if (
            !catalogByJsonScope.ContainsKey(address.ParentAddress.JsonScope)
            || (
                compiledScope.ImmediateParentJsonScope is not null
                && !string.Equals(
                    address.ParentAddress.JsonScope,
                    compiledScope.ImmediateParentJsonScope,
                    StringComparison.Ordinal
                )
            )
        )
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
