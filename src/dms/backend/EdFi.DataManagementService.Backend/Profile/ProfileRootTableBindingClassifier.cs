// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Per-binding disposition produced by <see cref="IProfileRootTableBindingClassifier"/>.
/// </summary>
internal enum RootBindingDisposition
{
    /// <summary>
    /// The profile makes this binding writable in this request. The synthesizer should
    /// evaluate the binding from the writable request body.
    /// </summary>
    VisibleWritable,

    /// <summary>
    /// The profile hides this binding on the stored side (either the whole scope is hidden,
    /// or the binding's member path is on the stored hidden-paths list). The synthesizer
    /// must preserve the stored value.
    /// </summary>
    HiddenPreserved,

    /// <summary>
    /// The profile makes this binding's containing scope visible but the request omits it.
    /// The synthesizer should clear the stored value.
    /// </summary>
    ClearOnVisibleAbsent,

    /// <summary>
    /// The binding is storage-managed (DocumentId, Precomputed, or resolver-owned). The
    /// synthesizer must not derive its value from request JSON.
    /// </summary>
    StorageManaged,
}

/// <summary>
/// Classification result for the root table's column bindings.
/// </summary>
internal sealed record ProfileRootTableBindingClassification(
    ImmutableArray<RootBindingDisposition> BindingsByIndex,
    ImmutableHashSet<int> ResolverOwnedBindingIndices
);

internal interface IProfileRootTableBindingClassifier
{
    ProfileRootTableBindingClassification Classify(
        ResourceWritePlan writePlan,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    );
}

internal sealed class ProfileRootTableBindingClassifier : IProfileRootTableBindingClassifier
{
    public ProfileRootTableBindingClassification Classify(
        ResourceWritePlan writePlan,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(profileRequest);

        var rootTable = writePlan.TablePlansInDependencyOrder[0];
        var resolverOwned = CollectResolverOwnedIndices(rootTable);

        // Sort scope canonicals longest-first so longest-prefix wins.
        var candidateScopes = ProfileScopeMatching.BuildCandidateScopeSet(
            profileRequest,
            profileAppliedContext
        );

        // Records the (memberPath, governingPath, matchKind) inventory of every ordinary
        // binding that resolved to a profile-governed containing scope. `memberPath` is the
        // binding's own scope-relative path; `governingPath` is the path used for hidden-path
        // matching — equal to `memberPath` for scalar/descriptor, and the owning document-
        // reference root for reference-sourced bindings. Drives the post-pass drift check.
        var bindingsByContainingScope = new Dictionary<string, List<GovernedBindingEntry>>(
            StringComparer.Ordinal
        );

        var dispositions = new RootBindingDisposition[rootTable.ColumnBindings.Length];
        for (var bindingIndex = 0; bindingIndex < rootTable.ColumnBindings.Length; bindingIndex++)
        {
            if (resolverOwned.Contains(bindingIndex))
            {
                dispositions[bindingIndex] = RootBindingDisposition.StorageManaged;
                continue;
            }
            dispositions[bindingIndex] = ClassifyOrdinary(
                writePlan,
                rootTable,
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
        // resolver evaluates them in ProfileRootKeyUnificationResolver.EvaluateMember.
        // Without this registration, ValidateStoredScopeMetadata would reject hidden
        // paths targeting k-u members as upstream contract drift, which is wrong.
        foreach (var keyUnificationPlan in rootTable.KeyUnificationPlans)
        {
            foreach (var member in keyUnificationPlan.MembersInOrder)
            {
                var memberPathAbsolute = member.RelativePath.Canonical;
                var containingScope = ProfileScopeMatching.TryMatchLongestScope(
                    memberPathAbsolute,
                    candidateScopes
                );
                if (containingScope is null)
                {
                    continue;
                }
                var strippedMemberPath = ProfileScopeMatching.StripScopePrefix(
                    memberPathAbsolute,
                    containingScope
                );
                var matchKind = ProfileMemberGovernanceRules.MatchKindFor(member);
                var governingPath = member switch
                {
                    KeyUnificationMemberWritePlan.ReferenceDerivedMember refDerived =>
                        ProfileScopeMatching.StripScopePrefix(
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
        // must resolve to at least one root-table binding. Anything that doesn't is upstream
        // Core / write-plan contract drift, not silent under-preservation.
        if (profileAppliedContext is not null)
        {
            ValidateStoredScopeMetadata(profileAppliedContext, bindingsByContainingScope, rootTable);
        }

        return new ProfileRootTableBindingClassification(dispositions.ToImmutableArray(), resolverOwned);
    }

    private static ImmutableHashSet<int> CollectResolverOwnedIndices(TableWritePlan rootTable)
    {
        var builder = ImmutableHashSet.CreateBuilder<int>();
        foreach (var plan in rootTable.KeyUnificationPlans)
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

    private static RootBindingDisposition ClassifyOrdinary(
        ResourceWritePlan writePlan,
        TableWritePlan rootTable,
        int bindingIndex,
        ImmutableArray<string> candidateScopes,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext,
        Dictionary<string, List<GovernedBindingEntry>> bindingsByContainingScope
    )
    {
        var binding = rootTable.ColumnBindings[bindingIndex];

        switch (binding.Source)
        {
            case WriteValueSource.Precomputed:
            case WriteValueSource.DocumentId:
                return RootBindingDisposition.StorageManaged;
            case WriteValueSource.ParentKeyPart:
            case WriteValueSource.Ordinal:
                throw new InvalidOperationException(
                    $"Root table '{FormatTable(rootTable)}' contains a "
                        + $"{binding.Source.GetType().Name} binding at index {bindingIndex}, "
                        + "which is a plan-shape violation for a root table."
                );
        }

        var bindingPath = ResolveBindingRootRelativePath(writePlan, binding, bindingIndex, rootTable);
        var governingPathAbsolute = ResolveBindingGoverningPath(writePlan, binding, bindingIndex, rootTable);

        // Longest-prefix scope match. If no profile scope matches, the binding is ungoverned.
        var containingScope = ProfileScopeMatching.TryMatchLongestScope(bindingPath, candidateScopes);
        if (containingScope is null)
        {
            return RootBindingDisposition.VisibleWritable;
        }

        var memberPath = ProfileScopeMatching.StripScopePrefix(bindingPath, containingScope);
        var governingPath = ProfileScopeMatching.StripScopePrefix(governingPathAbsolute, containingScope);
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
    /// Verifies that every stored scope in the profile context resolves to at least one
    /// ordinary root-table binding, and that every <c>HiddenMemberPath</c> within each stored
    /// scope is matched (per its binding's <see cref="ProfileMemberGovernanceRules.HiddenPathMatchKind"/>)
    /// by at least one binding under that scope. Throws <see cref="InvalidOperationException"/>
    /// otherwise. This converts upstream Core / write-plan contract drift into a deterministic
    /// invariant failure rather than silent under-preservation.
    /// </summary>
    private static void ValidateStoredScopeMetadata(
        ProfileAppliedWriteContext profileAppliedContext,
        Dictionary<string, List<GovernedBindingEntry>> bindingsByContainingScope,
        TableWritePlan rootTable
    )
    {
        foreach (var storedScope in profileAppliedContext.StoredScopeStates)
        {
            var scopeCanonical = storedScope.Address.JsonScope;
            if (
                !bindingsByContainingScope.TryGetValue(scopeCanonical, out var bindingsUnderScope)
                || bindingsUnderScope.Count == 0
            )
            {
                throw new InvalidOperationException(
                    $"Stored scope '{scopeCanonical}' on root table '{FormatTable(rootTable)}' "
                        + "does not resolve to any root-table binding. This indicates upstream "
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
                            + $"on root table '{FormatTable(rootTable)}' does not resolve to any "
                            + "root-table binding under that scope. This indicates upstream "
                            + "Core / write-plan contract drift."
                    );
                }
            }
        }
    }

    private static string ResolveBindingRootRelativePath(
        ResourceWritePlan writePlan,
        WriteColumnBinding binding,
        int bindingIndex,
        TableWritePlan rootTable
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
                $"Root-table binding at index {bindingIndex} on table '{FormatTable(rootTable)}' "
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
        TableWritePlan rootTable
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
                $"Root-table binding at index {bindingIndex} on table '{FormatTable(rootTable)}' "
                    + $"has a WriteValueSource kind '{binding.Source.GetType().Name}' that the classifier "
                    + "does not know how to resolve for governance. Storage-managed and plan-shape kinds must be filtered upstream."
            ),
        };

    private static string FormatTable(TableWritePlan rootTable) =>
        $"{rootTable.TableModel.Table.Schema.Value}.{rootTable.TableModel.Table.Name}";

    private readonly record struct GovernedBindingEntry(
        string MemberPath,
        string GoverningPath,
        ProfileMemberGovernanceRules.HiddenPathMatchKind MatchKind
    );
}
