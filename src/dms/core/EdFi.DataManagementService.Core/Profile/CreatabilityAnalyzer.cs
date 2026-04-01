// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Result of creatability analysis for the full request.
/// </summary>
/// <param name="RootResourceCreatable">
/// Whether the root resource is creatable (only meaningful when isCreate=true).
/// </param>
/// <param name="EnrichedScopeStates">
/// Scope states with Creatable flags populated by the analyzer.
/// </param>
/// <param name="EnrichedCollectionItems">
/// Collection items with Creatable flags populated by the analyzer.
/// </param>
/// <param name="Failures">
/// Category-4 creatability violation failures for non-creatable new-visible-create attempts.
/// </param>
public sealed record CreatabilityResult(
    bool RootResourceCreatable,
    ImmutableArray<RequestScopeState> EnrichedScopeStates,
    ImmutableArray<VisibleRequestCollectionItem> EnrichedCollectionItems,
    ImmutableArray<CreatabilityViolationFailure> Failures
);

/// <summary>
/// Implements the full top-down creatability decision model from profiles.md.
/// Enriches C3 outputs with Creatable flags and emits category-4 failures
/// for non-creatable new-visible-create attempts.
/// </summary>
public sealed class CreatabilityAnalyzer(
    IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
    ProfileVisibilityClassifier classifier,
    string profileName,
    string resourceName,
    string method,
    string operation
)
{
    private readonly IReadOnlyDictionary<string, CompiledScopeDescriptor> _scopesByJsonScope =
        scopeCatalog.ToDictionary(s => s.JsonScope);

    /// <summary>
    /// Analyzes creatability for all scope states and collection items, enriching
    /// them with Creatable flags and emitting category-4 failures as needed.
    /// </summary>
    public CreatabilityResult Analyze(
        ImmutableArray<RequestScopeState> requestScopeStates,
        ImmutableArray<VisibleRequestCollectionItem> visibleItems,
        IStoredSideExistenceLookup existenceLookup,
        bool isCreate,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope
    )
    {
        // Build lookup of scope states by JsonScope for quick access.
        // Multiple states can exist for the same JsonScope (e.g. scopes inside collections
        // with different ancestor contexts), so use a list.
        var scopeStatesByJsonScope = new Dictionary<string, List<int>>();
        for (int i = 0; i < requestScopeStates.Length; i++)
        {
            string jsonScope = requestScopeStates[i].Address.JsonScope;
            if (!scopeStatesByJsonScope.TryGetValue(jsonScope, out var indices))
            {
                indices = [];
                scopeStatesByJsonScope[jsonScope] = indices;
            }
            indices.Add(i);
        }

        // Build mutable arrays for enrichment
        var enrichedScopes = requestScopeStates.ToArray();
        var enrichedItems = visibleItems.ToArray();
        List<CreatabilityViolationFailure> failures = [];

        // Track creatability by scope state index (for parent gating)
        var scopeCreatable = new bool[enrichedScopes.Length];
        var itemCreatable = new bool[enrichedItems.Length];

        // Track whether each scope is attempting a new create (vs update/preserve).
        // Needed for parent gating: a parent that is VisiblePresent but NOT attempting
        // a new create is an existing scope that satisfies the gate. A parent that IS
        // attempting a new create satisfies the gate only if it is creatable.
        var scopeIsNewCreate = new bool[enrichedScopes.Length];

        // Build parent-child tree from scope catalog for top-down traversal
        var childrenByParent = new Dictionary<string, List<string>>();
        string? rootJsonScope = null;
        foreach (var scope in scopeCatalog)
        {
            if (scope.ScopeKind == ScopeKind.Root)
            {
                rootJsonScope = scope.JsonScope;
            }

            if (scope.ImmediateParentJsonScope != null)
            {
                if (!childrenByParent.TryGetValue(scope.ImmediateParentJsonScope, out var children))
                {
                    children = [];
                    childrenByParent[scope.ImmediateParentJsonScope] = children;
                }
                children.Add(scope.JsonScope);
            }
        }

        if (rootJsonScope == null)
        {
            // No root scope found — return unmodified inputs
            return new CreatabilityResult(false, requestScopeStates, visibleItems, []);
        }

        // Step 1-3 for root scope
        bool rootCreatable = false;
        if (scopeStatesByJsonScope.TryGetValue(rootJsonScope, out var rootIndices))
        {
            foreach (int rootIdx in rootIndices)
            {
                bool isCreatingNewInstance = isCreate;
                scopeIsNewCreate[rootIdx] = isCreatingNewInstance;
                bool creatable = EvaluateCreatability(
                    rootJsonScope,
                    isCreatingNewInstance,
                    parentIsCreatable: true, // root has no parent gate
                    effectiveSchemaRequiredMembersByScope,
                    failures
                );
                scopeCreatable[rootIdx] = creatable;
                if (isCreate)
                {
                    rootCreatable = creatable;
                }
            }
        }

        // Top-down depth-first traversal of non-root scopes
        ProcessChildScopes(
            rootJsonScope,
            childrenByParent,
            scopeStatesByJsonScope,
            enrichedScopes,
            scopeCreatable,
            scopeIsNewCreate,
            enrichedItems,
            itemCreatable,
            existenceLookup,
            effectiveSchemaRequiredMembersByScope,
            failures
        );

        // Bottom-up co-creation propagation: if creating a scope requires co-creating
        // a newly visible descendant, and that descendant is non-creatable, the parent
        // is also non-creatable. Iterate in reverse depth order (leaves first).
        for (int i = enrichedScopes.Length - 1; i >= 0; i--)
        {
            if (!scopeIsNewCreate[i] || !scopeCreatable[i])
            {
                continue;
            }

            string parentJsonScope = enrichedScopes[i].Address.JsonScope;

            // Root scope creatability is not demoted by bottom-up propagation;
            // root uses its own dedicated failure type.
            if (parentJsonScope == rootJsonScope)
            {
                continue;
            }

            // Check non-collection child scopes of this scope
            if (!childrenByParent.TryGetValue(parentJsonScope, out var childJsonScopes))
            {
                continue;
            }

            foreach (string childJsonScope in childJsonScopes)
            {
                if (
                    !_scopesByJsonScope.TryGetValue(childJsonScope, out var childDesc)
                    || childDesc.ScopeKind == ScopeKind.Collection
                )
                {
                    continue;
                }

                if (!scopeStatesByJsonScope.TryGetValue(childJsonScope, out var childIndices))
                {
                    continue;
                }

                foreach (int childIdx in childIndices)
                {
                    if (scopeIsNewCreate[childIdx] && !scopeCreatable[childIdx])
                    {
                        // Demote parent to non-creatable
                        scopeCreatable[i] = false;

                        // Emit category-4 failure with RequiredVisibleDescendant dependency
                        var childDescriptor = _scopesByJsonScope.TryGetValue(childJsonScope, out var cd)
                            ? cd
                            : null;
                        List<ProfileFailureDiagnostic.CreatabilityDependency> dependencies =
                        [
                            new(
                                ProfileCreatabilityDependencyKind.RequiredVisibleDescendant,
                                DetermineTargetKind(childDescriptor),
                                childJsonScope,
                                childDescriptor?.ScopeKind ?? ScopeKind.NonCollection,
                                false,
                                false
                            ),
                        ];

                        failures.Add(
                            ProfileFailures.VisibleScopeOrItemInsertRejectedWhenNonCreatable(
                                profileName: profileName,
                                resourceName: resourceName,
                                method: method,
                                operation: operation,
                                targetKind: DetermineScopeTargetKind(parentJsonScope),
                                affectedAddress: enrichedScopes[i].Address,
                                hiddenCreationRequiredMemberPaths: [],
                                missingCreationRequiredMemberPaths: [],
                                dependencies: dependencies
                            )
                        );

                        break;
                    }
                }

                if (!scopeCreatable[i])
                {
                    break;
                }
            }
        }

        // Apply creatable flags to produce immutable enriched results
        for (int i = 0; i < enrichedScopes.Length; i++)
        {
            if (scopeCreatable[i] != enrichedScopes[i].Creatable)
            {
                enrichedScopes[i] = enrichedScopes[i] with { Creatable = scopeCreatable[i] };
            }
        }
        for (int i = 0; i < enrichedItems.Length; i++)
        {
            if (itemCreatable[i] != enrichedItems[i].Creatable)
            {
                enrichedItems[i] = enrichedItems[i] with { Creatable = itemCreatable[i] };
            }
        }

        return new CreatabilityResult(rootCreatable, [.. enrichedScopes], [.. enrichedItems], [.. failures]);
    }

    /// <summary>
    /// Recursively processes child scopes in top-down order, evaluating creatability
    /// for non-collection scopes and collection items.
    /// </summary>
    private void ProcessChildScopes(
        string parentJsonScope,
        Dictionary<string, List<string>> childrenByParent,
        Dictionary<string, List<int>> scopeStatesByJsonScope,
        RequestScopeState[] enrichedScopes,
        bool[] scopeCreatable,
        bool[] scopeIsNewCreate,
        VisibleRequestCollectionItem[] enrichedItems,
        bool[] itemCreatable,
        IStoredSideExistenceLookup existenceLookup,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope,
        List<CreatabilityViolationFailure> failures
    )
    {
        if (!childrenByParent.TryGetValue(parentJsonScope, out var children))
        {
            return;
        }

        foreach (string childJsonScope in children)
        {
            if (!_scopesByJsonScope.TryGetValue(childJsonScope, out var childDescriptor))
            {
                continue;
            }

            if (childDescriptor.ScopeKind == ScopeKind.Collection)
            {
                // Process collection items for this collection scope
                ProcessCollectionItems(
                    childJsonScope,
                    parentJsonScope,
                    scopeStatesByJsonScope,
                    scopeCreatable,
                    scopeIsNewCreate,
                    enrichedItems,
                    itemCreatable,
                    existenceLookup,
                    effectiveSchemaRequiredMembersByScope,
                    failures
                );

                // Collections can have child scopes (nested non-collections inside collection items)
                ProcessChildScopes(
                    childJsonScope,
                    childrenByParent,
                    scopeStatesByJsonScope,
                    enrichedScopes,
                    scopeCreatable,
                    scopeIsNewCreate,
                    enrichedItems,
                    itemCreatable,
                    existenceLookup,
                    effectiveSchemaRequiredMembersByScope,
                    failures
                );
            }
            else
            {
                // Non-collection child scope
                ProcessNonCollectionScope(
                    childJsonScope,
                    parentJsonScope,
                    scopeStatesByJsonScope,
                    enrichedScopes,
                    scopeCreatable,
                    scopeIsNewCreate,
                    existenceLookup,
                    effectiveSchemaRequiredMembersByScope,
                    failures
                );

                // Recurse into this scope's children
                ProcessChildScopes(
                    childJsonScope,
                    childrenByParent,
                    scopeStatesByJsonScope,
                    enrichedScopes,
                    scopeCreatable,
                    scopeIsNewCreate,
                    enrichedItems,
                    itemCreatable,
                    existenceLookup,
                    effectiveSchemaRequiredMembersByScope,
                    failures
                );
            }
        }
    }

    /// <summary>
    /// Evaluates creatability for a non-collection child scope, applying parent gating.
    /// </summary>
    private void ProcessNonCollectionScope(
        string jsonScope,
        string parentJsonScope,
        Dictionary<string, List<int>> scopeStatesByJsonScope,
        RequestScopeState[] enrichedScopes,
        bool[] scopeCreatable,
        bool[] scopeIsNewCreate,
        IStoredSideExistenceLookup existenceLookup,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope,
        List<CreatabilityViolationFailure> failures
    )
    {
        if (!scopeStatesByJsonScope.TryGetValue(jsonScope, out var scopeIndices))
        {
            return;
        }

        foreach (int idx in scopeIndices)
        {
            var scopeState = enrichedScopes[idx];

            // Determine if creating a new visible instance (Step 1)
            bool isCreatingNewInstance =
                scopeState.Visibility == ProfileVisibilityKind.VisiblePresent
                && !existenceLookup.VisibleScopeExistsAt(scopeState.Address);
            scopeIsNewCreate[idx] = isCreatingNewInstance;

            // Determine parent creatability gate
            bool parentSatisfiesGate = IsParentGateSatisfied(
                parentJsonScope,
                scopeStatesByJsonScope,
                scopeCreatable,
                scopeIsNewCreate
            );

            if (!isCreatingNewInstance)
            {
                // Step 2: Not creating → Creatable = false, no failure
                scopeCreatable[idx] = false;
            }
            else
            {
                // Step 3: Creating → check parent gate and required members
                bool creatable = EvaluateCreatabilityWithParentGate(
                    scopeState,
                    jsonScope,
                    parentSatisfiesGate,
                    parentJsonScope,
                    effectiveSchemaRequiredMembersByScope,
                    failures
                );
                scopeCreatable[idx] = creatable;
            }
        }
    }

    /// <summary>
    /// Processes collection items for a collection scope, evaluating creatability
    /// with parent gating from the containing scope.
    /// </summary>
    private void ProcessCollectionItems(
        string collectionJsonScope,
        string parentJsonScope,
        Dictionary<string, List<int>> scopeStatesByJsonScope,
        bool[] scopeCreatable,
        bool[] scopeIsNewCreate,
        VisibleRequestCollectionItem[] enrichedItems,
        bool[] itemCreatable,
        IStoredSideExistenceLookup existenceLookup,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope,
        List<CreatabilityViolationFailure> failures
    )
    {
        for (int i = 0; i < enrichedItems.Length; i++)
        {
            var item = enrichedItems[i];
            if (item.Address.JsonScope != collectionJsonScope)
            {
                continue;
            }

            // Step 1 for collection items: always check existence lookup
            bool isCreatingNewInstance = !existenceLookup.VisibleCollectionRowExistsAt(item.Address);

            if (!isCreatingNewInstance)
            {
                // Step 2: Matched visible update → Creatable = false, no failure
                itemCreatable[i] = false;
                continue;
            }

            // Determine parent creatability gate from the containing scope
            bool parentSatisfiesGate = IsParentGateSatisfied(
                parentJsonScope,
                scopeStatesByJsonScope,
                scopeCreatable,
                scopeIsNewCreate
            );

            // Step 3: Creating → check parent gate and required members
            ScopeMemberFilter memberFilter = classifier.GetMemberFilter(collectionJsonScope);
            IReadOnlyList<string> effectiveRequired = effectiveSchemaRequiredMembersByScope.TryGetValue(
                collectionJsonScope,
                out var req
            )
                ? req
                : [];

            var crResult = CreationRequiredMemberResolver.Resolve(
                _scopesByJsonScope[collectionJsonScope],
                effectiveRequired,
                memberFilter
            );

            bool hasHiddenRequired = !crResult.HiddenByProfile.IsEmpty;
            bool creatable = parentSatisfiesGate && !hasHiddenRequired;

            if (!creatable)
            {
                // Emit category-4 failure
                List<ProfileFailureDiagnostic.CreatabilityDependency> dependencies = [];
                if (!parentSatisfiesGate)
                {
                    var parentDescriptor = _scopesByJsonScope.TryGetValue(parentJsonScope, out var pd)
                        ? pd
                        : null;
                    dependencies.Add(
                        new ProfileFailureDiagnostic.CreatabilityDependency(
                            ProfileCreatabilityDependencyKind.ImmediateVisibleParent,
                            DetermineTargetKind(parentDescriptor),
                            parentJsonScope,
                            parentDescriptor?.ScopeKind ?? ScopeKind.Root,
                            false,
                            false
                        )
                    );
                }

                failures.Add(
                    ProfileFailures.VisibleScopeOrItemInsertRejectedWhenNonCreatable(
                        profileName: profileName,
                        resourceName: resourceName,
                        method: method,
                        operation: operation,
                        targetKind: DetermineCollectionItemTargetKind(collectionJsonScope),
                        affectedAddress: item.Address,
                        hiddenCreationRequiredMemberPaths: crResult.HiddenByProfile,
                        missingCreationRequiredMemberPaths: [],
                        dependencies: dependencies.Count > 0 ? dependencies : null
                    )
                );
            }

            itemCreatable[i] = creatable;
        }
    }

    /// <summary>
    /// Evaluates creatability for a scope state (root or non-root without parent gating).
    /// </summary>
    private bool EvaluateCreatability(
        string jsonScope,
        bool isCreatingNewInstance,
        bool parentIsCreatable,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope,
        List<CreatabilityViolationFailure> failures
    )
    {
        if (!isCreatingNewInstance)
        {
            // Step 2: Not creating → Creatable = false, no failure
            return false;
        }

        // Step 3: Creating → check required members
        ScopeMemberFilter memberFilter = classifier.GetMemberFilter(jsonScope);
        IReadOnlyList<string> effectiveRequired = effectiveSchemaRequiredMembersByScope.TryGetValue(
            jsonScope,
            out var req
        )
            ? req
            : [];

        var crResult = CreationRequiredMemberResolver.Resolve(
            _scopesByJsonScope[jsonScope],
            effectiveRequired,
            memberFilter
        );

        bool hasHiddenRequired = !crResult.HiddenByProfile.IsEmpty;
        bool creatable = parentIsCreatable && !hasHiddenRequired;

        if (!creatable && jsonScope == "$")
        {
            failures.Add(
                ProfileFailures.RootCreateRejectedWhenNonCreatable(
                    profileName: profileName,
                    resourceName: resourceName,
                    method: method,
                    operation: operation,
                    hiddenCreationRequiredMemberPaths: crResult.HiddenByProfile,
                    missingCreationRequiredMemberPaths: []
                )
            );
        }

        return creatable;
    }

    /// <summary>
    /// Evaluates creatability for a non-root scope with parent gating applied.
    /// </summary>
    private bool EvaluateCreatabilityWithParentGate(
        RequestScopeState scopeState,
        string jsonScope,
        bool parentCreatable,
        string parentJsonScope,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope,
        List<CreatabilityViolationFailure> failures
    )
    {
        ScopeMemberFilter memberFilter = classifier.GetMemberFilter(jsonScope);
        IReadOnlyList<string> effectiveRequired = effectiveSchemaRequiredMembersByScope.TryGetValue(
            jsonScope,
            out var req
        )
            ? req
            : [];

        var crResult = CreationRequiredMemberResolver.Resolve(
            _scopesByJsonScope[jsonScope],
            effectiveRequired,
            memberFilter
        );

        bool hasHiddenRequired = !crResult.HiddenByProfile.IsEmpty;
        bool creatable = parentCreatable && !hasHiddenRequired;

        if (!creatable)
        {
            List<ProfileFailureDiagnostic.CreatabilityDependency> dependencies = [];
            if (!parentCreatable)
            {
                var parentDescriptor = _scopesByJsonScope.TryGetValue(parentJsonScope, out var pd)
                    ? pd
                    : null;
                dependencies.Add(
                    new ProfileFailureDiagnostic.CreatabilityDependency(
                        ProfileCreatabilityDependencyKind.ImmediateVisibleParent,
                        DetermineTargetKind(parentDescriptor),
                        parentJsonScope,
                        parentDescriptor?.ScopeKind ?? ScopeKind.Root,
                        false,
                        false
                    )
                );
            }

            failures.Add(
                ProfileFailures.VisibleScopeOrItemInsertRejectedWhenNonCreatable(
                    profileName: profileName,
                    resourceName: resourceName,
                    method: method,
                    operation: operation,
                    targetKind: DetermineScopeTargetKind(jsonScope),
                    affectedAddress: scopeState.Address,
                    hiddenCreationRequiredMemberPaths: crResult.HiddenByProfile,
                    missingCreationRequiredMemberPaths: [],
                    dependencies: dependencies.Count > 0 ? dependencies : null
                )
            );
        }

        return creatable;
    }

    /// <summary>
    /// Determines whether the parent gate is satisfied for a child create attempt.
    /// The gate is satisfied when the parent either already exists (is not a new create)
    /// or is itself creatable (a new create that passed its own creatability check).
    /// </summary>
    private static bool IsParentGateSatisfied(
        string parentJsonScope,
        Dictionary<string, List<int>> scopeStatesByJsonScope,
        bool[] scopeCreatable,
        bool[] scopeIsNewCreate
    )
    {
        if (!scopeStatesByJsonScope.TryGetValue(parentJsonScope, out var parentIndices))
        {
            // Parent scope not found in scope states.
            // For root scope "$", the parent gate is always satisfied.
            return parentJsonScope == "$";
        }

        foreach (int idx in parentIndices)
        {
            if (!scopeIsNewCreate[idx])
            {
                // Parent is not attempting a new create — it already exists.
                // The gate is satisfied.
                return true;
            }

            if (scopeCreatable[idx])
            {
                // Parent is attempting a new create and is creatable. Gate satisfied.
                return true;
            }
        }

        // All parent instances are new creates that failed creatability.
        return false;
    }

    /// <summary>
    /// Determines the ProfileCreatabilityTargetKind for a non-collection scope.
    /// </summary>
    private ProfileCreatabilityTargetKind DetermineScopeTargetKind(string jsonScope)
    {
        if (jsonScope == "$")
        {
            return ProfileCreatabilityTargetKind.RootResource;
        }

        if (jsonScope.Contains("._ext."))
        {
            return ProfileCreatabilityTargetKind.ExtensionScope;
        }

        // Check if it's a 1:1 scope (immediate parent is root) or nested
        if (
            _scopesByJsonScope.TryGetValue(jsonScope, out var descriptor)
            && descriptor.ImmediateParentJsonScope == "$"
        )
        {
            return ProfileCreatabilityTargetKind.OneToOneScope;
        }

        return ProfileCreatabilityTargetKind.NestedOrCommonTypeScope;
    }

    /// <summary>
    /// Determines the ProfileCreatabilityTargetKind for a collection item.
    /// </summary>
    private static ProfileCreatabilityTargetKind DetermineCollectionItemTargetKind(string collectionJsonScope)
    {
        if (collectionJsonScope.Contains("._ext."))
        {
            return ProfileCreatabilityTargetKind.ExtensionCollectionItem;
        }

        return ProfileCreatabilityTargetKind.CollectionOrCommonTypeItem;
    }

    /// <summary>
    /// Determines the ProfileCreatabilityTargetKind for a scope descriptor (used in dependency reporting).
    /// </summary>
    private static ProfileCreatabilityTargetKind DetermineTargetKind(CompiledScopeDescriptor? descriptor)
    {
        if (descriptor == null)
        {
            return ProfileCreatabilityTargetKind.RootResource;
        }

        return descriptor.ScopeKind switch
        {
            ScopeKind.Root => ProfileCreatabilityTargetKind.RootResource,
            ScopeKind.Collection => ProfileCreatabilityTargetKind.CollectionOrCommonTypeItem,
            ScopeKind.NonCollection when descriptor.JsonScope.Contains("._ext.") =>
                ProfileCreatabilityTargetKind.ExtensionScope,
            ScopeKind.NonCollection when descriptor.ImmediateParentJsonScope == "$" =>
                ProfileCreatabilityTargetKind.OneToOneScope,
            _ => ProfileCreatabilityTargetKind.NestedOrCommonTypeScope,
        };
    }
}
