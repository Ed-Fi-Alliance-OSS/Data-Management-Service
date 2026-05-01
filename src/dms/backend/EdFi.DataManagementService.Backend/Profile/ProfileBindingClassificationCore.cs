// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Scope-family-agnostic core for profile-aware binding classification. Operates on
/// a single <see cref="TableWritePlan"/> (root-table or separate-table) and the
/// shared per-scope profile inputs, producing a per-binding
/// <see cref="RootBindingDisposition"/> array plus the resolver-owned binding index
/// set. Extracted from <see cref="ProfileRootTableBindingClassifier"/> without
/// behavior change so Slice 3's separate-table classifier can reuse the same
/// governance logic.
/// </summary>
internal static class ProfileBindingClassificationCore
{
    /// <summary>
    /// Classifies every column binding on the supplied <paramref name="tableWritePlan"/>
    /// into a <see cref="RootBindingDisposition"/> and enforces the metadata-drift
    /// invariant against the profile context.
    /// </summary>
    internal static ImmutableArray<RootBindingDisposition> ClassifyBindings(
        ResourceWritePlan writePlan,
        TableWritePlan tableWritePlan,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(profileRequest);
        ArgumentNullException.ThrowIfNull(resolverOwnedBindingIndices);

        // Sort scope canonicals longest-first so longest-prefix wins.
        var candidateScopes = BuildCandidateScopeSet(profileRequest, profileAppliedContext);

        return ClassifyBindingsCore(
            writePlan,
            tableWritePlan,
            resolverOwnedBindingIndices,
            ScopeStateLookup.FromProfile(profileRequest, profileAppliedContext),
            candidateScopes,
            hiddenMemberPathsOverride: null
        );
    }

    /// <summary>
    /// Classifies bindings for one structurally-addressed separate-scope instance. The
    /// caller supplies the request and stored scope states already resolved for
    /// <paramref name="scopeAddress"/>, avoiding JsonScope-only lookup across sibling
    /// collection-aligned instances.
    /// </summary>
    internal static ImmutableArray<RootBindingDisposition> ClassifyBindings(
        ResourceWritePlan writePlan,
        TableWritePlan tableWritePlan,
        ScopeInstanceAddress scopeAddress,
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    ) =>
        ClassifyBindings(
            writePlan,
            tableWritePlan,
            scopeAddress,
            requestScope,
            storedScope,
            descendantStates: default,
            resolverOwnedBindingIndices
        );

    /// <summary>
    /// Classifies bindings for one structurally-addressed separate-scope instance, plus
    /// the inlined non-collection descendant scope states whose owner table equals the
    /// direct scope's table and whose ancestor-instance chain matches. Descendant request
    /// and stored scopes participate in candidate-scope resolution and stored-scope
    /// metadata-drift validation alongside the direct scope.
    /// </summary>
    internal static ImmutableArray<RootBindingDisposition> ClassifyBindings(
        ResourceWritePlan writePlan,
        TableWritePlan tableWritePlan,
        ScopeInstanceAddress scopeAddress,
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        ProfileSeparateScopeDescendantStates descendantStates,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(scopeAddress);
        ArgumentNullException.ThrowIfNull(resolverOwnedBindingIndices);

        var candidateScopes = BuildCandidateScopeSet(requestScope, storedScope, descendantStates);

        return ClassifyBindingsCore(
            writePlan,
            tableWritePlan,
            resolverOwnedBindingIndices,
            ScopeStateLookup.FromDirect(scopeAddress, requestScope, storedScope, descendantStates),
            candidateScopes,
            hiddenMemberPathsOverride: null
        );
    }

    /// <summary>
    /// Row-level primitive: classifies every column binding on the supplied
    /// <paramref name="tableWritePlan"/> using pre-computed
    /// <paramref name="hiddenMemberPaths"/> supplied directly by the caller, bypassing
    /// per-scope state derivation and the stored-scope metadata-drift check. Intended for
    /// collection-row overlay where the caller synthesises hidden-path state from the
    /// enclosing scope rather than from <see cref="ProfileAppliedWriteContext"/> stored
    /// scope states.
    /// </summary>
    /// <param name="writePlan">The resource-level write plan.</param>
    /// <param name="tableWritePlan">The single table being classified.</param>
    /// <param name="profileRequest">
    /// The profile-applied write request; used for <c>ClearOnVisibleAbsent</c> scope
    /// lookup and as the source of candidate scope addresses.
    /// </param>
    /// <param name="resolverOwnedBindingIndices">
    /// Binding indices that are managed by the key-unification resolver; classified
    /// unconditionally as <see cref="RootBindingDisposition.StorageManaged"/>.
    /// </param>
    /// <param name="hiddenMemberPaths">
    /// The pre-computed flat set of hidden member paths to apply. Every binding whose
    /// governing path is hidden-governed by this set is classified as
    /// <see cref="RootBindingDisposition.HiddenPreserved"/>. Pass
    /// <see cref="ImmutableArray{T}.Empty"/> for no hidden members.
    /// </param>
    internal static ImmutableArray<RootBindingDisposition> ClassifyBindingsWithExplicitHiddenPaths(
        ResourceWritePlan writePlan,
        TableWritePlan tableWritePlan,
        ProfileAppliedWriteRequest profileRequest,
        ImmutableHashSet<int> resolverOwnedBindingIndices,
        ImmutableArray<string> hiddenMemberPaths
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(profileRequest);
        ArgumentNullException.ThrowIfNull(resolverOwnedBindingIndices);

        // For collection-row classification the containing scope is always the table's own
        // JsonScope. Core does not emit collection scopes in RequestScopeStates, so building
        // candidateScopes from the request would yield an empty set and TryMatchLongestScope
        // would return null for every binding, falling through to VisibleWritable — hiding the
        // hidden-member-path override entirely. Use the table's own scope directly so the
        // scope match is trivially correct and StripScopePrefix produces the bare member path.
        var candidateScopes = ImmutableArray.Create(tableWritePlan.TableModel.JsonScope.Canonical);

        return ClassifyBindingsCore(
            writePlan,
            tableWritePlan,
            resolverOwnedBindingIndices,
            // JsonScope-keyed request lookup: safe because row-level callers supply
            // hidden-member paths explicitly per collection row; no stored-scope state is
            // consulted on this path.
            ScopeStateLookup.FromProfile(profileRequest, profileAppliedContext: null),
            candidateScopes,
            hiddenMemberPathsOverride: hiddenMemberPaths
        );
    }

    /// <summary>
    /// Shared core loop: iterates <paramref name="tableWritePlan"/>'s column bindings,
    /// assigns a <see cref="RootBindingDisposition"/> to each one, optionally runs the
    /// stored-scope metadata-drift check (only when
    /// <paramref name="hiddenMemberPathsOverride"/> is <c>null</c> and
    /// <paramref name="profileAppliedContext"/> is non-null), and returns the disposition
    /// array. When <paramref name="hiddenMemberPathsOverride"/> is non-null, stored-scope
    /// derivation and the drift check are both bypassed; the supplied hidden paths are used
    /// directly for all bindings.
    /// </summary>
    private static ImmutableArray<RootBindingDisposition> ClassifyBindingsCore(
        ResourceWritePlan writePlan,
        TableWritePlan tableWritePlan,
        ImmutableHashSet<int> resolverOwnedBindingIndices,
        ScopeStateLookup scopeStateLookup,
        ImmutableArray<string> candidateScopes,
        ImmutableArray<string>? hiddenMemberPathsOverride
    )
    {
        // Records the (memberPath, governingPath, matchKind) inventory of every ordinary
        // binding that resolved to a profile-governed containing scope. `memberPath` is the
        // binding's own scope-relative path; `governingPath` is the path used for hidden-path
        // matching — equal to `memberPath` for scalar/descriptor, and the owning document-
        // reference root for reference-sourced bindings. Drives the post-pass drift check.
        var bindingsByContainingScope = new Dictionary<string, List<GovernedBindingEntry>>(
            StringComparer.Ordinal
        );

        var dispositions = new RootBindingDisposition[tableWritePlan.ColumnBindings.Length];
        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            if (resolverOwnedBindingIndices.Contains(bindingIndex))
            {
                dispositions[bindingIndex] = RootBindingDisposition.StorageManaged;
                continue;
            }
            dispositions[bindingIndex] = ClassifyOrdinary(
                writePlan,
                tableWritePlan,
                bindingIndex,
                candidateScopes,
                scopeStateLookup,
                bindingsByContainingScope,
                hiddenMemberPathsOverride
            );
        }

        // Register key-unification member paths into the drift-check inventory.
        // K-u members are not ordinary bindings (their canonical + presence bindings are
        // resolver-owned), but they are legitimate targets for profile-hidden paths — the
        // resolver evaluates them in ProfileKeyUnificationCore. Without this registration,
        // ValidateStoredScopeMetadata would reject hidden paths targeting k-u members as
        // upstream contract drift, which is wrong.
        var tableScopeCanonicalForKeyUnification = tableWritePlan.TableModel.JsonScope.Canonical;
        foreach (var keyUnificationPlan in tableWritePlan.KeyUnificationPlans)
        {
            foreach (var member in keyUnificationPlan.MembersInOrder)
            {
                // K-u member.RelativePath is scope-relative per WritePlanContracts; lift it to
                // absolute before scope matching so non-root tables match correctly. See
                // ToAbsoluteBindingPath for path-domain normalisation details.
                var memberPathAbsolute = ToAbsoluteBindingPath(
                    tableScopeCanonicalForKeyUnification,
                    member.RelativePath.Canonical
                );
                var containingScope = TryMatchLongestScope(memberPathAbsolute, candidateScopes);

                if (containingScope is null)
                {
                    continue;
                }

                var strippedMemberPath = StripScopePrefix(memberPathAbsolute, containingScope);
                var matchKind = ProfileMemberGovernanceRules.MatchKindFor(member);
                var governingPath = member switch
                {
                    KeyUnificationMemberWritePlan.ReferenceDerivedMember refDerived => StripScopePrefix(
                        refDerived.ReferenceSource.ReferenceObjectPath.Canonical,
                        containingScope
                    ),
                    _ => strippedMemberPath,
                };

                if (!bindingsByContainingScope.TryGetValue(containingScope, out var bindingsUnderScope))
                {
                    bindingsUnderScope = [];
                    bindingsByContainingScope[containingScope] = bindingsUnderScope;
                }

                bindingsUnderScope.Add(
                    new GovernedBindingEntry(strippedMemberPath, governingPath, matchKind)
                );
            }
        }

        // Fail-closed metadata-drift check: every stored scope and every hidden member path
        // must resolve to at least one binding on this table. Anything that doesn't is
        // upstream Core / write-plan contract drift, not silent under-preservation.
        // Skipped when hiddenMemberPathsOverride is supplied (row-level primitive path)
        // because there is no stored scope context to cross-check; the row-level path runs
        // its own narrower hidden-path coverage check below instead.
        var storedScopesForMetadata = scopeStateLookup.StoredScopesForMetadata();
        if (storedScopesForMetadata.Length > 0 && hiddenMemberPathsOverride is null)
        {
            ValidateStoredScopeMetadata(
                writePlan,
                storedScopesForMetadata,
                bindingsByContainingScope,
                tableWritePlan
            );
        }

        // Row-level hidden-path coverage check. Mirrors the hidden-path half of
        // ValidateStoredScopeMetadata but operates against the single set of caller-supplied
        // paths under the table's own JsonScope. Without this, a row HiddenMemberPath that
        // does not match any governed binding under the table's scope would be silently
        // ignored — risking under-preservation when upstream emits a hidden path that the
        // table's bindings cannot honour.
        if (hiddenMemberPathsOverride is ImmutableArray<string> rowHiddenPaths && rowHiddenPaths.Length > 0)
        {
            ValidateRowLevelHiddenPathCoverage(tableWritePlan, rowHiddenPaths, bindingsByContainingScope);
        }

        return dispositions.ToImmutableArray();
    }

    /// <summary>
    /// Verifies that every row-supplied hidden member path resolves to at least one governed
    /// binding under the table's own JsonScope. The row-level primitive path lacks a
    /// stored-scope-state cross-check (no stored scope context exists for collection rows),
    /// so this narrower check is the sole defense against upstream drift in
    /// <see cref="VisibleStoredCollectionRow.HiddenMemberPaths"/>: a hidden path that names
    /// a member with no matching binding on this table would otherwise be silently ignored.
    /// Throws <see cref="InvalidOperationException"/> when any path fails to match.
    /// </summary>
    private static void ValidateRowLevelHiddenPathCoverage(
        TableWritePlan tableWritePlan,
        ImmutableArray<string> rowHiddenPaths,
        Dictionary<string, List<GovernedBindingEntry>> bindingsByContainingScope
    )
    {
        var tableScopeCanonical = tableWritePlan.TableModel.JsonScope.Canonical;
        if (
            !bindingsByContainingScope.TryGetValue(tableScopeCanonical, out var bindingsUnderScope)
            || bindingsUnderScope.Count == 0
        )
        {
            // No governed bindings under this table's scope at all — every row hidden path is
            // unmatchable. Surface the first one in the diagnostic.
            throw new InvalidOperationException(
                $"Row-level hidden member path '{rowHiddenPaths[0]}' on table "
                    + $"'{FormatTable(tableWritePlan)}' does not resolve to any binding under "
                    + $"scope '{tableScopeCanonical}': the table has no governed bindings under "
                    + "that scope. Upstream Core / write-plan contract drift."
            );
        }

        foreach (var hiddenPath in rowHiddenPaths)
        {
            var singleHiddenPath = ImmutableArray.Create(hiddenPath);
            var matched = bindingsUnderScope.Exists(entry =>
                ProfileMemberGovernanceRules.IsHiddenGoverned(
                    entry.GoverningPath,
                    singleHiddenPath,
                    entry.MatchKind
                )
            );
            if (!matched)
            {
                throw new InvalidOperationException(
                    $"Row-level hidden member path '{hiddenPath}' on table "
                        + $"'{FormatTable(tableWritePlan)}' does not resolve to any binding under "
                        + $"scope '{tableScopeCanonical}'. Upstream Core / write-plan contract drift."
                );
            }
        }
    }

    /// <summary>
    /// Collects the binding indices owned by the post-overlay key-unification resolver
    /// on the supplied <paramref name="tableWritePlan"/>: every canonical binding, plus
    /// every synthetic-presence binding.
    /// </summary>
    internal static ImmutableHashSet<int> CollectResolverOwnedIndices(TableWritePlan tableWritePlan)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);

        var builder = ImmutableHashSet.CreateBuilder<int>();
        foreach (var plan in tableWritePlan.KeyUnificationPlans)
        {
            builder.Add(plan.CanonicalBindingIndex);
            foreach (var member in plan.MembersInOrder)
            {
                if (member.PresenceIsSynthetic && member.PresenceBindingIndex is int presenceBindingIndex)
                {
                    builder.Add(presenceBindingIndex);
                }
            }
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// Builds the candidate scope canonical set by union of request and stored scope addresses,
    /// sorted longest-first so longest-prefix matching is straightforward. Reused by the
    /// key-unification core so classifier and resolver see identical scope ordering. The
    /// <see cref="ImmutableArray{T}"/> return type carries the longest-first ordering contract
    /// in the type system so callers cannot silently reorder it.
    /// </summary>
    internal static ImmutableArray<string> BuildCandidateScopeSet(
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    )
    {
        ArgumentNullException.ThrowIfNull(profileRequest);

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

    internal static ImmutableArray<string> BuildCandidateScopeSet(
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        ProfileSeparateScopeDescendantStates descendantStates = default
    )
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (requestScope is not null)
        {
            set.Add(requestScope.Address.JsonScope);
        }
        if (storedScope is not null)
        {
            set.Add(storedScope.Address.JsonScope);
        }
        if (!descendantStates.RequestScopes.IsDefaultOrEmpty)
        {
            foreach (var s in descendantStates.RequestScopes)
            {
                set.Add(s.Address.JsonScope);
            }
        }
        if (!descendantStates.StoredScopes.IsDefaultOrEmpty)
        {
            foreach (var s in descendantStates.StoredScopes)
            {
                set.Add(s.Address.JsonScope);
            }
        }
        return [.. set.OrderByDescending(s => s.Length).ThenBy(s => s, StringComparer.Ordinal)];
    }

    private static RootBindingDisposition ClassifyOrdinary(
        ResourceWritePlan writePlan,
        TableWritePlan tableWritePlan,
        int bindingIndex,
        ImmutableArray<string> candidateScopes,
        ScopeStateLookup scopeStateLookup,
        Dictionary<string, List<GovernedBindingEntry>> bindingsByContainingScope,
        ImmutableArray<string>? hiddenMemberPathsOverride
    )
    {
        var binding = tableWritePlan.ColumnBindings[bindingIndex];

        switch (binding.Source)
        {
            case WriteValueSource.Precomputed:
            case WriteValueSource.DocumentId:
                return RootBindingDisposition.StorageManaged;
            case WriteValueSource.ParentKeyPart:
                // Root-attached separate tables legitimately carry ParentKeyPart bindings
                // (how the row aligns to its parent root row). The no-profile persister
                // already handles parent-key rewriting in a separate step, not via
                // key-unification. Classify as StorageManaged so the synthesizer skips it
                // during profile overlay; Task 5 will add a ParentKeyPart rewrite step
                // for separate-table rows mirroring the no-profile path.
                return RootBindingDisposition.StorageManaged;
            case WriteValueSource.Ordinal:
                // Row-level callers may supply collection tables with Ordinal bindings.
                // Those bindings are storage-managed (derived from row position). Non-collection
                // callers fail-closed-reject Ordinal because it has no meaning there.
                if (hiddenMemberPathsOverride is null)
                {
                    throw new InvalidOperationException(
                        $"Table '{FormatTable(tableWritePlan)}' contains a "
                            + $"{nameof(WriteValueSource.Ordinal)} binding at index {bindingIndex}, "
                            + "which the non-collection profile-aware binding classifier does not support. "
                            + "Collection-shaped scopes must use row-level classification."
                    );
                }
                return RootBindingDisposition.StorageManaged;
        }

        var bindingPath = ResolveBindingRootRelativePath(writePlan, binding, bindingIndex, tableWritePlan);
        var governingPathAbsolute = ResolveBindingGoverningPath(
            writePlan,
            binding,
            bindingIndex,
            tableWritePlan
        );

        // Scalar/DescriptorReference bindings carry scope-relative paths per WritePlanContracts
        // (see WritePlanJsonPathConventions.DeriveScopeRelativePath); document-reference and
        // reference-derived bindings carry absolute paths (DocumentReferenceBinding.ReferenceObjectPath
        // / ReferenceDerivedValueSourceMetadata.ReferenceObjectPath|ReferenceJsonPath). Lift the
        // scope-relative forms to absolute here so TryMatchLongestScope matches against the
        // candidateScopes (which are absolute RequestScopeState/StoredScopeState addresses).
        // Without this, a binding at scope "$._ext.sample" with a scope-relative path
        // "$.extVisibleScalar" would mis-resolve to "$" and the downstream metadata-drift
        // check would fail "Stored scope '$._ext.sample' does not resolve to any binding".
        var tableScopeCanonical = tableWritePlan.TableModel.JsonScope.Canonical;
        bindingPath = ToAbsoluteBindingPath(tableScopeCanonical, bindingPath);
        governingPathAbsolute = ToAbsoluteBindingPath(tableScopeCanonical, governingPathAbsolute);

        // Longest-prefix scope match. If no profile scope matches, the binding is ungoverned.
        var containingScope = TryMatchLongestScope(bindingPath, candidateScopes);
        if (containingScope is null)
        {
            return RootBindingDisposition.VisibleWritable;
        }

        var memberPath = StripScopePrefix(bindingPath, containingScope);
        var governingPath = StripScopePrefix(governingPathAbsolute, containingScope);
        var matchKind = ProfileMemberGovernanceRules.MatchKindFor(binding.Source);

        // Record this binding under its containing scope so the post-pass drift check can
        // verify every stored scope / hidden-member-path resolves to at least one binding.
        if (!bindingsByContainingScope.TryGetValue(containingScope, out var bindingsUnderScope))
        {
            bindingsUnderScope = [];
            bindingsByContainingScope[containingScope] = bindingsUnderScope;
        }
        bindingsUnderScope.Add(new GovernedBindingEntry(memberPath, governingPath, matchKind));

        // Derive the effective hidden paths for this binding from either the caller-supplied
        // override (row-level primitive path) or the stored scope (scope-state-derived path).
        // Returns (hiddenPaths, whollyHidden) where whollyHidden is true only when the stored
        // scope has Visibility == Hidden (entire scope is preserved). The explicit-override
        // path never sets whollyHidden because callers that supply a hidden-paths set have
        // already resolved scope-level visibility before classifying individual members.
        var (hiddenPaths, whollyHidden) = DeriveHiddenPathsForBinding(
            hiddenMemberPathsOverride,
            scopeStateLookup,
            containingScope
        );

        if (
            whollyHidden
            || ProfileMemberGovernanceRules.IsHiddenGoverned(governingPath, hiddenPaths, matchKind)
        )
        {
            return RootBindingDisposition.HiddenPreserved;
        }

        var requestScope = scopeStateLookup.LookupRequestScope(containingScope);
        if (requestScope is not null && requestScope.Visibility == ProfileVisibilityKind.VisibleAbsent)
        {
            return RootBindingDisposition.ClearOnVisibleAbsent;
        }

        return RootBindingDisposition.VisibleWritable;
    }

    /// <summary>
    /// Returns the effective hidden-member-path set and whole-scope-hidden flag for a single
    /// governed binding index. When <paramref name="hiddenMemberPathsOverride"/> is non-null
    /// (row-level primitive path), the override is returned directly with
    /// <c>whollyHidden = false</c>. Otherwise the stored scope for
    /// <paramref name="containingScope"/> is looked up in <paramref name="profileAppliedContext"/>
    /// (if available); its <see cref="StoredScopeState.HiddenMemberPaths"/> and
    /// <c>Visibility == Hidden</c> flag are returned.
    /// </summary>
    private static (ImmutableArray<string> HiddenPaths, bool WhollyHidden) DeriveHiddenPathsForBinding(
        ImmutableArray<string>? hiddenMemberPathsOverride,
        ScopeStateLookup scopeStateLookup,
        string containingScope
    )
    {
        if (hiddenMemberPathsOverride is ImmutableArray<string> explicitPaths)
        {
            return (explicitPaths, false);
        }

        var storedScope = scopeStateLookup.LookupStoredScope(containingScope);
        if (storedScope is null)
        {
            return ([], false);
        }

        return (storedScope.HiddenMemberPaths, storedScope.Visibility == ProfileVisibilityKind.Hidden);
    }

    /// <summary>
    /// Verifies that every stored scope in the profile context <em>relevant to this table</em>
    /// resolves to at least one ordinary binding on this table, and that every
    /// <c>HiddenMemberPath</c> within each such stored scope is matched (per its binding's
    /// <see cref="ProfileMemberGovernanceRules.HiddenPathMatchKind"/>) by at least one binding
    /// under that scope. Throws <see cref="InvalidOperationException"/> otherwise. This
    /// converts upstream Core / write-plan contract drift into a deterministic invariant
    /// failure rather than silent under-preservation.
    /// </summary>
    /// <remarks>
    /// Slice 3 scope-relevance filter: a stored scope is "relevant to this table" only when
    /// the stored scope is <em>owned by</em> this table — that is, when the longest
    /// table-backed JSON-scope prefix of the stored scope's address equals this table's
    /// own <see cref="DbTableModel.JsonScope"/>. The profile context carries stored scope
    /// states for every scope on the resource (root + extensions); each per-table
    /// classification invocation only owns bindings within scopes rooted at its table, so
    /// stored scopes owned by sibling/parent tables (for example, root <c>$</c> when
    /// classifying the extension table <c>$._ext.sample</c>, or extension
    /// <c>$._ext.sample</c> when classifying the root) must be ignored here — those scopes
    /// are validated by the classifier invocation that owns the table they belong to.
    /// Without this filter, a realistic existing-document context would false-fail on
    /// every cross-table classification because scopes owned by another table don't
    /// resolve to a binding here. Note: a plain descendant filter is incorrect for the
    /// root (<c>$</c>) direction — every scope on the resource is a descendant of
    /// <c>$</c>, which would readmit extension-owned scopes when classifying the root
    /// table. Ownership (longest-table-prefix-equals-this-table) is the correct rule in
    /// both directions.
    /// </remarks>
    private static void ValidateStoredScopeMetadata(
        ResourceWritePlan writePlan,
        ImmutableArray<StoredScopeState> storedScopeStates,
        Dictionary<string, List<GovernedBindingEntry>> bindingsByContainingScope,
        TableWritePlan tableWritePlan
    )
    {
        var tableScopeCanonical = tableWritePlan.TableModel.JsonScope.Canonical;

        // Precompute all table-backed JSON scopes on this resource, longest-first, so
        // ownership resolution for each stored scope picks the longest table-backed
        // prefix (segment-boundary match).
        var tableBackedScopes = writePlan
            .TablePlansInDependencyOrder.Select(tp => tp.TableModel.JsonScope.Canonical)
            .OrderByDescending(s => s.Length)
            .ToImmutableArray();

        foreach (var storedScope in storedScopeStates)
        {
            var scopeCanonical = storedScope.Address.JsonScope;

            // Table-ownership filter: the stored scope is relevant to this table only when
            // the longest table-backed prefix (segment-boundary) of its address equals this
            // table's own scope. Stored scopes owned by another table must be ignored here.
            var ownerTableScope = ResolveOwnerTableScope(scopeCanonical, tableBackedScopes);
            if (!string.Equals(ownerTableScope, tableScopeCanonical, StringComparison.Ordinal))
            {
                continue;
            }

            if (
                !bindingsByContainingScope.TryGetValue(scopeCanonical, out var bindingsUnderScope)
                || bindingsUnderScope.Count == 0
            )
            {
                throw new InvalidOperationException(
                    $"Stored scope '{scopeCanonical}' on table '{FormatTable(tableWritePlan)}' "
                        + "does not resolve to any binding. This indicates upstream "
                        + "Core / write-plan contract drift."
                );
            }

            foreach (var hiddenPath in storedScope.HiddenMemberPaths)
            {
                var singleHiddenPath = ImmutableArray.Create(hiddenPath);
                var matched = bindingsUnderScope.Exists(entry =>
                    ProfileMemberGovernanceRules.IsHiddenGoverned(
                        entry.GoverningPath,
                        singleHiddenPath,
                        entry.MatchKind
                    )
                );
                if (!matched)
                {
                    throw new InvalidOperationException(
                        $"Hidden member path '{hiddenPath}' in stored scope '{scopeCanonical}' "
                            + $"on table '{FormatTable(tableWritePlan)}' does not resolve to any "
                            + "binding under that scope. This indicates upstream "
                            + "Core / write-plan contract drift."
                    );
                }
            }
        }
    }

    /// <summary>
    /// Returns the longest table-backed JSON-scope prefix of <paramref name="scopeAddress"/>
    /// from <paramref name="tableBackedScopesLongestFirst"/>, or <c>null</c> if no
    /// table-backed scope is a segment-boundary prefix of <paramref name="scopeAddress"/>.
    /// Prefix semantics match <see cref="TryMatchLongestScope"/>: a prefix must equal
    /// <paramref name="scopeAddress"/>, or be followed by a <c>.</c> separator. This is the
    /// "which table owns this scope?" resolver — the returned value is the JSON scope of
    /// the table that owns a binding at <paramref name="scopeAddress"/>. The caller treats
    /// a stored scope as relevant to the current table only when this value equals the
    /// current table's scope.
    /// </summary>
    private static string? ResolveOwnerTableScope(
        string scopeAddress,
        ImmutableArray<string> tableBackedScopesLongestFirst
    ) =>
        tableBackedScopesLongestFirst.FirstOrDefault(tableScope =>
            IsEqualOrSegmentPrefix(tableScope, scopeAddress)
        );

    /// <summary>
    /// Finds the <see cref="TableWritePlan"/> that owns <paramref name="scopeAddress"/> by
    /// longest table-backed JSON-scope prefix with segment-boundary semantics. Returns
    /// <c>null</c> if no table-backed ancestor exists (e.g., the scope itself is not under
    /// any table-backed scope canonical). Shares prefix semantics with
    /// <see cref="ResolveOwnerTableScope"/> so profile-scope ownership lookups and per-table
    /// classification ownership resolution use identical rules.
    /// </summary>
    internal static TableWritePlan? ResolveOwnerTablePlan(string scopeAddress, ResourceWritePlan writePlan)
    {
        ArgumentNullException.ThrowIfNull(scopeAddress);
        ArgumentNullException.ThrowIfNull(writePlan);

        TableWritePlan? owner = null;
        var ownerLength = -1;
        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            var tableScope = tablePlan.TableModel.JsonScope.Canonical;
            if (IsEqualOrSegmentPrefix(tableScope, scopeAddress) && tableScope.Length > ownerLength)
            {
                owner = tablePlan;
                ownerLength = tableScope.Length;
            }
        }
        return owner;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="maybePrefix"/> is equal to
    /// <paramref name="scopeAddress"/>, or is a segment-boundary prefix (i.e., followed by
    /// a <c>.</c> separator). Mirrors the segment-boundary rule used by
    /// <see cref="TryMatchLongestScope"/> so table-ownership resolution and binding-to-
    /// scope matching share identical prefix semantics.
    /// </summary>
    private static bool IsEqualOrSegmentPrefix(string maybePrefix, string scopeAddress)
    {
        if (string.Equals(maybePrefix, scopeAddress, StringComparison.Ordinal))
        {
            return true;
        }
        return scopeAddress.StartsWith(maybePrefix, StringComparison.Ordinal)
            && scopeAddress.Length > maybePrefix.Length
            && scopeAddress[maybePrefix.Length] == '.';
    }

    private static string ResolveBindingRootRelativePath(
        ResourceWritePlan writePlan,
        WriteColumnBinding binding,
        int bindingIndex,
        TableWritePlan tableWritePlan
    ) =>
        binding.Source switch
        {
            WriteValueSource.Scalar scalar => scalar.RelativePath.Canonical,
            WriteValueSource.DescriptorReference descriptor => descriptor.RelativePath.Canonical,
            WriteValueSource.DocumentReference documentReference => writePlan
                .Model
                .DocumentReferenceBindings[documentReference.BindingIndex]
                .ReferenceObjectPath
                .Canonical,
            WriteValueSource.ReferenceDerived referenceDerived => referenceDerived
                .ReferenceSource
                .ReferenceJsonPath
                .Canonical,
            _ => throw new InvalidOperationException(
                $"Binding at index {bindingIndex} on table '{FormatTable(tableWritePlan)}' "
                    + $"has a WriteValueSource kind '{binding.Source.GetType().Name}' that the classifier "
                    + "does not know how to resolve. Storage-managed and plan-shape kinds must be filtered upstream."
            ),
        };

    /// <summary>
    /// Absolute JSONPath used to match the binding against profile <c>HiddenMemberPaths</c>.
    /// Equals <see cref="ResolveBindingRootRelativePath"/> for scalar/descriptor bindings; for
    /// document-reference and reference-derived bindings it is the owning reference root
    /// (<c>DocumentReferenceBinding.ReferenceObjectPath</c> / <c>ReferenceDerivedValueSourceMetadata.ReferenceObjectPath</c>),
    /// so a single hidden sub-reference path preserves the whole reference-derived storage family.
    /// </summary>
    private static string ResolveBindingGoverningPath(
        ResourceWritePlan writePlan,
        WriteColumnBinding binding,
        int bindingIndex,
        TableWritePlan tableWritePlan
    ) =>
        binding.Source switch
        {
            WriteValueSource.Scalar scalar => scalar.RelativePath.Canonical,
            WriteValueSource.DescriptorReference descriptor => descriptor.RelativePath.Canonical,
            WriteValueSource.DocumentReference documentReference => writePlan
                .Model
                .DocumentReferenceBindings[documentReference.BindingIndex]
                .ReferenceObjectPath
                .Canonical,
            WriteValueSource.ReferenceDerived referenceDerived => referenceDerived
                .ReferenceSource
                .ReferenceObjectPath
                .Canonical,
            _ => throw new InvalidOperationException(
                $"Binding at index {bindingIndex} on table '{FormatTable(tableWritePlan)}' "
                    + $"has a WriteValueSource kind '{binding.Source.GetType().Name}' that the classifier "
                    + "does not know how to resolve for governance. Storage-managed and plan-shape kinds must be filtered upstream."
            ),
        };

    private static string? TryMatchLongestScope(string bindingPath, ImmutableArray<string> candidateScopes)
    {
        // candidateScopes is pre-sorted longest-first.
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

    private static string StripScopePrefix(string bindingPath, string scope)
    {
        if (string.Equals(bindingPath, scope, StringComparison.Ordinal))
        {
            // Binding path equals the scope itself — member path is empty (rare; an exact-path
            // hidden match would also have to be empty-string for it to govern).
            return string.Empty;
        }
        // bindingPath = scope + '.' + memberPath
        return bindingPath[(scope.Length + 1)..];
    }

    internal static string FormatTable(TableWritePlan tableWritePlan) =>
        $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";

    /// <summary>
    /// Lifts a binding's path to the absolute document domain used by candidate scopes. The
    /// write-plan contract specifies <c>WriteValueSource.Scalar.RelativePath</c>,
    /// <c>WriteValueSource.DescriptorReference.RelativePath</c>, and
    /// <c>KeyUnificationMemberWritePlan.RelativePath</c> as scope-relative (see
    /// <c>WritePlanJsonPathConventions.DeriveScopeRelativePath</c>); reference-derived and
    /// document-reference paths are already absolute. The classifier matches against absolute
    /// <c>RequestScopeState</c>/<c>StoredScopeState</c> addresses, so this helper normalises
    /// both shapes into the absolute domain. Tolerates paths that are already absolute
    /// (detected by <paramref name="tableScope"/>-prefix match) so test doubles and future
    /// upstream refactors that materialise absolute paths stay working.
    /// </summary>
    internal static string ToAbsoluteBindingPath(string tableScope, string bindingPath)
    {
        // Root table scope is "$"; relative paths start with "$.", so after stripping the "$"
        // prefix the concatenation is already correct without any special-case. But a
        // scope-relative path coincident with the root scope ("$" and "$") would produce an
        // empty tail; handle the exact-match case up front.
        if (string.Equals(bindingPath, tableScope, StringComparison.Ordinal))
        {
            return bindingPath;
        }

        // Already-absolute forms: bindingPath is tableScope followed by a "." segment boundary.
        // This covers production emissions for reference-derived/document-reference paths and
        // test doubles that stamp the absolute form directly.
        if (
            bindingPath.StartsWith(tableScope, StringComparison.Ordinal)
            && bindingPath.Length > tableScope.Length
            && bindingPath[tableScope.Length] == '.'
        )
        {
            return bindingPath;
        }

        // Scope-relative form: canonical scope-relative paths start with "$" per
        // JsonPathExpression canonicalisation. Drop the leading "$" and concat onto
        // tableScope so that "$.extVisibleScalar" under scope "$._ext.sample" becomes
        // "$._ext.sample.extVisibleScalar" (the "." separator is carried by the tail).
        if (bindingPath.Length == 0 || bindingPath[0] != '$')
        {
            throw new InvalidOperationException(
                $"Unexpected binding path '{bindingPath}' for table scope '{tableScope}': "
                    + "expected a canonical JSON path beginning with '$'."
            );
        }

        if (string.Equals(tableScope, "$", StringComparison.Ordinal))
        {
            // Root scope: the scope-relative path "$.foo" is already absolute against "$".
            return bindingPath;
        }

        return tableScope + bindingPath.AsSpan(1).ToString();
    }

    private readonly record struct ScopeStateLookup(
        ProfileAppliedWriteRequest? ProfileRequest,
        ProfileAppliedWriteContext? ProfileAppliedContext,
        RequestScopeState? DirectRequestScope,
        StoredScopeState? DirectStoredScope,
        ImmutableDictionary<string, RequestScopeState>? DescendantRequestScopesByJsonScope,
        ImmutableDictionary<string, StoredScopeState>? DescendantStoredScopesByJsonScope,
        bool UsesDirectScope
    )
    {
        public static ScopeStateLookup FromProfile(
            ProfileAppliedWriteRequest profileRequest,
            ProfileAppliedWriteContext? profileAppliedContext
        )
        {
            ArgumentNullException.ThrowIfNull(profileRequest);
            return new(profileRequest, profileAppliedContext, null, null, null, null, UsesDirectScope: false);
        }

        public static ScopeStateLookup FromDirect(
            ScopeInstanceAddress scopeAddress,
            RequestScopeState? requestScope,
            StoredScopeState? storedScope,
            ProfileSeparateScopeDescendantStates descendantStates = default
        )
        {
            ArgumentNullException.ThrowIfNull(scopeAddress);
            ValidateDirectScopeAddress(scopeAddress, requestScope?.Address, nameof(requestScope));
            ValidateDirectScopeAddress(scopeAddress, storedScope?.Address, nameof(storedScope));

            ImmutableDictionary<string, RequestScopeState>? requestMap = null;
            if (!descendantStates.RequestScopes.IsDefaultOrEmpty)
            {
                var b = ImmutableDictionary.CreateBuilder<string, RequestScopeState>(StringComparer.Ordinal);
                foreach (var s in descendantStates.RequestScopes)
                {
                    b[s.Address.JsonScope] = s;
                }
                requestMap = b.ToImmutable();
            }

            ImmutableDictionary<string, StoredScopeState>? storedMap = null;
            if (!descendantStates.StoredScopes.IsDefaultOrEmpty)
            {
                var b = ImmutableDictionary.CreateBuilder<string, StoredScopeState>(StringComparer.Ordinal);
                foreach (var s in descendantStates.StoredScopes)
                {
                    b[s.Address.JsonScope] = s;
                }
                storedMap = b.ToImmutable();
            }

            return new(null, null, requestScope, storedScope, requestMap, storedMap, UsesDirectScope: true);
        }

        public RequestScopeState? LookupRequestScope(string containingScope)
        {
            if (UsesDirectScope)
            {
                if (
                    DirectRequestScope is not null
                    && string.Equals(
                        DirectRequestScope.Address.JsonScope,
                        containingScope,
                        StringComparison.Ordinal
                    )
                )
                {
                    return DirectRequestScope;
                }
                return
                    DescendantRequestScopesByJsonScope is not null
                    && DescendantRequestScopesByJsonScope.TryGetValue(containingScope, out var s)
                    ? s
                    : null;
            }

            return ProfileMemberGovernanceRules.LookupRequestScope(ProfileRequest!, containingScope);
        }

        public StoredScopeState? LookupStoredScope(string containingScope)
        {
            if (UsesDirectScope)
            {
                if (
                    DirectStoredScope is not null
                    && string.Equals(
                        DirectStoredScope.Address.JsonScope,
                        containingScope,
                        StringComparison.Ordinal
                    )
                )
                {
                    return DirectStoredScope;
                }
                return
                    DescendantStoredScopesByJsonScope is not null
                    && DescendantStoredScopesByJsonScope.TryGetValue(containingScope, out var s)
                    ? s
                    : null;
            }

            return ProfileAppliedContext is null
                ? null
                : ProfileMemberGovernanceRules.LookupStoredScope(ProfileAppliedContext, containingScope);
        }

        public ImmutableArray<StoredScopeState> StoredScopesForMetadata()
        {
            if (!UsesDirectScope)
            {
                return ProfileAppliedContext?.StoredScopeStates ?? [];
            }

            var b = ImmutableArray.CreateBuilder<StoredScopeState>();
            if (DirectStoredScope is not null)
            {
                b.Add(DirectStoredScope);
            }
            if (DescendantStoredScopesByJsonScope is not null)
            {
                foreach (var kv in DescendantStoredScopesByJsonScope)
                {
                    b.Add(kv.Value);
                }
            }
            return b.ToImmutable();
        }

        private static void ValidateDirectScopeAddress(
            ScopeInstanceAddress scopeAddress,
            ScopeInstanceAddress? stateAddress,
            string parameterName
        )
        {
            if (
                stateAddress is not null
                && !ScopeInstanceAddressComparer.ScopeInstanceAddressEquals(scopeAddress, stateAddress)
            )
            {
                throw new ArgumentException(
                    $"The supplied {parameterName} address does not match the separate scope instance "
                        + $"address '{scopeAddress.JsonScope}'.",
                    parameterName
                );
            }
        }
    }

    private readonly record struct GovernedBindingEntry(
        string MemberPath,
        string GoverningPath,
        ProfileMemberGovernanceRules.HiddenPathMatchKind MatchKind
    );
}
