// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalWriteFlattener
{
    FlattenedWriteSet Flatten(FlatteningInput flatteningInput);
}

internal sealed class RelationalWriteFlattener : IRelationalWriteFlattener
{
    public FlattenedWriteSet Flatten(FlatteningInput flatteningInput)
    {
        ArgumentNullException.ThrowIfNull(flatteningInput);

        var writePlan = flatteningInput.WritePlan;
        var rootTablePlan = GetRootTablePlan(writePlan);
        var selectedBodyIndex = SelectedBodyIndex.Create(flatteningInput.SelectedBody);
        var resolvedReferenceLookups = FlatteningResolvedReferenceLookupSet.Create(
            writePlan,
            flatteningInput.ResolvedReferences
        );
        var rootDocumentIdValue = ResolveRootDocumentIdValue(flatteningInput.TargetContext);
        var collectionChildPlansByParentScope = BuildCollectionChildPlansByParentScope(writePlan);

        var rootRow = new RootWriteRowBuffer(
            tableWritePlan: rootTablePlan,
            values: MaterializeValues(
                flatteningInput,
                rootTablePlan,
                selectedBodyIndex,
                resolvedReferenceLookups,
                parentKeyParts: [],
                ordinalPath: []
            ),
            nonCollectionRows: MaterializeRootExtensionRows(
                flatteningInput,
                selectedBodyIndex,
                resolvedReferenceLookups,
                rootDocumentIdValue
            ),
            collectionCandidates: MaterializeCollectionCandidates(
                flatteningInput,
                collectionChildPlansByParentScope,
                rootTablePlan.TableModel.JsonScope.Canonical,
                flatteningInput.SelectedBody,
                parentKeyParts: [],
                parentOrdinalPath: [],
                resolvedReferenceLookups
            )
        );

        return new FlattenedWriteSet(rootRow);
    }

    private static TableWritePlan GetRootTablePlan(ResourceWritePlan writePlan)
    {
        ArgumentNullException.ThrowIfNull(writePlan);

        var rootPlans = writePlan
            .TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind == DbTableKind.Root
            )
            .Take(2)
            .ToArray();

        return rootPlans.Length switch
        {
            1 => rootPlans[0],
            0 => throw new InvalidOperationException(
                $"Write plan for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}' does not contain a root table plan."
            ),
            _ => throw new InvalidOperationException(
                $"Write plan for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}' contains multiple root table plans."
            ),
        };
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyList<CollectionChildPlan>
    > BuildCollectionChildPlansByParentScope(ResourceWritePlan writePlan)
    {
        ArgumentNullException.ThrowIfNull(writePlan);

        Dictionary<string, List<CollectionChildPlan>> childPlansByParentScope = new(StringComparer.Ordinal);

        foreach (
            var tableWritePlan in writePlan.TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind == DbTableKind.Collection
            )
        )
        {
            var scopeSegments = RestrictedJsonPath.GetSegments(tableWritePlan.TableModel.JsonScope).ToArray();

            if (
                scopeSegments.Length < 2
                || scopeSegments[^2] is not JsonPathSegment.Property
                || scopeSegments[^1] is not JsonPathSegment.AnyArrayElement
            )
            {
                throw new InvalidOperationException(
                    $"Collection table '{FormatTable(tableWritePlan)}' does not have a canonical collection scope."
                );
            }

            var parentScopeSegments = scopeSegments[..^2];
            var parentScopeCanonical = RestrictedJsonPath.BuildCanonical(parentScopeSegments);
            var relativeScopeSegments = scopeSegments[parentScopeSegments.Length..];

            if (!childPlansByParentScope.TryGetValue(parentScopeCanonical, out var childPlans))
            {
                childPlans = [];
                childPlansByParentScope.Add(parentScopeCanonical, childPlans);
            }

            childPlans.Add(new CollectionChildPlan(tableWritePlan, [.. relativeScopeSegments]));
        }

        return childPlansByParentScope.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<CollectionChildPlan>)entry.Value,
            StringComparer.Ordinal
        );
    }

    private static IEnumerable<StandaloneScopeWriteRowBuffer> MaterializeRootExtensionRows(
        FlatteningInput flatteningInput,
        SelectedBodyIndex selectedBodyIndex,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        FlattenedWriteValue rootDocumentIdValue
    )
    {
        var rootParentKeyParts = new[] { rootDocumentIdValue };

        foreach (
            var tableWritePlan in flatteningInput.WritePlan.TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind == DbTableKind.RootExtension
            )
        )
        {
            if (!selectedBodyIndex.HasBoundDataForScope(tableWritePlan.TableModel.JsonScope.Canonical))
            {
                continue;
            }

            yield return new StandaloneScopeWriteRowBuffer(
                tableWritePlan,
                MaterializeValues(
                    flatteningInput,
                    tableWritePlan,
                    selectedBodyIndex,
                    resolvedReferenceLookups,
                    rootParentKeyParts,
                    ordinalPath: []
                ),
                nonCollectionRows: [],
                collectionCandidates: []
            );
        }
    }

    private static IReadOnlyList<CollectionWriteCandidate> MaterializeCollectionCandidates(
        FlatteningInput flatteningInput,
        IReadOnlyDictionary<string, IReadOnlyList<CollectionChildPlan>> collectionChildPlansByParentScope,
        string parentScopeCanonical,
        JsonNode parentScopeNode,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts,
        ReadOnlySpan<int> parentOrdinalPath,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        if (
            !collectionChildPlansByParentScope.TryGetValue(parentScopeCanonical, out var childPlans)
            || childPlans.Count == 0
        )
        {
            return [];
        }

        List<CollectionWriteCandidate> collectionCandidates = [];

        foreach (var childPlan in childPlans)
        {
            Dictionary<object?[], int[]> firstOrdinalPathBySemanticIdentity = new(
                SemanticIdentityValueArrayComparer.Instance
            );

            foreach (
                var collectionScopeInstance in EnumerateCollectionScopeInstances(parentScopeNode, childPlan)
            )
            {
                var ordinalPath = AppendOrdinalPath(parentOrdinalPath, collectionScopeInstance.RequestOrder);
                var values = MaterializeScopeNodeValues(
                    flatteningInput,
                    childPlan.TableWritePlan,
                    collectionScopeInstance.ScopeNode,
                    resolvedReferenceLookups,
                    parentKeyParts,
                    collectionScopeInstance.RequestOrder,
                    ordinalPath
                );
                var semanticIdentityValues = MaterializeSemanticIdentityValues(
                    childPlan.TableWritePlan,
                    values
                );

                if (
                    firstOrdinalPathBySemanticIdentity.TryGetValue(
                        semanticIdentityValues,
                        out var firstOrdinalPath
                    )
                )
                {
                    throw CreateDuplicateSemanticIdentityException(
                        childPlan.TableWritePlan,
                        parentScopeCanonical,
                        firstOrdinalPath,
                        ordinalPath,
                        semanticIdentityValues
                    );
                }

                firstOrdinalPathBySemanticIdentity.Add(semanticIdentityValues, ordinalPath);

                var childParentKeyParts = GetPhysicalRowIdentityValues(childPlan.TableWritePlan, values);
                var nestedCollectionCandidates = MaterializeCollectionCandidates(
                    flatteningInput,
                    collectionChildPlansByParentScope,
                    childPlan.TableWritePlan.TableModel.JsonScope.Canonical,
                    collectionScopeInstance.ScopeNode,
                    childParentKeyParts,
                    ordinalPath,
                    resolvedReferenceLookups
                );

                collectionCandidates.Add(
                    new CollectionWriteCandidate(
                        childPlan.TableWritePlan,
                        ordinalPath,
                        collectionScopeInstance.RequestOrder,
                        values,
                        semanticIdentityValues,
                        collectionCandidates: nestedCollectionCandidates
                    )
                );
            }
        }

        return collectionCandidates;
    }

    private static IReadOnlyList<FlattenedWriteValue> MaterializeValues(
        FlatteningInput flatteningInput,
        TableWritePlan tableWritePlan,
        SelectedBodyIndex selectedBodyIndex,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts,
        ReadOnlySpan<int> ordinalPath
    )
    {
        FlattenedWriteValue[] values = new FlattenedWriteValue[tableWritePlan.ColumnBindings.Length];
        bool[] valueAssigned = new bool[tableWritePlan.ColumnBindings.Length];

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            if (tableWritePlan.ColumnBindings[bindingIndex].Source is WriteValueSource.Precomputed)
            {
                continue;
            }

            values[bindingIndex] = MaterializeValue(
                flatteningInput,
                tableWritePlan,
                tableWritePlan.ColumnBindings[bindingIndex],
                selectedBodyIndex,
                resolvedReferenceLookups,
                parentKeyParts,
                ordinalPath
            );
            valueAssigned[bindingIndex] = true;
        }

        ApplyKeyUnificationValues(
            tableWritePlan,
            selectedBodyIndex,
            resolvedReferenceLookups,
            ordinalPath,
            values,
            valueAssigned
        );
        EnsureAllBindingsAssigned(tableWritePlan, values, valueAssigned);

        return values;
    }

    private static IReadOnlyList<FlattenedWriteValue> MaterializeScopeNodeValues(
        FlatteningInput flatteningInput,
        TableWritePlan tableWritePlan,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts,
        int ordinal,
        ReadOnlySpan<int> ordinalPath
    )
    {
        FlattenedWriteValue[] values = new FlattenedWriteValue[tableWritePlan.ColumnBindings.Length];
        bool[] valueAssigned = new bool[tableWritePlan.ColumnBindings.Length];

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            if (tableWritePlan.ColumnBindings[bindingIndex].Source is WriteValueSource.Precomputed)
            {
                continue;
            }

            values[bindingIndex] = MaterializeScopeNodeValue(
                flatteningInput,
                tableWritePlan,
                tableWritePlan.ColumnBindings[bindingIndex],
                scopeNode,
                resolvedReferenceLookups,
                parentKeyParts,
                ordinal,
                ordinalPath
            );
            valueAssigned[bindingIndex] = true;
        }

        ApplyKeyUnificationValues(
            tableWritePlan,
            scopeNode,
            resolvedReferenceLookups,
            ordinalPath,
            values,
            valueAssigned
        );
        ApplyCollectionKeyPreallocationValue(tableWritePlan, values, valueAssigned);
        EnsureAllBindingsAssigned(tableWritePlan, values, valueAssigned);

        return values;
    }

    private static void ApplyKeyUnificationValues(
        TableWritePlan tableWritePlan,
        SelectedBodyIndex selectedBodyIndex,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath,
        FlattenedWriteValue[] values,
        bool[] valueAssigned
    )
    {
        foreach (var keyUnificationPlan in tableWritePlan.KeyUnificationPlans)
        {
            var canonicalValue = EvaluateCanonicalValue(
                tableWritePlan,
                keyUnificationPlan,
                selectedBodyIndex,
                resolvedReferenceLookups,
                ordinalPath,
                values,
                valueAssigned
            );

            values[keyUnificationPlan.CanonicalBindingIndex] = new FlattenedWriteValue.Literal(
                canonicalValue
            );
            valueAssigned[keyUnificationPlan.CanonicalBindingIndex] = true;

            ValidateKeyUnificationGuardrails(
                tableWritePlan,
                keyUnificationPlan,
                canonicalValue,
                values,
                valueAssigned
            );
        }
    }

    private static void ApplyKeyUnificationValues(
        TableWritePlan tableWritePlan,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath,
        FlattenedWriteValue[] values,
        bool[] valueAssigned
    )
    {
        foreach (var keyUnificationPlan in tableWritePlan.KeyUnificationPlans)
        {
            var canonicalValue = EvaluateCanonicalValue(
                tableWritePlan,
                keyUnificationPlan,
                scopeNode,
                resolvedReferenceLookups,
                ordinalPath,
                values,
                valueAssigned
            );

            values[keyUnificationPlan.CanonicalBindingIndex] = new FlattenedWriteValue.Literal(
                canonicalValue
            );
            valueAssigned[keyUnificationPlan.CanonicalBindingIndex] = true;

            ValidateKeyUnificationGuardrails(
                tableWritePlan,
                keyUnificationPlan,
                canonicalValue,
                values,
                valueAssigned
            );
        }
    }

    private static object? EvaluateCanonicalValue(
        TableWritePlan tableWritePlan,
        KeyUnificationWritePlan keyUnificationPlan,
        SelectedBodyIndex selectedBodyIndex,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath,
        FlattenedWriteValue[] values,
        bool[] valueAssigned
    )
    {
        object? canonicalValue = null;
        KeyUnificationMemberWritePlan? firstPresentMember = null;

        foreach (var member in keyUnificationPlan.MembersInOrder)
        {
            var evaluation = EvaluateKeyUnificationMember(
                tableWritePlan,
                member,
                selectedBodyIndex,
                resolvedReferenceLookups,
                ordinalPath
            );

            if (member.PresenceIsSynthetic && member.PresenceBindingIndex is int presenceBindingIndex)
            {
                values[presenceBindingIndex] = new FlattenedWriteValue.Literal(
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
                throw new InvalidOperationException(
                    $"Key-unification conflict for canonical column '{keyUnificationPlan.CanonicalColumn.Value}' "
                        + $"on table '{FormatTable(tableWritePlan)}': member '{firstPresentMember.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(canonicalValue)} but member '{member.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(evaluation.Value)}."
                );
            }
        }

        return canonicalValue;
    }

    private static object? EvaluateCanonicalValue(
        TableWritePlan tableWritePlan,
        KeyUnificationWritePlan keyUnificationPlan,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath,
        FlattenedWriteValue[] values,
        bool[] valueAssigned
    )
    {
        object? canonicalValue = null;
        KeyUnificationMemberWritePlan? firstPresentMember = null;

        foreach (var member in keyUnificationPlan.MembersInOrder)
        {
            var evaluation = EvaluateKeyUnificationMember(
                tableWritePlan,
                member,
                scopeNode,
                resolvedReferenceLookups,
                ordinalPath
            );

            if (member.PresenceIsSynthetic && member.PresenceBindingIndex is int presenceBindingIndex)
            {
                values[presenceBindingIndex] = new FlattenedWriteValue.Literal(
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
                throw new InvalidOperationException(
                    $"Key-unification conflict for canonical column '{keyUnificationPlan.CanonicalColumn.Value}' "
                        + $"on table '{FormatTable(tableWritePlan)}': member '{firstPresentMember.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(canonicalValue)} but member '{member.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(evaluation.Value)}."
                );
            }
        }

        return canonicalValue;
    }

    private static KeyUnificationMemberEvaluation EvaluateKeyUnificationMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan member,
        SelectedBodyIndex selectedBodyIndex,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        var absolutePath = RestrictedJsonPath.CombineCanonical(
            tableWritePlan.TableModel.JsonScope,
            member.RelativePath
        );

        if (!selectedBodyIndex.TryGetLeafNode(absolutePath, out var memberNode) || memberNode is null)
        {
            return KeyUnificationMemberEvaluation.Absent;
        }

        return member switch
        {
            KeyUnificationMemberWritePlan.ScalarMember scalarMember => EvaluateScalarKeyUnificationMember(
                tableWritePlan,
                scalarMember,
                absolutePath,
                memberNode
            ),
            KeyUnificationMemberWritePlan.DescriptorMember descriptorMember =>
                EvaluateDescriptorKeyUnificationMember(
                    tableWritePlan,
                    descriptorMember,
                    absolutePath,
                    memberNode,
                    resolvedReferenceLookups,
                    ordinalPath
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported key-unification member kind '{member.GetType().Name}' on table '{FormatTable(tableWritePlan)}'."
            ),
        };
    }

    private static KeyUnificationMemberEvaluation EvaluateKeyUnificationMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan member,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        var absolutePath = RestrictedJsonPath.CombineCanonical(
            tableWritePlan.TableModel.JsonScope,
            member.RelativePath
        );

        if (!TryGetRelativeLeafNode(scopeNode, member.RelativePath, out var memberNode) || memberNode is null)
        {
            return KeyUnificationMemberEvaluation.Absent;
        }

        return member switch
        {
            KeyUnificationMemberWritePlan.ScalarMember scalarMember => EvaluateScalarKeyUnificationMember(
                tableWritePlan,
                scalarMember,
                absolutePath,
                memberNode
            ),
            KeyUnificationMemberWritePlan.DescriptorMember descriptorMember =>
                EvaluateDescriptorKeyUnificationMember(
                    tableWritePlan,
                    descriptorMember,
                    absolutePath,
                    memberNode,
                    resolvedReferenceLookups,
                    ordinalPath
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported key-unification member kind '{member.GetType().Name}' on table '{FormatTable(tableWritePlan)}'."
            ),
        };
    }

    private static KeyUnificationMemberEvaluation EvaluateScalarKeyUnificationMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan.ScalarMember member,
        string absolutePath,
        JsonNode memberNode
    )
    {
        if (memberNode is not JsonValue jsonValue)
        {
            throw CreateInvalidKeyUnificationScalarReadException(
                tableWritePlan,
                member,
                absolutePath,
                $"encountered non-scalar JSON node type '{memberNode.GetType().Name}'"
            );
        }

        return KeyUnificationMemberEvaluation.Present(
            ConvertScalarValue(
                jsonValue,
                member.ScalarType,
                tableWritePlan,
                member.MemberPathColumn,
                absolutePath
            )
        );
    }

    private static KeyUnificationMemberEvaluation EvaluateDescriptorKeyUnificationMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan.DescriptorMember member,
        string absolutePath,
        JsonNode memberNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        if (memberNode is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out _))
        {
            throw new InvalidOperationException(
                $"Key-unification member '{member.MemberPathColumn.Value}' on table '{FormatTable(tableWritePlan)}' "
                    + $"expected a descriptor URI string at path '{absolutePath}', but encountered "
                    + $"JSON value kind '{GetJsonValueKind(memberNode)}'."
            );
        }

        var descriptorId = resolvedReferenceLookups.GetDescriptorId(
            member.DescriptorResource,
            absolutePath,
            ordinalPath
        );

        if (descriptorId is null)
        {
            throw new InvalidOperationException(
                $"Key-unification member '{member.MemberPathColumn.Value}' on table '{FormatTable(tableWritePlan)}' "
                    + $"did not have a resolved descriptor id for path '{absolutePath}' and descriptor resource "
                    + $"'{RelationalWriteSupport.FormatResource(member.DescriptorResource)}'."
            );
        }

        return KeyUnificationMemberEvaluation.Present(descriptorId.Value);
    }

    private static void ValidateKeyUnificationGuardrails(
        TableWritePlan tableWritePlan,
        KeyUnificationWritePlan keyUnificationPlan,
        object? canonicalValue,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<bool> valueAssigned
    )
    {
        foreach (var member in keyUnificationPlan.MembersInOrder)
        {
            if (member.PresenceBindingIndex is not int presenceBindingIndex)
            {
                continue;
            }

            if (!valueAssigned[presenceBindingIndex])
            {
                throw new InvalidOperationException(
                    $"Presence binding for key-unification member '{member.MemberPathColumn.Value}' on table "
                        + $"'{FormatTable(tableWritePlan)}' was not assigned before guardrail validation."
                );
            }

            if (values[presenceBindingIndex] is not FlattenedWriteValue.Literal presenceValue)
            {
                throw new InvalidOperationException(
                    $"Presence binding for key-unification member '{member.MemberPathColumn.Value}' on table "
                        + $"'{FormatTable(tableWritePlan)}' was not materialized as a literal value."
                );
            }

            if (presenceValue.Value is not null && canonicalValue is null)
            {
                throw new InvalidOperationException(
                    $"Key-unification canonical column '{keyUnificationPlan.CanonicalColumn.Value}' on table "
                        + $"'{FormatTable(tableWritePlan)}' resolved to null while presence column "
                        + $"'{member.PresenceColumn!.Value}' indicated member '{member.MemberPathColumn.Value}' was present."
                );
            }
        }

        var canonicalBinding = tableWritePlan.ColumnBindings[keyUnificationPlan.CanonicalBindingIndex];

        if (!canonicalBinding.Column.IsNullable && canonicalValue is null)
        {
            throw new InvalidOperationException(
                $"Key-unification canonical column '{canonicalBinding.Column.ColumnName.Value}' on table "
                    + $"'{FormatTable(tableWritePlan)}' is not nullable but resolved to null."
            );
        }
    }

    private static void EnsureAllBindingsAssigned(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<bool> valueAssigned
    )
    {
        List<string> unassignedColumns = [];

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            if (!valueAssigned[bindingIndex] || values[bindingIndex] is null)
            {
                unassignedColumns.Add(tableWritePlan.ColumnBindings[bindingIndex].Column.ColumnName.Value);
            }
        }

        if (unassignedColumns.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Table '{FormatTable(tableWritePlan)}' left write bindings unassigned during flattening: {string.Join(", ", unassignedColumns)}."
        );
    }

    private static FlattenedWriteValue MaterializeValue(
        FlatteningInput flatteningInput,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        SelectedBodyIndex selectedBodyIndex,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts,
        ReadOnlySpan<int> ordinalPath
    )
    {
        return columnBinding.Source switch
        {
            WriteValueSource.DocumentId => ResolveRootDocumentIdValue(flatteningInput.TargetContext),
            WriteValueSource.ParentKeyPart parentKeyPart => ResolveParentKeyPart(
                tableWritePlan,
                columnBinding,
                parentKeyPart,
                parentKeyParts
            ),
            WriteValueSource.Scalar scalar => ResolveScalarValue(
                tableWritePlan,
                columnBinding,
                scalar,
                selectedBodyIndex
            ),
            WriteValueSource.DocumentReference documentReference => new FlattenedWriteValue.Literal(
                resolvedReferenceLookups.GetDocumentId(documentReference.BindingIndex, ordinalPath)
            ),
            WriteValueSource.DescriptorReference descriptorReference => new FlattenedWriteValue.Literal(
                resolvedReferenceLookups.GetDescriptorId(tableWritePlan, descriptorReference, ordinalPath)
            ),
            WriteValueSource.Ordinal => throw CreateUnsupportedValueSourceException(
                tableWritePlan,
                columnBinding,
                "collection ordinals"
            ),
            WriteValueSource.Precomputed => throw new InvalidOperationException(
                $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' "
                    + "was routed through non-precomputed materialization."
            ),
            _ => throw new InvalidOperationException(
                $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' uses unsupported write source '{columnBinding.Source.GetType().Name}'."
            ),
        };
    }

    private static FlattenedWriteValue MaterializeScopeNodeValue(
        FlatteningInput flatteningInput,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts,
        int ordinal,
        ReadOnlySpan<int> ordinalPath
    )
    {
        return columnBinding.Source switch
        {
            WriteValueSource.DocumentId => ResolveRootDocumentIdValue(flatteningInput.TargetContext),
            WriteValueSource.ParentKeyPart parentKeyPart => ResolveParentKeyPart(
                tableWritePlan,
                columnBinding,
                parentKeyPart,
                parentKeyParts
            ),
            WriteValueSource.Scalar scalar => ResolveScopeNodeScalarValue(
                tableWritePlan,
                columnBinding,
                scalar,
                scopeNode
            ),
            WriteValueSource.DocumentReference documentReference => new FlattenedWriteValue.Literal(
                resolvedReferenceLookups.GetDocumentId(documentReference.BindingIndex, ordinalPath)
            ),
            WriteValueSource.DescriptorReference descriptorReference => new FlattenedWriteValue.Literal(
                resolvedReferenceLookups.GetDescriptorId(tableWritePlan, descriptorReference, ordinalPath)
            ),
            WriteValueSource.Ordinal => new FlattenedWriteValue.Literal(ordinal),
            WriteValueSource.Precomputed => throw new InvalidOperationException(
                $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' "
                    + "was routed through non-precomputed materialization."
            ),
            _ => throw new InvalidOperationException(
                $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' uses unsupported write source '{columnBinding.Source.GetType().Name}'."
            ),
        };
    }

    private static FlattenedWriteValue ResolveParentKeyPart(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.ParentKeyPart parentKeyPart,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts
    )
    {
        if (parentKeyPart.Index >= 0 && parentKeyPart.Index < parentKeyParts.Count)
        {
            return parentKeyParts[parentKeyPart.Index];
        }

        throw new InvalidOperationException(
            $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' requested parent key part index {parentKeyPart.Index}, but only {parentKeyParts.Count} parent key part values were available."
        );
    }

    private static FlattenedWriteValue ResolveScalarValue(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.Scalar scalar,
        SelectedBodyIndex selectedBodyIndex
    )
    {
        var absolutePath = RestrictedJsonPath.CombineCanonical(
            tableWritePlan.TableModel.JsonScope,
            scalar.RelativePath
        );

        if (!selectedBodyIndex.TryGetLeafNode(absolutePath, out var scalarNode))
        {
            return new FlattenedWriteValue.Literal(null);
        }

        if (scalarNode is null)
        {
            return new FlattenedWriteValue.Literal(null);
        }

        if (scalarNode is not JsonValue jsonValue)
        {
            throw CreateInvalidScalarReadException(
                tableWritePlan,
                columnBinding,
                absolutePath,
                scalar.Type,
                $"encountered non-scalar JSON node type '{scalarNode.GetType().Name}'"
            );
        }

        return new FlattenedWriteValue.Literal(
            ConvertScalarValue(jsonValue, scalar.Type, tableWritePlan, columnBinding, absolutePath)
        );
    }

    private static FlattenedWriteValue ResolveScopeNodeScalarValue(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.Scalar scalar,
        JsonNode scopeNode
    )
    {
        var absolutePath = RestrictedJsonPath.CombineCanonical(
            tableWritePlan.TableModel.JsonScope,
            scalar.RelativePath
        );

        if (!TryGetRelativeLeafNode(scopeNode, scalar.RelativePath, out var scalarNode))
        {
            return new FlattenedWriteValue.Literal(null);
        }

        if (scalarNode is null)
        {
            return new FlattenedWriteValue.Literal(null);
        }

        if (scalarNode is not JsonValue jsonValue)
        {
            throw CreateInvalidScalarReadException(
                tableWritePlan,
                columnBinding,
                absolutePath,
                scalar.Type,
                $"encountered non-scalar JSON node type '{scalarNode.GetType().Name}'"
            );
        }

        return new FlattenedWriteValue.Literal(
            ConvertScalarValue(jsonValue, scalar.Type, tableWritePlan, columnBinding, absolutePath)
        );
    }

    private static void ApplyCollectionKeyPreallocationValue(
        TableWritePlan tableWritePlan,
        FlattenedWriteValue[] values,
        bool[] valueAssigned
    )
    {
        if (tableWritePlan.CollectionKeyPreallocationPlan is not { } collectionKeyPreallocationPlan)
        {
            return;
        }

        values[collectionKeyPreallocationPlan.BindingIndex] = FlattenedWriteValue
            .UnresolvedCollectionItemId
            .Instance;
        valueAssigned[collectionKeyPreallocationPlan.BindingIndex] = true;
    }

    private static object?[] MaterializeSemanticIdentityValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        var collectionMergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableWritePlan)}' does not have a compiled collection merge plan."
            );

        if (collectionMergePlan.SemanticIdentityBindings.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableWritePlan)}' does not have compiled semantic-identity bindings."
            );
        }

        object?[] semanticIdentityValues = new object?[collectionMergePlan.SemanticIdentityBindings.Length];

        for (
            var bindingIndex = 0;
            bindingIndex < collectionMergePlan.SemanticIdentityBindings.Length;
            bindingIndex++
        )
        {
            var semanticIdentityBinding = collectionMergePlan.SemanticIdentityBindings[bindingIndex];

            if (values[semanticIdentityBinding.BindingIndex] is not FlattenedWriteValue.Literal literalValue)
            {
                throw new InvalidOperationException(
                    $"Collection semantic-identity binding '{semanticIdentityBinding.RelativePath.Canonical}' "
                        + $"on table '{FormatTable(tableWritePlan)}' did not materialize as a literal value."
                );
            }

            semanticIdentityValues[bindingIndex] = literalValue.Value;
        }

        return semanticIdentityValues;
    }

    private static IReadOnlyList<FlattenedWriteValue> GetPhysicalRowIdentityValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        var physicalRowIdentityColumns = tableWritePlan
            .TableModel
            .IdentityMetadata
            .PhysicalRowIdentityColumns;
        FlattenedWriteValue[] physicalRowIdentityValues = new FlattenedWriteValue[
            physicalRowIdentityColumns.Count
        ];

        for (var index = 0; index < physicalRowIdentityColumns.Count; index++)
        {
            physicalRowIdentityValues[index] = values[
                FindBindingIndex(tableWritePlan, physicalRowIdentityColumns[index])
            ];
        }

        return physicalRowIdentityValues;
    }

    private static IEnumerable<CollectionScopeInstance> EnumerateCollectionScopeInstances(
        JsonNode parentScopeNode,
        CollectionChildPlan childPlan
    )
    {
        var relativeScopeSegments = childPlan.RelativeScopeSegments;

        if (
            relativeScopeSegments.IsDefaultOrEmpty
            || relativeScopeSegments[^1] is not JsonPathSegment.AnyArrayElement
        )
        {
            throw new InvalidOperationException(
                $"Collection table '{FormatTable(childPlan.TableWritePlan)}' does not have a canonical immediate-child collection scope."
            );
        }

        if (
            !TryNavigateRelativeNode(
                parentScopeNode,
                relativeScopeSegments[..^1],
                out var collectionArrayNode
            ) || collectionArrayNode is null
        )
        {
            yield break;
        }

        if (collectionArrayNode is not JsonArray collectionArray)
        {
            throw new InvalidOperationException(
                $"Collection table '{FormatTable(childPlan.TableWritePlan)}' expected a JSON array at path "
                    + $"'{childPlan.TableWritePlan.TableModel.JsonScope.Canonical}'."
            );
        }

        for (var index = 0; index < collectionArray.Count; index++)
        {
            if (collectionArray[index] is not JsonObject collectionItem)
            {
                throw new InvalidOperationException(
                    $"Collection table '{FormatTable(childPlan.TableWritePlan)}' expected object items at path "
                        + $"'{childPlan.TableWritePlan.TableModel.JsonScope.Canonical}[{index}]'."
                );
            }

            yield return new CollectionScopeInstance(index, collectionItem);
        }
    }

    private static bool TryNavigateRelativeNode(
        JsonNode scopeNode,
        IReadOnlyList<JsonPathSegment> segments,
        out JsonNode? resolvedNode
    )
    {
        ArgumentNullException.ThrowIfNull(scopeNode);
        ArgumentNullException.ThrowIfNull(segments);

        JsonNode? currentNode = scopeNode;

        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            if (segments[segmentIndex] is not JsonPathSegment.Property property)
            {
                throw new InvalidOperationException(
                    $"Scope-local traversal does not support JSONPath segment '{segments[segmentIndex].GetType().Name}'."
                );
            }

            if (currentNode is not JsonObject jsonObject)
            {
                resolvedNode = null;
                return false;
            }

            if (!jsonObject.TryGetPropertyValue(property.Name, out var childNode))
            {
                resolvedNode = null;
                return false;
            }

            if (childNode is null)
            {
                resolvedNode = null;
                return segmentIndex == segments.Count - 1;
            }

            currentNode = childNode;
        }

        resolvedNode = currentNode;
        return true;
    }

    private static bool TryGetRelativeLeafNode(
        JsonNode scopeNode,
        JsonPathExpression relativePath,
        out JsonNode? value
    )
    {
        var segments = RestrictedJsonPath.GetSegments(relativePath);
        return TryNavigateRelativeNode(scopeNode, segments, out value);
    }

    private static int FindBindingIndex(TableWritePlan tableWritePlan, DbColumnName columnName)
    {
        for (var index = 0; index < tableWritePlan.ColumnBindings.Length; index++)
        {
            if (tableWritePlan.ColumnBindings[index].Column.ColumnName.Equals(columnName))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            $"Table '{FormatTable(tableWritePlan)}' does not have a write binding for column '{columnName.Value}'."
        );
    }

    private static int[] AppendOrdinalPath(ReadOnlySpan<int> parentOrdinalPath, int ordinal)
    {
        var ordinalPath = new int[parentOrdinalPath.Length + 1];
        parentOrdinalPath.CopyTo(ordinalPath);
        ordinalPath[^1] = ordinal;

        return ordinalPath;
    }

    private static object ConvertScalarValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string absolutePath
    )
    {
        return ConvertScalarValue(
            jsonValue,
            scalarType,
            tableWritePlan,
            columnBinding.Column.ColumnName,
            absolutePath
        );
    }

    private static object ConvertScalarValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        DbColumnName columnName,
        string absolutePath
    )
    {
        return scalarType.Kind switch
        {
            ScalarKind.String => ReadRequiredJsonValue<string>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnName,
                absolutePath
            ),
            ScalarKind.Int32 => ReadRequiredJsonValue<int>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnName,
                absolutePath
            ),
            ScalarKind.Int64 => ReadRequiredJsonValue<long>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnName,
                absolutePath
            ),
            ScalarKind.Decimal => ReadRequiredJsonValue<decimal>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnName,
                absolutePath
            ),
            ScalarKind.Boolean => ReadRequiredJsonValue<bool>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnName,
                absolutePath
            ),
            ScalarKind.Date => ReadDateOnlyValue(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnName,
                absolutePath
            ),
            ScalarKind.DateTime => ReadDateTimeValue(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnName,
                absolutePath
            ),
            ScalarKind.Time => ReadTimeOnlyValue(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnName,
                absolutePath
            ),
            _ => throw new InvalidOperationException(
                $"Scalar kind '{scalarType.Kind}' is not supported by the relational write flattener."
            ),
        };
    }

    private static T ReadRequiredJsonValue<T>(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        DbColumnName columnName,
        string absolutePath
    )
        where T : notnull
    {
        if (jsonValue.TryGetValue<T>(out var value))
        {
            return value;
        }

        throw CreateInvalidScalarReadException(
            tableWritePlan,
            columnName,
            absolutePath,
            scalarType,
            $"encountered JSON value kind '{jsonValue.GetValueKind()}' with raw value {jsonValue.ToJsonString()}"
        );
    }

    private static DateOnly ReadDateOnlyValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        DbColumnName columnName,
        string absolutePath
    )
    {
        if (
            jsonValue.TryGetValue<string>(out var rawValue)
            && DateOnly.TryParseExact(
                rawValue,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateOnlyValue
            )
        )
        {
            return dateOnlyValue;
        }

        throw CreateInvalidScalarReadException(
            tableWritePlan,
            columnName,
            absolutePath,
            scalarType,
            $"encountered JSON value kind '{jsonValue.GetValueKind()}' with raw value {jsonValue.ToJsonString()}"
        );
    }

    private static DateTime ReadDateTimeValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        DbColumnName columnName,
        string absolutePath
    )
    {
        if (
            jsonValue.TryGetValue<string>(out var rawValue)
            && DateTime.TryParse(
                rawValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dateTimeValue
            )
        )
        {
            return dateTimeValue;
        }

        throw CreateInvalidScalarReadException(
            tableWritePlan,
            columnName,
            absolutePath,
            scalarType,
            $"encountered JSON value kind '{jsonValue.GetValueKind()}' with raw value {jsonValue.ToJsonString()}"
        );
    }

    private static TimeOnly ReadTimeOnlyValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        DbColumnName columnName,
        string absolutePath
    )
    {
        if (
            jsonValue.TryGetValue<string>(out var rawValue)
            && TimeOnly.TryParse(
                rawValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timeOnlyValue
            )
        )
        {
            return timeOnlyValue;
        }

        throw CreateInvalidScalarReadException(
            tableWritePlan,
            columnName,
            absolutePath,
            scalarType,
            $"encountered JSON value kind '{jsonValue.GetValueKind()}' with raw value {jsonValue.ToJsonString()}"
        );
    }

    private static FlattenedWriteValue ResolveRootDocumentIdValue(RelationalWriteTargetContext targetContext)
    {
        return targetContext switch
        {
            RelationalWriteTargetContext.CreateNew => FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
            RelationalWriteTargetContext.ExistingDocument existingDocument => new FlattenedWriteValue.Literal(
                existingDocument.DocumentId
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational write target context '{targetContext.GetType().Name}'."
            ),
        };
    }

    private static NotSupportedException CreateUnsupportedValueSourceException(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string featureDescription
    )
    {
        return new NotSupportedException(
            $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' depends on {featureDescription}, which are not implemented in the initial relational write flattener."
        );
    }

    private static InvalidOperationException CreateDuplicateSemanticIdentityException(
        TableWritePlan tableWritePlan,
        string parentScopeCanonical,
        IReadOnlyList<int> firstOrdinalPath,
        IReadOnlyList<int> duplicateOrdinalPath,
        IReadOnlyList<object?> semanticIdentityValues
    )
    {
        return new InvalidOperationException(
            $"Collection table '{FormatTable(tableWritePlan)}' received duplicate semantic identity values "
                + $"{FormatSemanticIdentityValues(semanticIdentityValues)} under parent scope '{parentScopeCanonical}'. "
                + $"First ordinal path: {FormatOrdinalPath(firstOrdinalPath)}. "
                + $"Duplicate ordinal path: {FormatOrdinalPath(duplicateOrdinalPath)}."
        );
    }

    private static InvalidOperationException CreateInvalidScalarReadException(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string absolutePath,
        RelationalScalarType scalarType,
        string reason
    )
    {
        return CreateInvalidScalarReadException(
            tableWritePlan,
            columnBinding.Column.ColumnName,
            absolutePath,
            scalarType,
            reason
        );
    }

    private static InvalidOperationException CreateInvalidScalarReadException(
        TableWritePlan tableWritePlan,
        DbColumnName columnName,
        string absolutePath,
        RelationalScalarType scalarType,
        string reason
    )
    {
        return new InvalidOperationException(
            $"Column '{columnName.Value}' on table '{FormatTable(tableWritePlan)}' expected scalar kind '{scalarType.Kind}' at path '{absolutePath}', but {reason}."
        );
    }

    private static InvalidOperationException CreateInvalidKeyUnificationScalarReadException(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan.ScalarMember member,
        string absolutePath,
        string reason
    )
    {
        return new InvalidOperationException(
            $"Key-unification member '{member.MemberPathColumn.Value}' on table '{FormatTable(tableWritePlan)}' "
                + $"expected scalar kind '{member.ScalarType.Kind}' at path '{absolutePath}', but {reason}."
        );
    }

    private static string FormatLiteral(object? value)
    {
        return value switch
        {
            null => "null",
            string stringValue => $"'{stringValue}'",
            bool boolValue => boolValue ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? "<unknown>",
        };
    }

    private static string FormatSemanticIdentityValues(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return $"[{string.Join(", ", values.Select(FormatLiteral))}]";
    }

    private static string FormatOrdinalPath(IReadOnlyList<int> ordinalPath)
    {
        ArgumentNullException.ThrowIfNull(ordinalPath);
        return $"[{string.Join(", ", ordinalPath)}]";
    }

    private static string GetJsonValueKind(JsonNode node)
    {
        return node switch
        {
            JsonValue jsonValue => jsonValue.GetValueKind().ToString(),
            _ => node.GetType().Name,
        };
    }

    private static string FormatTable(TableWritePlan tableWritePlan)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        return $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";
    }

    private sealed record KeyUnificationMemberEvaluation(bool IsPresent, object? Value)
    {
        public static KeyUnificationMemberEvaluation Absent { get; } = new(false, null);

        public static KeyUnificationMemberEvaluation Present(object value)
        {
            return new(true, value);
        }
    }

    private sealed record CollectionChildPlan(
        TableWritePlan TableWritePlan,
        ImmutableArray<JsonPathSegment> RelativeScopeSegments
    );

    private sealed record CollectionScopeInstance(int RequestOrder, JsonObject ScopeNode);

    private sealed class SemanticIdentityValueArrayComparer : IEqualityComparer<object?[]>
    {
        public static SemanticIdentityValueArrayComparer Instance { get; } = new();

        public bool Equals(object?[]? x, object?[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Length != y.Length)
            {
                return false;
            }

            for (var index = 0; index < x.Length; index++)
            {
                if (!Equals(x[index], y[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(object?[] values)
        {
            ArgumentNullException.ThrowIfNull(values);

            HashCode hashCode = new();
            hashCode.Add(values.Length);

            for (var index = 0; index < values.Length; index++)
            {
                hashCode.Add(values[index]);
            }

            return hashCode.ToHashCode();
        }
    }

    private sealed class SelectedBodyIndex
    {
        private readonly Dictionary<string, JsonNode?> _leafNodesByPath;
        private readonly HashSet<string> _scopesWithBoundData;

        private SelectedBodyIndex(
            Dictionary<string, JsonNode?> leafNodesByPath,
            HashSet<string> scopesWithBoundData
        )
        {
            _leafNodesByPath = leafNodesByPath ?? throw new ArgumentNullException(nameof(leafNodesByPath));
            _scopesWithBoundData =
                scopesWithBoundData ?? throw new ArgumentNullException(nameof(scopesWithBoundData));
        }

        public static SelectedBodyIndex Create(JsonNode selectedBody)
        {
            ArgumentNullException.ThrowIfNull(selectedBody);

            if (selectedBody is not JsonObject rootObject)
            {
                throw new InvalidOperationException(
                    $"Selected write body must be a JSON object, but found '{selectedBody.GetType().Name}'."
                );
            }

            Dictionary<string, JsonNode?> leafNodesByPath = new(StringComparer.Ordinal);
            HashSet<string> scopesWithBoundData = ["$"];

            VisitObject(rootObject, "$", leafNodesByPath, scopesWithBoundData);

            return new SelectedBodyIndex(leafNodesByPath, scopesWithBoundData);
        }

        public bool TryGetLeafNode(string absolutePath, out JsonNode? value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
            return _leafNodesByPath.TryGetValue(absolutePath, out value);
        }

        public bool HasBoundDataForScope(string scopePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scopePath);
            return _scopesWithBoundData.Contains(scopePath);
        }

        private static void VisitObject(
            JsonObject jsonObject,
            string currentPath,
            Dictionary<string, JsonNode?> leafNodesByPath,
            HashSet<string> scopesWithBoundData
        )
        {
            foreach (var property in jsonObject)
            {
                var propertyPath = $"{currentPath}.{property.Key}";

                switch (property.Value)
                {
                    case JsonObject childObject:
                        VisitObject(childObject, propertyPath, leafNodesByPath, scopesWithBoundData);
                        break;
                    case JsonArray:
                        break;
                    default:
                        leafNodesByPath[propertyPath] = property.Value;
                        MarkAncestorScopes(propertyPath, scopesWithBoundData);
                        break;
                }
            }
        }

        private static void MarkAncestorScopes(string leafPath, HashSet<string> scopesWithBoundData)
        {
            var currentPath = leafPath;

            while (true)
            {
                var lastDotIndex = currentPath.LastIndexOf('.');

                if (lastDotIndex <= 0)
                {
                    scopesWithBoundData.Add("$");
                    return;
                }

                currentPath = currentPath[..lastDotIndex];
                scopesWithBoundData.Add(currentPath);
            }
        }
    }

    private static class RestrictedJsonPath
    {
        public static string CombineCanonical(JsonPathExpression scopePath, JsonPathExpression relativePath)
        {
            JsonPathSegment[] combinedSegments = [.. GetSegments(scopePath), .. GetSegments(relativePath)];

            return BuildCanonical(combinedSegments);
        }

        public static IReadOnlyList<JsonPathSegment> GetSegments(JsonPathExpression path)
        {
            if (path.Canonical == "$")
            {
                return [];
            }

            if (path.Segments.Count > 0)
            {
                return path.Segments;
            }

            return Parse(path.Canonical);
        }

        private static JsonPathSegment[] Parse(string canonicalPath)
        {
            if (string.IsNullOrWhiteSpace(canonicalPath) || canonicalPath[0] != '$')
            {
                throw new InvalidOperationException(
                    $"Restricted JSONPath '{canonicalPath}' is not canonical."
                );
            }

            List<JsonPathSegment> segments = [];
            var index = 1;

            while (index < canonicalPath.Length)
            {
                switch (canonicalPath[index])
                {
                    case '.':
                        index = AppendProperty(canonicalPath, index, segments);
                        break;
                    case '[' when IsArrayWildcard(canonicalPath, index):
                        segments.Add(new JsonPathSegment.AnyArrayElement());
                        index += 3;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Restricted JSONPath '{canonicalPath}' is not canonical."
                        );
                }
            }

            return [.. segments];
        }

        private static int AppendProperty(string canonicalPath, int dotIndex, List<JsonPathSegment> segments)
        {
            var startIndex = dotIndex + 1;
            var index = startIndex;

            while (index < canonicalPath.Length && canonicalPath[index] is not ('.' or '['))
            {
                index++;
            }

            if (index == startIndex)
            {
                throw new InvalidOperationException(
                    $"Restricted JSONPath '{canonicalPath}' is not canonical."
                );
            }

            segments.Add(new JsonPathSegment.Property(canonicalPath[startIndex..index]));

            return index;
        }

        private static bool IsArrayWildcard(string canonicalPath, int openBracketIndex)
        {
            return openBracketIndex + 2 < canonicalPath.Length
                && canonicalPath[openBracketIndex + 1] == '*'
                && canonicalPath[openBracketIndex + 2] == ']';
        }

        public static string BuildCanonical(IReadOnlyList<JsonPathSegment> segments)
        {
            ArgumentNullException.ThrowIfNull(segments);

            return string.Create(
                CalculateCanonicalLength(segments),
                segments,
                static (buffer, state) =>
                {
                    buffer[0] = '$';
                    var index = 1;

                    foreach (var segment in state)
                    {
                        switch (segment)
                        {
                            case JsonPathSegment.Property property:
                                buffer[index++] = '.';
                                property.Name.AsSpan().CopyTo(buffer[index..]);
                                index += property.Name.Length;
                                break;
                            case JsonPathSegment.AnyArrayElement:
                                "[*]".AsSpan().CopyTo(buffer[index..]);
                                index += 3;
                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"Restricted JSONPath segment '{segment.GetType().Name}' is not supported."
                                );
                        }
                    }
                }
            );
        }

        private static int CalculateCanonicalLength(IReadOnlyList<JsonPathSegment> segments)
        {
            var length = 1;

            for (var index = 0; index < segments.Count; index++)
            {
                length += segments[index] switch
                {
                    JsonPathSegment.Property property => property.Name.Length + 1,
                    JsonPathSegment.AnyArrayElement => 3,
                    _ => throw new InvalidOperationException(
                        $"Restricted JSONPath segment '{segments[index].GetType().Name}' is not supported."
                    ),
                };
            }

            return length;
        }
    }
}
