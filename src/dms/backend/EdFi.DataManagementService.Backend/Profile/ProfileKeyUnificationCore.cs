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
/// Scope-family-agnostic core for the post-overlay key-unification resolver. Operates on
/// a single <see cref="TableWritePlan"/> (root-table or separate-table) plus the shared
/// per-scope profile inputs and row-level state. For every
/// <see cref="KeyUnificationWritePlan"/> on the table, evaluates each member via
/// profile-aware routing (visible-present → request body, hidden-governed → stored row,
/// visible-absent → absent), writes the synthetic-presence binding (when resolver-owned),
/// enforces first-present-wins canonical agreement, writes the canonical binding, and
/// delegates to <see cref="ProfileKeyUnificationGuardrails"/> for presence-gated-null and
/// canonical-non-nullable checks. Extracted from <see cref="ProfileRootKeyUnificationResolver"/>
/// without behavior change so Slice 3's separate-table resolver can reuse the same logic.
/// </summary>
/// <remarks>
/// Exception typing is preserved from the flattener:
/// canonical-disagreement raises <see cref="RelationalWriteRequestValidationException"/>
/// (request-shape failure); presence-gated-null, canonical-non-nullable,
/// missing hidden column, and resolver/classifier drift raise
/// <see cref="InvalidOperationException"/> (invariant violations).
/// </remarks>
internal static class ProfileKeyUnificationCore
{
    /// <summary>
    /// Resolves every <see cref="KeyUnificationWritePlan"/> on the supplied
    /// <paramref name="tableWritePlan"/>, mutating <paramref name="mergedRowValuesMutable"/>
    /// in place. Callers must pass a buffer whose length equals the table's
    /// <see cref="TableWritePlan.ColumnBindings"/> count; violation raises
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static void ResolveKeyUnification(
        TableWritePlan tableWritePlan,
        IReadOnlyDictionary<DbColumnName, object?> currentRowByColumnName,
        JsonNode writableRequestBody,
        RelationalWriteCurrentState? currentState,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext,
        FlattenedWriteValue[] mergedRowValuesMutable,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(currentRowByColumnName);
        ArgumentNullException.ThrowIfNull(writableRequestBody);
        ArgumentNullException.ThrowIfNull(resolvedReferenceLookups);
        ArgumentNullException.ThrowIfNull(profileRequest);
        ArgumentNullException.ThrowIfNull(mergedRowValuesMutable);
        ArgumentNullException.ThrowIfNull(resolverOwnedBindingIndices);

        if (tableWritePlan.KeyUnificationPlans.Length == 0)
        {
            return;
        }

        var valueAssigned = new bool[mergedRowValuesMutable.Length];
        for (var i = 0; i < mergedRowValuesMutable.Length; i++)
        {
            valueAssigned[i] = mergedRowValuesMutable[i] is not null;
        }

        var candidateScopes = ProfileBindingClassificationCore.BuildCandidateScopeSet(
            profileRequest,
            profileAppliedContext
        );

        // currentState is accepted on the public surface for parity with the original
        // ProfileRootKeyUnificationContext; ResolveOne does not consume it because per-member
        // evaluation routes through currentRowByColumnName for hidden governance and the
        // request body for visible members.
        _ = currentState;

        foreach (var keyUnificationPlan in tableWritePlan.KeyUnificationPlans)
        {
            ResolveOne(
                tableWritePlan,
                keyUnificationPlan,
                currentRowByColumnName,
                writableRequestBody,
                resolvedReferenceLookups,
                profileRequest,
                profileAppliedContext,
                candidateScopes,
                mergedRowValuesMutable,
                valueAssigned,
                resolverOwnedBindingIndices
            );
        }
    }

    private static void ResolveOne(
        TableWritePlan tableWritePlan,
        KeyUnificationWritePlan keyUnificationPlan,
        IReadOnlyDictionary<DbColumnName, object?> currentRowByColumnName,
        JsonNode writableRequestBody,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext,
        ImmutableArray<string> candidateScopes,
        FlattenedWriteValue[] mergedRowValuesMutable,
        bool[] valueAssigned,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        object? canonicalValue = null;
        KeyUnificationMemberWritePlan? firstPresentMember = null;

        foreach (var member in keyUnificationPlan.MembersInOrder)
        {
            var visibility = ClassifyMemberVisibility(
                member,
                profileRequest,
                profileAppliedContext,
                candidateScopes
            );
            var evaluation = EvaluateMember(
                tableWritePlan,
                member,
                visibility,
                currentRowByColumnName,
                writableRequestBody,
                resolvedReferenceLookups
            );

            if (member.PresenceIsSynthetic && member.PresenceBindingIndex is int presenceBindingIndex)
            {
                if (!resolverOwnedBindingIndices.Contains(presenceBindingIndex))
                {
                    throw new InvalidOperationException(
                        $"Resolver cannot write synthetic presence binding index {presenceBindingIndex} on table "
                            + $"'{ProfileBindingClassificationCore.FormatTable(tableWritePlan)}' because the classifier did not mark it resolver-owned."
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
                        tableWritePlan.TableModel.JsonScope,
                        member.RelativePath
                    ),
                    $"Key-unification conflict for canonical column '{keyUnificationPlan.CanonicalColumn.Value}' "
                        + $"on table '{ProfileBindingClassificationCore.FormatTable(tableWritePlan)}': member '{firstPresentMember.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(canonicalValue)} but member '{member.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(evaluation.Value)}."
                );
            }
        }

        if (!resolverOwnedBindingIndices.Contains(keyUnificationPlan.CanonicalBindingIndex))
        {
            throw new InvalidOperationException(
                $"Resolver cannot write canonical binding index {keyUnificationPlan.CanonicalBindingIndex} on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tableWritePlan)}' because the classifier did not mark it resolver-owned."
            );
        }

        mergedRowValuesMutable[keyUnificationPlan.CanonicalBindingIndex] = new FlattenedWriteValue.Literal(
            canonicalValue
        );
        valueAssigned[keyUnificationPlan.CanonicalBindingIndex] = true;

        ProfileKeyUnificationGuardrails.Validate(
            tableWritePlan,
            keyUnificationPlan,
            canonicalValue,
            mergedRowValuesMutable,
            valueAssigned
        );
    }

    private static MemberVisibility ClassifyMemberVisibility(
        KeyUnificationMemberWritePlan member,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext,
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

        var storedScope = profileAppliedContext is not null
            ? ProfileMemberGovernanceRules.LookupStoredScope(profileAppliedContext, containingScope)
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

        var requestScope = ProfileMemberGovernanceRules.LookupRequestScope(profileRequest, containingScope);
        if (requestScope is not null && requestScope.Visibility == ProfileVisibilityKind.VisibleAbsent)
        {
            return MemberVisibility.VisibleAbsent;
        }

        return MemberVisibility.VisiblePresent;
    }

    private static KeyUnificationMemberEvaluation EvaluateMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan member,
        MemberVisibility visibility,
        IReadOnlyDictionary<DbColumnName, object?> currentRowByColumnName,
        JsonNode writableRequestBody,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    ) =>
        visibility switch
        {
            MemberVisibility.VisibleAbsent => KeyUnificationMemberEvaluation.Absent,
            MemberVisibility.HiddenGoverned => EvaluateHiddenMember(
                tableWritePlan,
                member,
                currentRowByColumnName
            ),
            MemberVisibility.VisiblePresent => FlattenerMemberEvaluation.EvaluateKeyUnificationMember(
                tableWritePlan,
                member,
                writableRequestBody,
                resolvedReferenceLookups,
                ReadOnlySpan<int>.Empty
            ),
            _ => throw new InvalidOperationException(
                $"Unhandled member visibility '{visibility}' on table '{ProfileBindingClassificationCore.FormatTable(tableWritePlan)}'."
            ),
        };

    private static KeyUnificationMemberEvaluation EvaluateHiddenMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan member,
        IReadOnlyDictionary<DbColumnName, object?> currentRowByColumnName
    )
    {
        if (!currentRowByColumnName.TryGetValue(member.MemberPathColumn, out var storedValue))
        {
            throw new InvalidOperationException(
                $"Hidden key-unification member '{member.MemberPathColumn.Value}' on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tableWritePlan)}' requires stored column "
                    + $"'{member.MemberPathColumn.Value}' in the current-state row projection, "
                    + "but it was not present."
            );
        }

        bool isPresent;
        if (member.PresenceColumn is { } presenceColumn)
        {
            if (!currentRowByColumnName.TryGetValue(presenceColumn, out var presenceValue))
            {
                throw new InvalidOperationException(
                    $"Hidden key-unification member '{member.MemberPathColumn.Value}' on table "
                        + $"'{ProfileBindingClassificationCore.FormatTable(tableWritePlan)}' requires presence column "
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
