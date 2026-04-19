// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Context carried into the post-overlay key-unification resolver. Assembled by
/// the profile merge synthesizer after it applies the classifier's per-binding
/// dispositions to merge the root row. The resolver recomputes canonical and
/// synthetic-presence values from merged request + stored state — visible
/// members are evaluated from <see cref="WritableRequestBody"/>, hidden-governed
/// members from <see cref="CurrentRootRowByColumnName"/>, and visible-absent
/// members are treated as absent.
/// </summary>
/// <remarks>
/// <see cref="ResolvedReferenceLookups"/> is supplied pre-built by the caller
/// (the profile merge synthesizer). It is the flattener's
/// <see cref="FlatteningResolvedReferenceLookupSet"/>, compiled once per
/// synthesis from the full <see cref="ResourceWritePlan"/> and
/// <see cref="ResolvedReferenceSet"/>, and reused across every key-unification
/// plan the resolver evaluates for the root table.
/// </remarks>
internal sealed record ProfileRootKeyUnificationContext(
    JsonNode WritableRequestBody,
    RelationalWriteCurrentState? CurrentState,
    IReadOnlyDictionary<DbColumnName, object?> CurrentRootRowByColumnName,
    FlatteningResolvedReferenceLookupSet ResolvedReferenceLookups,
    ProfileAppliedWriteRequest ProfileRequest,
    ProfileAppliedWriteContext? ProfileAppliedContext
);

/// <summary>
/// Post-overlay key-unification resolver. Run by the profile merge synthesizer
/// after it applies the classifier's per-binding dispositions to the merged
/// root row. For every <see cref="KeyUnificationWritePlan"/> on the root table,
/// the resolver evaluates each member via profile-aware routing
/// (visible-present → request body, hidden-governed → stored row,
/// visible-absent → absent), writes the synthetic-presence binding (when
/// resolver-owned), enforces first-present-wins canonical agreement, writes
/// the canonical binding, and then delegates to
/// <see cref="ProfileKeyUnificationGuardrails"/> for presence-gated-null and
/// canonical-non-nullable checks.
/// </summary>
/// <remarks>
/// Exception typing is preserved from the flattener:
/// canonical-disagreement raises <see cref="RelationalWriteRequestValidationException"/>
/// (request-shape failure); presence-gated-null, canonical-non-nullable,
/// missing hidden column, and resolver/classifier drift raise
/// <see cref="InvalidOperationException"/> (invariant violations).
/// </remarks>
internal interface IProfileRootKeyUnificationResolver
{
    void Resolve(
        TableWritePlan rootTableWritePlan,
        ProfileRootKeyUnificationContext context,
        FlattenedWriteValue[] mergedRowValuesMutable,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    );
}

internal sealed class ProfileRootKeyUnificationResolver : IProfileRootKeyUnificationResolver
{
    public void Resolve(
        TableWritePlan rootTableWritePlan,
        ProfileRootKeyUnificationContext context,
        FlattenedWriteValue[] mergedRowValuesMutable,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        ArgumentNullException.ThrowIfNull(rootTableWritePlan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mergedRowValuesMutable);
        ArgumentNullException.ThrowIfNull(resolverOwnedBindingIndices);

        if (mergedRowValuesMutable.Length != rootTableWritePlan.ColumnBindings.Length)
        {
            throw new InvalidOperationException(
                $"Merged row value buffer length {mergedRowValuesMutable.Length} does not match "
                    + $"root table '{FormatTable(rootTableWritePlan)}' binding count "
                    + $"{rootTableWritePlan.ColumnBindings.Length}."
            );
        }

        if (rootTableWritePlan.KeyUnificationPlans.Length == 0)
        {
            return;
        }

        var valueAssigned = new bool[mergedRowValuesMutable.Length];
        for (var i = 0; i < mergedRowValuesMutable.Length; i++)
        {
            valueAssigned[i] = mergedRowValuesMutable[i] is not null;
        }

        var candidateScopes = BuildCandidateScopeSet(context.ProfileRequest, context.ProfileAppliedContext);

        foreach (var keyUnificationPlan in rootTableWritePlan.KeyUnificationPlans)
        {
            ResolveOne(
                rootTableWritePlan,
                keyUnificationPlan,
                context,
                candidateScopes,
                context.ResolvedReferenceLookups,
                mergedRowValuesMutable,
                valueAssigned,
                resolverOwnedBindingIndices
            );
        }
    }

    private static void ResolveOne(
        TableWritePlan rootTableWritePlan,
        KeyUnificationWritePlan keyUnificationPlan,
        ProfileRootKeyUnificationContext context,
        ImmutableArray<string> candidateScopes,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        FlattenedWriteValue[] mergedRowValuesMutable,
        bool[] valueAssigned,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        object? canonicalValue = null;
        KeyUnificationMemberWritePlan? firstPresentMember = null;

        foreach (var member in keyUnificationPlan.MembersInOrder)
        {
            var visibility = ClassifyMemberVisibility(member, context, candidateScopes);
            var evaluation = EvaluateMember(
                rootTableWritePlan,
                member,
                visibility,
                context,
                resolvedReferenceLookups
            );

            if (member.PresenceIsSynthetic && member.PresenceBindingIndex is int presenceBindingIndex)
            {
                if (!resolverOwnedBindingIndices.Contains(presenceBindingIndex))
                {
                    throw new InvalidOperationException(
                        $"Resolver cannot write synthetic presence binding index {presenceBindingIndex} on table "
                            + $"'{FormatTable(rootTableWritePlan)}' because the classifier did not mark it resolver-owned."
                    );
                }

                mergedRowValuesMutable[presenceBindingIndex] = new FlattenedWriteValue.Literal(
                    evaluation.IsPresent ? true : null
                );
                valueAssigned[presenceBindingIndex] = true;
            }

            if (!evaluation.IsPresent)
            {
                continue;
            }

            if (firstPresentMember is null)
            {
                canonicalValue = evaluation.Value;
                firstPresentMember = member;
                continue;
            }

            if (!Equals(canonicalValue, evaluation.Value))
            {
                throw RelationalWriteFlattener.CreateRequestShapeValidationException(
                    RelationalJsonPathSupport.CombineRestrictedCanonical(
                        rootTableWritePlan.TableModel.JsonScope,
                        member.RelativePath
                    ),
                    $"Key-unification conflict for canonical column '{keyUnificationPlan.CanonicalColumn.Value}' "
                        + $"on table '{FormatTable(rootTableWritePlan)}': member '{firstPresentMember.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(canonicalValue)} but member '{member.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(evaluation.Value)}."
                );
            }
        }

        if (!resolverOwnedBindingIndices.Contains(keyUnificationPlan.CanonicalBindingIndex))
        {
            throw new InvalidOperationException(
                $"Resolver cannot write canonical binding index {keyUnificationPlan.CanonicalBindingIndex} on table "
                    + $"'{FormatTable(rootTableWritePlan)}' because the classifier did not mark it resolver-owned."
            );
        }

        mergedRowValuesMutable[keyUnificationPlan.CanonicalBindingIndex] = new FlattenedWriteValue.Literal(
            canonicalValue
        );
        valueAssigned[keyUnificationPlan.CanonicalBindingIndex] = true;

        ProfileKeyUnificationGuardrails.Validate(
            rootTableWritePlan,
            keyUnificationPlan,
            canonicalValue,
            mergedRowValuesMutable,
            valueAssigned
        );
    }

    private static MemberVisibility ClassifyMemberVisibility(
        KeyUnificationMemberWritePlan member,
        ProfileRootKeyUnificationContext context,
        ImmutableArray<string> candidateScopes
    )
    {
        var memberPath = member.RelativePath.Canonical;
        var containingScope = TryMatchLongestScope(memberPath, candidateScopes);

        if (containingScope is null)
        {
            // No profile governance touches this member; treat as fully visible.
            return MemberVisibility.VisiblePresent;
        }

        var storedScope = context.ProfileAppliedContext is not null
            ? ProfileMemberGovernanceRules.LookupStoredScope(context.ProfileAppliedContext, containingScope)
            : null;
        var matchKind = ProfileMemberGovernanceRules.MatchKindFor(member);
        var strippedMemberPath = StripScopePrefix(memberPath, containingScope);
        var governingPath = member switch
        {
            KeyUnificationMemberWritePlan.ReferenceDerivedMember refDerived => StripScopePrefix(
                refDerived.ReferenceSource.ReferenceObjectPath.Canonical,
                containingScope
            ),
            _ => strippedMemberPath,
        };

        if (storedScope is not null)
        {
            if (storedScope.Visibility == ProfileVisibilityKind.Hidden)
            {
                return MemberVisibility.HiddenGoverned;
            }
            if (
                ProfileMemberGovernanceRules.IsHiddenGoverned(
                    governingPath,
                    storedScope.HiddenMemberPaths,
                    matchKind
                )
            )
            {
                return MemberVisibility.HiddenGoverned;
            }
        }

        var requestScope = ProfileMemberGovernanceRules.LookupRequestScope(
            context.ProfileRequest,
            containingScope
        );
        if (requestScope is not null && requestScope.Visibility == ProfileVisibilityKind.VisibleAbsent)
        {
            return MemberVisibility.VisibleAbsent;
        }

        return MemberVisibility.VisiblePresent;
    }

    private static KeyUnificationMemberEvaluation EvaluateMember(
        TableWritePlan rootTableWritePlan,
        KeyUnificationMemberWritePlan member,
        MemberVisibility visibility,
        ProfileRootKeyUnificationContext context,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    ) =>
        visibility switch
        {
            MemberVisibility.VisibleAbsent => KeyUnificationMemberEvaluation.Absent,
            MemberVisibility.HiddenGoverned => EvaluateHiddenMember(rootTableWritePlan, member, context),
            MemberVisibility.VisiblePresent => FlattenerMemberEvaluation.EvaluateKeyUnificationMember(
                rootTableWritePlan,
                member,
                context.WritableRequestBody,
                resolvedReferenceLookups,
                ReadOnlySpan<int>.Empty
            ),
            _ => throw new InvalidOperationException(
                $"Unhandled member visibility '{visibility}' on table '{FormatTable(rootTableWritePlan)}'."
            ),
        };

    private static KeyUnificationMemberEvaluation EvaluateHiddenMember(
        TableWritePlan rootTableWritePlan,
        KeyUnificationMemberWritePlan member,
        ProfileRootKeyUnificationContext context
    )
    {
        if (!context.CurrentRootRowByColumnName.TryGetValue(member.MemberPathColumn, out var storedValue))
        {
            throw new InvalidOperationException(
                $"Hidden key-unification member '{member.MemberPathColumn.Value}' on table "
                    + $"'{FormatTable(rootTableWritePlan)}' requires stored column "
                    + $"'{member.MemberPathColumn.Value}' in the current-state row projection, "
                    + "but it was not present."
            );
        }

        bool isPresent;
        if (member.PresenceColumn is { } presenceColumn)
        {
            if (!context.CurrentRootRowByColumnName.TryGetValue(presenceColumn, out var presenceValue))
            {
                throw new InvalidOperationException(
                    $"Hidden key-unification member '{member.MemberPathColumn.Value}' on table "
                        + $"'{FormatTable(rootTableWritePlan)}' requires presence column "
                        + $"'{presenceColumn.Value}' in the current-state row projection, "
                        + "but it was not present."
                );
            }

            // Presence is non-nullness of the stored presence column; presence columns are
            // synthetic nullable bit/bool, never interpreted as boolean truthiness.
            isPresent = presenceValue is not null;
        }
        else
        {
            isPresent = storedValue is not null;
        }

        return isPresent
            ? KeyUnificationMemberEvaluation.Present(storedValue!)
            : KeyUnificationMemberEvaluation.Absent;
    }

    private static ImmutableArray<string> BuildCandidateScopeSet(
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    )
    {
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

    private static string? TryMatchLongestScope(string bindingPath, ImmutableArray<string> candidateScopes)
    {
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
            return string.Empty;
        }
        return bindingPath[(scope.Length + 1)..];
    }

    private static string FormatTable(TableWritePlan tableWritePlan) =>
        $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";

    private static string FormatLiteral(object? value) =>
        value switch
        {
            null => "null",
            string stringValue => $"'{stringValue}'",
            bool boolValue => boolValue ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? "<unknown>",
        };

    private enum MemberVisibility
    {
        VisiblePresent,
        HiddenGoverned,
        VisibleAbsent,
    }
}
