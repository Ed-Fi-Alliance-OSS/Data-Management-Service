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
                profileRequest,
                profileAppliedContext,
                bindingsByContainingScope
            );
        }

        // Register key-unification member paths into the drift-check inventory.
        // K-u members are not ordinary bindings (their canonical + presence bindings are
        // resolver-owned), but they are legitimate targets for profile-hidden paths — the
        // resolver evaluates them in ProfileKeyUnificationCore. Without this registration,
        // ValidateStoredScopeMetadata would reject hidden paths targeting k-u members as
        // upstream contract drift, which is wrong.
        foreach (var keyUnificationPlan in tableWritePlan.KeyUnificationPlans)
        {
            foreach (var member in keyUnificationPlan.MembersInOrder)
            {
                var memberPathAbsolute = member.RelativePath.Canonical;
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
        if (profileAppliedContext is not null)
        {
            ValidateStoredScopeMetadata(
                writePlan,
                profileAppliedContext,
                bindingsByContainingScope,
                tableWritePlan
            );
        }

        return dispositions.ToImmutableArray();
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

    private static RootBindingDisposition ClassifyOrdinary(
        ResourceWritePlan writePlan,
        TableWritePlan tableWritePlan,
        int bindingIndex,
        ImmutableArray<string> candidateScopes,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext,
        Dictionary<string, List<GovernedBindingEntry>> bindingsByContainingScope
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
                // Ordinal implies collection-shaped behavior. Slice 3 is root-attached
                // only; collections (and their Ordinal columns) are fenced for slice 4/5.
                // Reaching this arm means upstream fencing failed.
                throw new InvalidOperationException(
                    $"Table '{FormatTable(tableWritePlan)}' contains a "
                        + $"{nameof(WriteValueSource.Ordinal)} binding at index {bindingIndex}, "
                        + "which the profile-aware binding classifier does not support in slice 3. "
                        + "Collection-shaped scopes must be fenced upstream."
                );
        }

        var bindingPath = ResolveBindingRootRelativePath(writePlan, binding, bindingIndex, tableWritePlan);
        var governingPathAbsolute = ResolveBindingGoverningPath(
            writePlan,
            binding,
            bindingIndex,
            tableWritePlan
        );

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

        if (profileAppliedContext is null)
        {
            return RootBindingDisposition.VisibleWritable;
        }

        var storedScope = ProfileMemberGovernanceRules.LookupStoredScope(
            profileAppliedContext,
            containingScope
        );
        if (storedScope is not null)
        {
            if (storedScope.Visibility == ProfileVisibilityKind.Hidden)
            {
                return RootBindingDisposition.HiddenPreserved;
            }
            if (
                ProfileMemberGovernanceRules.IsHiddenGoverned(
                    governingPath,
                    storedScope.HiddenMemberPaths,
                    matchKind
                )
            )
            {
                return RootBindingDisposition.HiddenPreserved;
            }
        }

        var requestScope = ProfileMemberGovernanceRules.LookupRequestScope(profileRequest, containingScope);
        if (requestScope is not null && requestScope.Visibility == ProfileVisibilityKind.VisibleAbsent)
        {
            return RootBindingDisposition.ClearOnVisibleAbsent;
        }

        return RootBindingDisposition.VisibleWritable;
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
        ProfileAppliedWriteContext profileAppliedContext,
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

        foreach (var storedScope in profileAppliedContext.StoredScopeStates)
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

    private readonly record struct GovernedBindingEntry(
        string MemberPath,
        string GoverningPath,
        ProfileMemberGovernanceRules.HiddenPathMatchKind MatchKind
    );
}
