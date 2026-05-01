// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

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
        var rootScopeNode = GetRootScopeNode(flatteningInput.SelectedBody);
        var resolvedReferenceLookups = FlatteningResolvedReferenceLookupSet.Create(
            writePlan,
            flatteningInput.ResolvedReferences
        );
        var rootDocumentIdValue = ResolveRootDocumentIdValue(flatteningInput.TargetContext);
        var traversalPlans = new TraversalPlans(
            BuildCollectionChildPlansByParentScope(writePlan),
            BuildAttachedAlignedScopePlansByParentScope(writePlan)
        );

        var rootRow = new RootWriteRowBuffer(
            tableWritePlan: rootTablePlan,
            values: MaterializeValues(
                flatteningInput,
                rootTablePlan,
                rootScopeNode,
                resolvedReferenceLookups,
                parentKeyParts: [],
                ordinal: 0,
                ordinalPath: []
            ),
            rootExtensionRows: MaterializeRootExtensionRows(
                flatteningInput,
                rootScopeNode,
                resolvedReferenceLookups,
                rootDocumentIdValue,
                traversalPlans
            ),
            collectionCandidates: MaterializeCollectionCandidates(
                flatteningInput,
                traversalPlans,
                rootTablePlan.TableModel.JsonScope.Canonical,
                rootScopeNode,
                parentKeyParts: [],
                parentOrdinalPath: [],
                resolvedReferenceLookups
            )
        );

        return new FlattenedWriteSet(rootRow);
    }

    private static JsonObject GetRootScopeNode(JsonNode selectedBody)
    {
        ArgumentNullException.ThrowIfNull(selectedBody);

        if (selectedBody is JsonObject rootScopeNode)
        {
            return rootScopeNode;
        }

        throw CreateRequestShapeValidationException(
            "$",
            $"Selected write body must be a JSON object, but found '{selectedBody.GetType().Name}'."
        );
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

        // The traversal in MaterializeCollectionCandidates only re-enters table-backed
        // scopes (root, root extension, collection rows, aligned-extension scopes), so a
        // collection table whose immediate JSON parent is an inlined non-collection scope
        // (e.g. $.parents[*].detail.children[*]) must be reached from its nearest
        // table-backed ancestor with the inlined intermediate property segments folded
        // into the child plan's relative path.
        var tableBackedScopes = new HashSet<string>(
            writePlan.TablePlansInDependencyOrder.Select(static plan => plan.TableModel.JsonScope.Canonical),
            StringComparer.Ordinal
        );

        Dictionary<string, List<CollectionChildPlan>> childPlansByParentScope = new(StringComparer.Ordinal);

        foreach (
            var tableWritePlan in writePlan.TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind
                    is DbTableKind.Collection
                        or DbTableKind.ExtensionCollection
            )
        )
        {
            var scopeSegments = RelationalJsonPathSupport
                .GetRestrictedSegments(tableWritePlan.TableModel.JsonScope)
                .ToArray();

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

            // Walk the ancestor prefix from the immediate JSON parent back toward the
            // root, picking the longest prefix whose canonical is table-backed. The root
            // scope "$" (segment length 0) is always table-backed, so the loop is
            // guaranteed to terminate.
            var parentSegmentLength = scopeSegments.Length - 2;
            while (parentSegmentLength > 0)
            {
                var candidateScope = RelationalJsonPathSupport.BuildCanonical(
                    scopeSegments[..parentSegmentLength]
                );
                if (tableBackedScopes.Contains(candidateScope))
                {
                    break;
                }
                parentSegmentLength--;
            }

            var parentScopeSegments = scopeSegments[..parentSegmentLength];
            var parentScopeCanonical = RelationalJsonPathSupport.BuildCanonical(parentScopeSegments);
            var relativeScopeSegments = scopeSegments[parentSegmentLength..];

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

    private static IReadOnlyDictionary<
        string,
        IReadOnlyList<AttachedAlignedScopePlan>
    > BuildAttachedAlignedScopePlansByParentScope(ResourceWritePlan writePlan)
    {
        ArgumentNullException.ThrowIfNull(writePlan);

        Dictionary<string, List<AttachedAlignedScopePlan>> plansByParentScope = new(StringComparer.Ordinal);

        foreach (
            var tableWritePlan in writePlan.TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind == DbTableKind.CollectionExtensionScope
            )
        )
        {
            var scopeSegments = RelationalJsonPathSupport
                .GetRestrictedSegments(tableWritePlan.TableModel.JsonScope)
                .ToArray();

            if (
                scopeSegments.Length < 2
                || scopeSegments[^2] is not JsonPathSegment.Property extensionMarker
                || scopeSegments[^1] is not JsonPathSegment.Property
                || !string.Equals(extensionMarker.Name, "_ext", StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    $"Collection-aligned extension table '{FormatTable(tableWritePlan)}' does not have a canonical aligned scope."
                );
            }

            var isMirroredExtensionScope =
                scopeSegments.Length >= 4
                && scopeSegments[0] is JsonPathSegment.Property { Name: "_ext" }
                && scopeSegments[1] is JsonPathSegment.Property;
            var parentScopeSegments = isMirroredExtensionScope ? scopeSegments[2..^2] : scopeSegments[..^2];
            var parentScopeCanonical = RelationalJsonPathSupport.BuildCanonical(parentScopeSegments);
            var relativeScopeSegments = scopeSegments[parentScopeSegments.Length..];

            if (!plansByParentScope.TryGetValue(parentScopeCanonical, out var plans))
            {
                plans = [];
                plansByParentScope.Add(parentScopeCanonical, plans);
            }

            plans.Add(
                new AttachedAlignedScopePlan(
                    tableWritePlan,
                    [.. relativeScopeSegments],
                    isMirroredExtensionScope
                )
            );
        }

        return plansByParentScope.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<AttachedAlignedScopePlan>)entry.Value,
            StringComparer.Ordinal
        );
    }

    private static IEnumerable<RootExtensionWriteRowBuffer> MaterializeRootExtensionRows(
        FlatteningInput flatteningInput,
        JsonObject rootScopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        FlattenedWriteValue rootDocumentIdValue,
        TraversalPlans traversalPlans
    )
    {
        var rootParentKeyParts = new[] { rootDocumentIdValue };

        foreach (
            var tableWritePlan in flatteningInput.WritePlan.TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind == DbTableKind.RootExtension
            )
        )
        {
            if (
                !TryGetScopeNode(rootScopeNode, tableWritePlan.TableModel.JsonScope, out var scopeNode)
                || scopeNode is null
            )
            {
                continue;
            }

            if (scopeNode is not JsonObject scopeObject)
            {
                throw CreateRequestShapeValidationException(
                    tableWritePlan.TableModel.JsonScope.Canonical,
                    $"Root extension table '{FormatTable(tableWritePlan)}' expected a JSON object at path "
                        + $"'{tableWritePlan.TableModel.JsonScope.Canonical}', but encountered "
                        + $"'{scopeNode.GetType().Name}'."
                );
            }

            var collectionCandidates = MaterializeCollectionCandidates(
                flatteningInput,
                traversalPlans,
                tableWritePlan.TableModel.JsonScope.Canonical,
                scopeObject,
                rootParentKeyParts,
                parentOrdinalPath: [],
                resolvedReferenceLookups
            );

            if (
                !flatteningInput.EmitEmptyRootExtensionBuffers
                && !HasBoundScopeData(scopeObject)
                && collectionCandidates.Count == 0
            )
            {
                continue;
            }

            yield return new RootExtensionWriteRowBuffer(
                tableWritePlan,
                MaterializeValues(
                    flatteningInput,
                    tableWritePlan,
                    scopeObject,
                    resolvedReferenceLookups,
                    rootParentKeyParts,
                    ordinal: 0,
                    ordinalPath: []
                ),
                collectionCandidates: collectionCandidates
            );
        }
    }

    private static IReadOnlyList<CollectionWriteCandidate> MaterializeCollectionCandidates(
        FlatteningInput flatteningInput,
        TraversalPlans traversalPlans,
        string parentScopeCanonical,
        JsonNode parentScopeNode,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts,
        ReadOnlySpan<int> parentOrdinalPath,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        if (
            !traversalPlans.CollectionChildPlansByParentScope.TryGetValue(
                parentScopeCanonical,
                out var childPlans
            )
            || childPlans.Count == 0
        )
        {
            return [];
        }

        List<CollectionWriteCandidate> collectionCandidates = [];

        foreach (var childPlan in childPlans)
        {
            Dictionary<string, (int[] OrdinalPath, object?[] Values)> firstOrdinalPathBySemanticIdentityKey =
                new(StringComparer.Ordinal);

            foreach (
                var collectionScopeInstance in EnumerateCollectionScopeInstances(parentScopeNode, childPlan)
            )
            {
                var ordinalPath = AppendOrdinalPath(parentOrdinalPath, collectionScopeInstance.RequestOrder);
                var values = MaterializeValues(
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
                var semanticIdentityInOrder = MaterializeSemanticIdentityParts(
                    childPlan.TableWritePlan,
                    collectionScopeInstance.ScopeNode,
                    semanticIdentityValues
                );

                // Duplicate detection uses the presence-aware key so that an explicit JSON
                // null and a missing identity property remain distinct identities under the
                // SemanticIdentityPart contract. Two array elements with the same null value
                // are duplicates only when both are present-null or both are missing — never
                // across the boundary.
                var semanticIdentityKey = Profile.SemanticIdentityKeys.BuildKey(semanticIdentityInOrder);

                if (firstOrdinalPathBySemanticIdentityKey.TryGetValue(semanticIdentityKey, out var firstSeen))
                {
                    throw CreateDuplicateSemanticIdentityException(
                        childPlan.TableWritePlan,
                        parentScopeCanonical,
                        firstSeen.OrdinalPath,
                        ordinalPath,
                        firstSeen.Values
                    );
                }

                firstOrdinalPathBySemanticIdentityKey.Add(
                    semanticIdentityKey,
                    (ordinalPath, semanticIdentityValues)
                );

                var childParentKeyParts = GetPhysicalRowIdentityValues(childPlan.TableWritePlan, values);
                var nestedCollectionCandidates = MaterializeCollectionCandidates(
                    flatteningInput,
                    traversalPlans,
                    childPlan.TableWritePlan.TableModel.JsonScope.Canonical,
                    collectionScopeInstance.ScopeNode,
                    childParentKeyParts,
                    ordinalPath,
                    resolvedReferenceLookups
                );
                var attachedAlignedScopeData = MaterializeAttachedAlignedScopeData(
                    flatteningInput,
                    traversalPlans,
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
                        attachedAlignedScopeData,
                        collectionCandidates: nestedCollectionCandidates,
                        semanticIdentityInOrder: semanticIdentityInOrder
                    )
                );
            }
        }

        return collectionCandidates;
    }

    private static IReadOnlyList<CandidateAttachedAlignedScopeData> MaterializeAttachedAlignedScopeData(
        FlatteningInput flatteningInput,
        TraversalPlans traversalPlans,
        string parentScopeCanonical,
        JsonNode parentScopeNode,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts,
        ReadOnlySpan<int> parentOrdinalPath,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        if (
            !traversalPlans.AttachedAlignedScopePlansByParentScope.TryGetValue(
                parentScopeCanonical,
                out var attachedScopePlans
            )
            || attachedScopePlans.Count == 0
        )
        {
            return [];
        }

        List<CandidateAttachedAlignedScopeData> attachedScopeData = [];

        foreach (var attachedScopePlan in attachedScopePlans)
        {
            if (
                !TryGetAttachedAlignedScopeNode(
                    flatteningInput.SelectedBody,
                    parentScopeNode,
                    parentOrdinalPath,
                    attachedScopePlan,
                    out var scopeNode
                ) || scopeNode is null
            )
            {
                continue;
            }

            if (scopeNode is not JsonObject scopeObject)
            {
                throw CreateRequestShapeValidationException(
                    attachedScopePlan.TableWritePlan.TableModel.JsonScope.Canonical,
                    $"Collection-aligned extension table '{FormatTable(attachedScopePlan.TableWritePlan)}' expected a JSON object at path "
                        + $"'{attachedScopePlan.TableWritePlan.TableModel.JsonScope.Canonical}', but encountered "
                        + $"'{scopeNode.GetType().Name}'."
                );
            }

            var childCollectionCandidates = MaterializeCollectionCandidates(
                flatteningInput,
                traversalPlans,
                attachedScopePlan.TableWritePlan.TableModel.JsonScope.Canonical,
                scopeObject,
                parentKeyParts,
                parentOrdinalPath,
                resolvedReferenceLookups
            );

            if (
                !flatteningInput.EmitEmptyRootExtensionBuffers
                && !HasBoundScopeData(scopeObject)
                && childCollectionCandidates.Count == 0
            )
            {
                continue;
            }

            attachedScopeData.Add(
                new CandidateAttachedAlignedScopeData(
                    attachedScopePlan.TableWritePlan,
                    MaterializeValues(
                        flatteningInput,
                        attachedScopePlan.TableWritePlan,
                        scopeObject,
                        resolvedReferenceLookups,
                        parentKeyParts,
                        ordinal: 0,
                        ordinalPath: parentOrdinalPath
                    ),
                    childCollectionCandidates
                )
            );
        }

        return attachedScopeData;
    }

    private static IReadOnlyList<FlattenedWriteValue> MaterializeValues(
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

            values[bindingIndex] = MaterializeValue(
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

            Profile.ProfileKeyUnificationGuardrails.Validate(
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
            var evaluation = Profile.FlattenerMemberEvaluation.EvaluateKeyUnificationMember(
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
                throw CreateRequestShapeValidationException(
                    RelationalJsonPathSupport.CombineRestrictedCanonical(
                        tableWritePlan.TableModel.JsonScope,
                        member.RelativePath
                    ),
                    $"Key-unification conflict for canonical column '{keyUnificationPlan.CanonicalColumn.Value}' "
                        + $"on table '{FormatTable(tableWritePlan)}': member '{firstPresentMember.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(canonicalValue)} but member '{member.MemberPathColumn.Value}' "
                        + $"resolved to {FormatLiteral(evaluation.Value)}."
                );
            }
        }

        return canonicalValue;
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
            WriteValueSource.Scalar scalar => ResolveScalarValue(
                tableWritePlan,
                columnBinding,
                scalar,
                scopeNode
            ),
            WriteValueSource.ReferenceDerived referenceDerived => ResolveReferenceDerivedValue(
                tableWritePlan,
                columnBinding,
                referenceDerived,
                scopeNode,
                resolvedReferenceLookups,
                ordinalPath
            ),
            WriteValueSource.DocumentReference documentReference => ResolveDocumentReferenceValue(
                flatteningInput,
                tableWritePlan,
                columnBinding,
                documentReference,
                scopeNode,
                resolvedReferenceLookups,
                ordinalPath
            ),
            WriteValueSource.DescriptorReference descriptorReference => ResolveDescriptorReferenceValue(
                tableWritePlan,
                columnBinding,
                descriptorReference,
                scopeNode,
                resolvedReferenceLookups,
                ordinalPath
            ),
            WriteValueSource.Ordinal => ResolveOrdinalValue(tableWritePlan, columnBinding, ordinal),
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

    private static FlattenedWriteValue ResolveOrdinalValue(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        int ordinal
    )
    {
        if (tableWritePlan.CollectionMergePlan is not null)
        {
            return new FlattenedWriteValue.Literal(ordinal);
        }

        throw CreateUnsupportedValueSourceException(tableWritePlan, columnBinding, "collection ordinals");
    }

    private static FlattenedWriteValue ResolveDocumentReferenceValue(
        FlatteningInput flatteningInput,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.DocumentReference documentReference,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        var binding = GetDocumentReferenceBinding(flatteningInput.WritePlan, documentReference);
        var relativeReferencePath = GetRelativePathWithinScope(tableWritePlan, binding.ReferenceObjectPath);

        if (
            !TryNavigateRelativeNode(scopeNode, relativeReferencePath, out var referenceNode)
            || referenceNode is null
        )
        {
            return new FlattenedWriteValue.Literal(null);
        }

        var documentId = resolvedReferenceLookups.GetDocumentId(documentReference.BindingIndex, ordinalPath);

        if (documentId is not null)
        {
            return new FlattenedWriteValue.Literal(documentId.Value);
        }

        throw CreateMissingDocumentReferenceLookupException(
            tableWritePlan,
            columnBinding,
            binding.ReferenceObjectPath.Canonical,
            ordinalPath,
            binding.TargetResource
        );
    }

    private static FlattenedWriteValue ResolveDescriptorReferenceValue(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.DescriptorReference descriptorReference,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        if (
            !TryGetRelativeLeafNode(scopeNode, descriptorReference.RelativePath, out var descriptorNode)
            || descriptorNode is null
        )
        {
            return new FlattenedWriteValue.Literal(null);
        }

        EnsureDescriptorValueNode(tableWritePlan, columnBinding, descriptorReference, descriptorNode);

        var descriptorId = resolvedReferenceLookups.GetDescriptorId(
            tableWritePlan,
            descriptorReference,
            ordinalPath
        );

        if (descriptorId is not null)
        {
            return new FlattenedWriteValue.Literal(descriptorId.Value);
        }

        throw CreateMissingDescriptorReferenceLookupException(
            tableWritePlan,
            columnBinding,
            GetDescriptorAbsolutePath(tableWritePlan, descriptorReference),
            ordinalPath,
            descriptorReference.DescriptorResource
        );
    }

    private static void EnsureDescriptorValueNode(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.DescriptorReference descriptorReference,
        JsonNode descriptorNode
    )
    {
        if (descriptorNode is JsonValue jsonValue && jsonValue.TryGetValue<string>(out _))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' expected a descriptor URI string at path "
                + $"'{GetDescriptorAbsolutePath(tableWritePlan, descriptorReference)}', but encountered JSON value kind "
                + $"'{GetJsonValueKind(descriptorNode)}'."
        );
    }

    private static FlattenedWriteValue ResolveScalarValue(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.Scalar scalar,
        JsonNode scopeNode
    )
    {
        var absolutePath = RelationalJsonPathSupport.CombineRestrictedCanonical(
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

        var conversionContext = CreateColumnScalarConversionContext(
            tableWritePlan,
            columnBinding.Column.ColumnName,
            absolutePath
        );

        if (scalarNode is not JsonValue jsonValue)
        {
            throw CreateInvalidRequestDerivedScalarException(
                conversionContext,
                scalar.Type,
                $"encountered non-scalar JSON node type '{scalarNode.GetType().Name}'"
            );
        }

        return new FlattenedWriteValue.Literal(
            ConvertRequestDerivedJsonValue(jsonValue, scalar.Type, conversionContext)
        );
    }

    private static FlattenedWriteValue ResolveReferenceDerivedValue(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.ReferenceDerived referenceDerived,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        if (
            !TryGetReferenceObjectNode(
                tableWritePlan,
                scopeNode,
                referenceDerived.ReferenceSource,
                out var referenceNode
            ) || referenceNode is null
        )
        {
            return new FlattenedWriteValue.Literal(null);
        }

        var absolutePath = MaterializeValidationPath(
            columnBinding.Column.SourceJsonPath?.Canonical
                ?? referenceDerived.ReferenceSource.ReferenceJsonPath.Canonical,
            ordinalPath
        );

        return new FlattenedWriteValue.Literal(
            ResolveReferenceDerivedLiteralValue(
                tableWritePlan,
                columnBinding.Column,
                columnBinding.Column.ColumnName,
                referenceDerived.ReferenceSource,
                absolutePath,
                resolvedReferenceLookups,
                ordinalPath
            )
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

        values[collectionKeyPreallocationPlan.BindingIndex] =
            FlattenedWriteValue.UnresolvedCollectionItemId.Create();
        valueAssigned[collectionKeyPreallocationPlan.BindingIndex] = true;
    }

    /// <summary>
    /// Builds the candidate's <see cref="CollectionWriteCandidate.SemanticIdentityInOrder"/>
    /// from compiled bindings, the materialized identity values, and a presence probe against
    /// the source JSON node. <see cref="SemanticIdentityPart.IsPresent"/> reflects whether the
    /// JSON property at the binding's relative path was present in the request body — keeping
    /// missing-vs-explicit-null fidelity end-to-end. <see cref="SemanticIdentityPart.RelativePath"/>
    /// is normalized to scope-relative form (the convention Core's address derivation engine
    /// publishes), so candidate-side keys produced by <see cref="SemanticIdentityKeys.BuildKey(CollectionWriteCandidate)"/>
    /// align with visible-request-item keys produced from
    /// <see cref="CollectionRowAddress.SemanticIdentityInOrder"/>.
    /// </summary>
    private static ImmutableArray<SemanticIdentityPart> MaterializeSemanticIdentityParts(
        TableWritePlan tableWritePlan,
        JsonNode scopeNode,
        object?[] semanticIdentityValues
    )
    {
        var collectionMergePlan = tableWritePlan.CollectionMergePlan!;
        var bindings = collectionMergePlan.SemanticIdentityBindings;
        var scopeCanonical = tableWritePlan.TableModel.JsonScope.Canonical;
        var parts = new SemanticIdentityPart[bindings.Length];

        for (var i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            var rawValue = semanticIdentityValues[i];
            JsonNode? jsonValue = rawValue is null ? null : JsonValue.Create(rawValue);
            var relativePath = ToScopeRelativeIdentityPath(binding.RelativePath.Canonical, scopeCanonical);
            var isPresent = ProbeIdentityPathPresence(scopeNode, relativePath);
            parts[i] = new SemanticIdentityPart(relativePath, jsonValue, isPresent);
        }

        return [.. parts];
    }

    /// <summary>
    /// Returns whether the JSON property at the binding's path exists on
    /// <paramref name="scopeNode"/>, distinguishing a missing property from an explicit JSON
    /// null. Walks the canonical relative path one segment at a time and returns <c>false</c>
    /// the moment any intermediate object lacks the property — matching the navigation
    /// semantics used by <c>AddressDerivationEngine.TryNavigateRelativePath</c> in Core, so
    /// the candidate-side presence flag converges with stored-side / address-side derivations.
    /// </summary>
    private static bool ProbeIdentityPathPresence(JsonNode scopeNode, string scopeRelativePath)
    {
        if (string.IsNullOrEmpty(scopeRelativePath))
        {
            return false;
        }

        if (scopeNode is not JsonObject objNode)
        {
            return false;
        }

        var segments = scopeRelativePath.Split('.');
        JsonNode? cursor = objNode;

        for (var i = 0; i < segments.Length; i++)
        {
            if (cursor is not JsonObject current)
            {
                return false;
            }

            if (!current.TryGetPropertyValue(segments[i], out var next))
            {
                return false;
            }

            if (i == segments.Length - 1)
            {
                return true;
            }

            cursor = next;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a binding's canonical path to the scope-relative form Core publishes (e.g.
    /// <c>"$.addresses[*].streetNumber"</c> with scope <c>"$.addresses[*]"</c> becomes
    /// <c>"streetNumber"</c>). Falls back to stripping a leading <c>"$."</c> for paths that
    /// do not nest under the supplied scope.
    /// </summary>
    private static string ToScopeRelativeIdentityPath(string canonicalPath, string scopeCanonical)
    {
        var scopePrefix = scopeCanonical + ".";
        if (canonicalPath.StartsWith(scopePrefix, StringComparison.Ordinal))
        {
            return canonicalPath[scopePrefix.Length..];
        }

        return canonicalPath.StartsWith("$.", StringComparison.Ordinal) ? canonicalPath[2..] : canonicalPath;
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
            throw CreateRequestShapeValidationException(
                childPlan.TableWritePlan.TableModel.JsonScope.Canonical,
                $"Collection table '{FormatTable(childPlan.TableWritePlan)}' expected a JSON array at path "
                    + $"'{childPlan.TableWritePlan.TableModel.JsonScope.Canonical}'."
            );
        }

        for (var index = 0; index < collectionArray.Count; index++)
        {
            if (collectionArray[index] is not JsonObject collectionItem)
            {
                throw CreateRequestShapeValidationException(
                    MaterializeConcretePath(childPlan.TableWritePlan.TableModel.JsonScope.Canonical, [index]),
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

    internal static bool TryNavigateConcreteNode(
        JsonNode scopeNode,
        IReadOnlyList<JsonPathSegment> segments,
        ReadOnlySpan<int> ordinalPath,
        out JsonNode? resolvedNode
    )
    {
        ArgumentNullException.ThrowIfNull(scopeNode);
        ArgumentNullException.ThrowIfNull(segments);

        JsonNode? currentNode = scopeNode;
        var ordinalIndex = 0;

        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            switch (segments[segmentIndex])
            {
                case JsonPathSegment.Property property:
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
                    break;

                case JsonPathSegment.AnyArrayElement:
                    if (
                        currentNode is not JsonArray jsonArray
                        || ordinalIndex >= ordinalPath.Length
                        || ordinalPath[ordinalIndex] < 0
                        || ordinalPath[ordinalIndex] >= jsonArray.Count
                    )
                    {
                        resolvedNode = null;
                        return false;
                    }

                    currentNode = jsonArray[ordinalPath[ordinalIndex]];
                    ordinalIndex++;

                    if (currentNode is null)
                    {
                        resolvedNode = null;
                        return segmentIndex == segments.Count - 1;
                    }

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Concrete traversal does not support JSONPath segment '{segments[segmentIndex].GetType().Name}'."
                    );
            }
        }

        resolvedNode = currentNode;
        return true;
    }

    private static bool TryGetAttachedAlignedScopeNode(
        JsonNode selectedBody,
        JsonNode parentScopeNode,
        ReadOnlySpan<int> parentOrdinalPath,
        AttachedAlignedScopePlan attachedScopePlan,
        out JsonNode? scopeNode
    )
    {
        ArgumentNullException.ThrowIfNull(selectedBody);
        ArgumentNullException.ThrowIfNull(parentScopeNode);
        ArgumentNullException.ThrowIfNull(attachedScopePlan);

        return attachedScopePlan.NavigateFromRoot
            ? TryNavigateConcreteNode(
                selectedBody,
                RelationalJsonPathSupport.GetRestrictedSegments(
                    attachedScopePlan.TableWritePlan.TableModel.JsonScope
                ),
                parentOrdinalPath,
                out scopeNode
            )
            : TryNavigateRelativeNode(
                parentScopeNode,
                attachedScopePlan.RelativeScopeSegments,
                out scopeNode
            );
    }

    internal static bool TryGetRelativeLeafNode(
        JsonNode scopeNode,
        JsonPathExpression relativePath,
        out JsonNode? value
    )
    {
        var segments = RelationalJsonPathSupport.GetRestrictedSegments(relativePath);
        return TryNavigateRelativeNode(scopeNode, segments, out value);
    }

    private static bool HasBoundScopeData(JsonNode scopeNode)
    {
        ArgumentNullException.ThrowIfNull(scopeNode);

        return scopeNode switch
        {
            JsonObject jsonObject => jsonObject.Any(static property =>
                property.Value is not JsonArray
                && (property.Value is null || HasBoundScopeData(property.Value))
            ),
            JsonArray => false,
            _ => true,
        };
    }

    private static bool TryGetScopeNode(
        JsonNode selectedBody,
        JsonPathExpression scopePath,
        out JsonNode? scopeNode
    )
    {
        var scopeSegments = RelationalJsonPathSupport.GetRestrictedSegments(scopePath);
        return TryNavigateRelativeNode(selectedBody, scopeSegments, out scopeNode);
    }

    internal static bool TryGetReferenceObjectNode(
        TableWritePlan tableWritePlan,
        JsonNode scopeNode,
        ReferenceDerivedValueSourceMetadata referenceSource,
        out JsonNode? referenceNode
    )
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(scopeNode);
        ArgumentNullException.ThrowIfNull(referenceSource);

        return TryNavigateRelativeNode(
            scopeNode,
            GetRelativePathWithinScope(tableWritePlan, referenceSource.ReferenceObjectPath),
            out referenceNode
        );
    }

    private static IReadOnlyList<JsonPathSegment> GetRelativePathWithinScope(
        TableWritePlan tableWritePlan,
        JsonPathExpression absolutePath
    )
    {
        var scopeSegments = RelationalJsonPathSupport
            .GetRestrictedSegments(tableWritePlan.TableModel.JsonScope)
            .ToArray();
        var absoluteSegments = RelationalJsonPathSupport.GetRestrictedSegments(absolutePath).ToArray();

        if (absoluteSegments.Length < scopeSegments.Length)
        {
            throw new InvalidOperationException(
                $"Path '{absolutePath.Canonical}' cannot be resolved relative to table scope '{tableWritePlan.TableModel.JsonScope.Canonical}'."
            );
        }

        for (var segmentIndex = 0; segmentIndex < scopeSegments.Length; segmentIndex++)
        {
            if (scopeSegments[segmentIndex] != absoluteSegments[segmentIndex])
            {
                throw new InvalidOperationException(
                    $"Path '{absolutePath.Canonical}' is not rooted under table scope '{tableWritePlan.TableModel.JsonScope.Canonical}'."
                );
            }
        }

        return absoluteSegments[scopeSegments.Length..];
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

    internal static DbColumnModel GetRequiredColumnModel(
        TableWritePlan tableWritePlan,
        DbColumnName columnName
    )
    {
        var column = tableWritePlan.TableModel.Columns.FirstOrDefault(modelColumn =>
            modelColumn.ColumnName.Equals(columnName)
        );

        if (column is not null)
        {
            return column;
        }

        throw new InvalidOperationException(
            $"Table '{FormatTable(tableWritePlan)}' does not define column '{columnName.Value}'."
        );
    }

    private static int[] AppendOrdinalPath(ReadOnlySpan<int> parentOrdinalPath, int ordinal)
    {
        var ordinalPath = new int[parentOrdinalPath.Length + 1];
        parentOrdinalPath.CopyTo(ordinalPath);
        ordinalPath[^1] = ordinal;

        return ordinalPath;
    }

    internal static object ResolveReferenceDerivedLiteralValue(
        TableWritePlan tableWritePlan,
        DbColumnModel column,
        DbColumnName columnName,
        ReferenceDerivedValueSourceMetadata referenceSource,
        string absolutePath,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        return column.Kind switch
        {
            ColumnKind.Scalar => ResolveReferenceDerivedScalarValue(
                tableWritePlan,
                column,
                columnName,
                referenceSource,
                absolutePath,
                resolvedReferenceLookups,
                ordinalPath
            ),
            ColumnKind.DescriptorFk => ResolveReferenceDerivedDescriptorValue(
                tableWritePlan,
                columnName,
                referenceSource,
                ordinalPath,
                resolvedReferenceLookups
            ),
            _ => throw new InvalidOperationException(
                $"Column '{columnName.Value}' on table '{FormatTable(tableWritePlan)}' cannot materialize {nameof(WriteValueSource.ReferenceDerived)} from unsupported column kind '{column.Kind}'."
            ),
        };
    }

    private static object ResolveReferenceDerivedScalarValue(
        TableWritePlan tableWritePlan,
        DbColumnModel column,
        DbColumnName columnName,
        ReferenceDerivedValueSourceMetadata referenceSource,
        string absolutePath,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        if (column.ScalarType is not { } scalarType)
        {
            throw new InvalidOperationException(
                $"Column '{columnName.Value}' on table '{FormatTable(tableWritePlan)}' cannot materialize {nameof(WriteValueSource.ReferenceDerived)} without scalar type metadata."
            );
        }

        var referenceIdentityValue = resolvedReferenceLookups.GetReferenceIdentityValue(
            referenceSource,
            columnName,
            ordinalPath
        );

        if (referenceIdentityValue is null)
        {
            throw CreateMissingReferenceDerivedLookupException(
                tableWritePlan,
                columnName,
                referenceSource,
                ordinalPath
            );
        }

        return ConvertRequestDerivedScalarLiteral(
            referenceIdentityValue,
            scalarType,
            CreateColumnScalarConversionContext(tableWritePlan, columnName, absolutePath),
            rawValue => $"resolved reference-derived raw value '{rawValue}' could not be converted"
        );
    }

    private static long ResolveReferenceDerivedDescriptorValue(
        TableWritePlan tableWritePlan,
        DbColumnName columnName,
        ReferenceDerivedValueSourceMetadata referenceSource,
        ReadOnlySpan<int> ordinalPath,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        var descriptorId = resolvedReferenceLookups.GetReferenceIdentityDescriptorId(
            referenceSource,
            columnName,
            ordinalPath
        );

        if (descriptorId is not null)
        {
            return descriptorId.Value;
        }

        throw CreateMissingReferenceDerivedLookupException(
            tableWritePlan,
            columnName,
            referenceSource,
            ordinalPath
        );
    }

    internal static object ConvertRequestDerivedJsonValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        RequestDerivedScalarConversionContext conversionContext
    )
    {
        var scalarLiteral = ReadRequiredRequestDerivedJsonScalarLiteral(
            jsonValue,
            scalarType,
            conversionContext
        );

        return ConvertRequestDerivedScalarLiteral(
            scalarLiteral,
            scalarType,
            conversionContext,
            _ =>
                $"encountered JSON value kind '{jsonValue.GetValueKind()}' with raw value {jsonValue.ToJsonString()}"
        );
    }

    private static string ReadRequiredRequestDerivedJsonScalarLiteral(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        RequestDerivedScalarConversionContext conversionContext
    )
    {
        return scalarType.Kind switch
        {
            ScalarKind.String => ReadRequiredJsonValue<string>(jsonValue, scalarType, conversionContext),
            ScalarKind.Int32 => ReadRequiredJsonValue<int>(jsonValue, scalarType, conversionContext)
                .ToString(CultureInfo.InvariantCulture),
            ScalarKind.Int64 => ReadRequiredJsonValue<long>(jsonValue, scalarType, conversionContext)
                .ToString(CultureInfo.InvariantCulture),
            ScalarKind.Decimal => ReadRequiredJsonValue<decimal>(jsonValue, scalarType, conversionContext)
                .ToString(CultureInfo.InvariantCulture),
            ScalarKind.Boolean => ReadRequiredJsonValue<bool>(jsonValue, scalarType, conversionContext)
                ? bool.TrueString.ToLowerInvariant()
                : bool.FalseString.ToLowerInvariant(),
            ScalarKind.Date or ScalarKind.DateTime or ScalarKind.Time => ReadRequiredJsonValue<string>(
                jsonValue,
                scalarType,
                conversionContext
            ),
            _ => throw new InvalidOperationException(
                $"Scalar kind '{scalarType.Kind}' is not supported by the relational write flattener."
            ),
        };
    }

    private static object ConvertRequestDerivedScalarLiteral(
        string scalarLiteral,
        RelationalScalarType scalarType,
        RequestDerivedScalarConversionContext conversionContext,
        Func<string, string> createInvalidLiteralReason
    )
    {
        ArgumentNullException.ThrowIfNull(scalarLiteral);
        ArgumentNullException.ThrowIfNull(scalarType);
        ArgumentNullException.ThrowIfNull(conversionContext);
        ArgumentNullException.ThrowIfNull(createInvalidLiteralReason);

        if (RelationalScalarLiteralParser.TryParse(scalarLiteral, scalarType, out var convertedValue))
        {
            return convertedValue!;
        }

        throw CreateInvalidRequestDerivedScalarException(
            conversionContext,
            scalarType,
            createInvalidLiteralReason(scalarLiteral)
        );
    }

    private static T ReadRequiredJsonValue<T>(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        RequestDerivedScalarConversionContext conversionContext
    )
        where T : notnull
    {
        if (jsonValue.TryGetValue<T>(out var value))
        {
            return value;
        }

        throw CreateInvalidRequestDerivedScalarException(
            conversionContext,
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

    private static RelationalWriteRequestValidationException CreateDuplicateSemanticIdentityException(
        TableWritePlan tableWritePlan,
        string parentScopeCanonical,
        IReadOnlyList<int> firstOrdinalPath,
        IReadOnlyList<int> duplicateOrdinalPath,
        IReadOnlyList<object?> semanticIdentityValues
    )
    {
        var duplicatePath = MaterializeConcretePath(
            tableWritePlan.TableModel.JsonScope.Canonical,
            [.. duplicateOrdinalPath]
        );

        return CreateRequestShapeValidationException(
            duplicatePath,
            $"Collection table '{FormatTable(tableWritePlan)}' received duplicate semantic identity values "
                + $"{FormatSemanticIdentityValues(semanticIdentityValues)} under parent scope '{parentScopeCanonical}'. "
                + $"First ordinal path: {FormatOrdinalPath(firstOrdinalPath)}. "
                + $"Duplicate ordinal path: {FormatOrdinalPath(duplicateOrdinalPath)}."
        );
    }

    private static RequestDerivedScalarConversionContext CreateColumnScalarConversionContext(
        TableWritePlan tableWritePlan,
        DbColumnName columnName,
        string absolutePath
    )
    {
        return new RequestDerivedScalarConversionContext(
            AbsolutePath: absolutePath,
            SubjectDescription: $"Column '{columnName.Value}' on table '{FormatTable(tableWritePlan)}'"
        );
    }

    internal static RequestDerivedScalarConversionContext CreateKeyUnificationScalarConversionContext(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan.ScalarMember member,
        string absolutePath
    )
    {
        return new RequestDerivedScalarConversionContext(
            AbsolutePath: absolutePath,
            SubjectDescription: $"Key-unification member '{member.MemberPathColumn.Value}' on table '{FormatTable(tableWritePlan)}'"
        );
    }

    internal static RelationalWriteRequestValidationException CreateInvalidRequestDerivedScalarException(
        RequestDerivedScalarConversionContext conversionContext,
        RelationalScalarType scalarType,
        string reason
    )
    {
        // Classification boundary:
        // request-shaped data issues from JSON payloads or resolved reference identity literals become
        // write-validation failures, while compiled metadata drift and unsupported plan shapes remain
        // internal invariant breaches.
        return CreateRequestShapeValidationException(
            conversionContext.AbsolutePath,
            $"{conversionContext.SubjectDescription} expected scalar kind '{scalarType.Kind}' at path "
                + $"'{conversionContext.AbsolutePath}', but {reason}."
        );
    }

    internal static RelationalWriteRequestValidationException CreateRequestShapeValidationException(
        string path,
        string message
    )
    {
        return RelationalWriteRequestValidationException.ForPath(path, message);
    }

    private static RelationalWriteRequestValidationException CreateMissingDocumentReferenceLookupException(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string wildcardPath,
        ReadOnlySpan<int> ordinalPath,
        QualifiedResourceName targetResource
    )
    {
        var concretePath = MaterializeConcretePath(wildcardPath, ordinalPath);

        return CreateRequestShapeValidationException(
            concretePath,
            $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' could not materialize document reference "
                + $"'{RelationalWriteSupport.FormatResource(targetResource)}' at path '{concretePath}' because the write request did not produce a matching resolved reference occurrence."
        );
    }

    private static InvalidOperationException CreateMissingDescriptorReferenceLookupException(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string wildcardPath,
        ReadOnlySpan<int> ordinalPath,
        QualifiedResourceName descriptorResource
    )
    {
        var concretePath = MaterializeConcretePath(wildcardPath, ordinalPath);

        return new InvalidOperationException(
            $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' had a descriptor value at path "
                + $"'{concretePath}', but the resolved lookup set did not contain a matching "
                + $"'{RelationalWriteSupport.FormatResource(descriptorResource)}' entry for ordinal path "
                + $"{FormatOrdinalPath(ordinalPath)}."
        );
    }

    private static RelationalWriteRequestValidationException CreateMissingReferenceDerivedLookupException(
        TableWritePlan tableWritePlan,
        DbColumnName columnName,
        ReferenceDerivedValueSourceMetadata referenceSource,
        ReadOnlySpan<int> ordinalPath
    )
    {
        var concreteReferenceValuePath = MaterializeConcretePath(
            referenceSource.ReferenceJsonPath.Canonical,
            ordinalPath
        );
        var concreteReferenceObjectPath = MaterializeConcretePath(
            referenceSource.ReferenceObjectPath.Canonical,
            ordinalPath
        );

        return CreateRequestShapeValidationException(
            concreteReferenceValuePath,
            $"Column '{columnName.Value}' on table '{FormatTable(tableWritePlan)}' could not materialize reference-derived value at path "
                + $"'{concreteReferenceValuePath}' because reference object '{concreteReferenceObjectPath}' did not produce a matching resolved reference occurrence."
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

    private static string FormatOrdinalPath(ReadOnlySpan<int> ordinalPath)
    {
        return $"[{string.Join(", ", ordinalPath.ToArray())}]";
    }

    internal static string GetJsonValueKind(JsonNode node)
    {
        return node switch
        {
            JsonValue jsonValue => jsonValue.GetValueKind().ToString(),
            _ => node.GetType().Name,
        };
    }

    internal static string FormatTable(TableWritePlan tableWritePlan)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        return $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";
    }

    private static string GetDescriptorAbsolutePath(
        TableWritePlan tableWritePlan,
        WriteValueSource.DescriptorReference descriptorReference
    )
    {
        return descriptorReference.DescriptorValuePath?.Canonical
            ?? RelationalJsonPathSupport.CombineRestrictedCanonical(
                tableWritePlan.TableModel.JsonScope,
                descriptorReference.RelativePath
            );
    }

    private static DocumentReferenceBinding GetDocumentReferenceBinding(
        ResourceWritePlan writePlan,
        WriteValueSource.DocumentReference documentReference
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(documentReference);

        if (
            documentReference.BindingIndex < 0
            || documentReference.BindingIndex >= writePlan.Model.DocumentReferenceBindings.Count
        )
        {
            throw new InvalidOperationException(
                $"Document-reference binding index {documentReference.BindingIndex} is out of range for resource "
                    + $"'{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}'."
            );
        }

        return writePlan.Model.DocumentReferenceBindings[documentReference.BindingIndex];
    }

    private static string MaterializeConcretePath(string wildcardPath, ReadOnlySpan<int> ordinalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wildcardPath);

        StringBuilder concretePath = new(wildcardPath.Length + ordinalPath.Length * 3);
        var ordinalIndex = 0;
        var pathIndex = 0;

        while (pathIndex < wildcardPath.Length)
        {
            if (
                pathIndex <= wildcardPath.Length - 3
                && wildcardPath[pathIndex] == '['
                && wildcardPath[pathIndex + 1] == '*'
                && wildcardPath[pathIndex + 2] == ']'
            )
            {
                if (ordinalIndex >= ordinalPath.Length)
                {
                    throw new InvalidOperationException(
                        $"Path '{wildcardPath}' requires more ordinal components than were supplied."
                    );
                }

                concretePath.Append('[').Append(ordinalPath[ordinalIndex]).Append(']');
                ordinalIndex++;
                pathIndex += 3;
                continue;
            }

            concretePath.Append(wildcardPath[pathIndex]);
            pathIndex++;
        }

        if (ordinalIndex != ordinalPath.Length)
        {
            throw new InvalidOperationException(
                $"Path '{wildcardPath}' received {ordinalPath.Length} ordinal components, but only {ordinalIndex} wildcards were available."
            );
        }

        return concretePath.ToString();
    }

    internal static string MaterializeValidationPath(
        string wildcardOrConcretePath,
        ReadOnlySpan<int> ordinalPath
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wildcardOrConcretePath);

        return ordinalPath.Length > 0 && wildcardOrConcretePath.Contains("[*]", StringComparison.Ordinal)
            ? MaterializeConcretePath(wildcardOrConcretePath, ordinalPath)
            : wildcardOrConcretePath;
    }

    internal sealed record RequestDerivedScalarConversionContext(
        string AbsolutePath,
        string SubjectDescription
    );

    private sealed record CollectionChildPlan(
        TableWritePlan TableWritePlan,
        ImmutableArray<JsonPathSegment> RelativeScopeSegments
    );

    private sealed record AttachedAlignedScopePlan(
        TableWritePlan TableWritePlan,
        ImmutableArray<JsonPathSegment> RelativeScopeSegments,
        bool NavigateFromRoot
    );

    private sealed record TraversalPlans(
        IReadOnlyDictionary<string, IReadOnlyList<CollectionChildPlan>> CollectionChildPlansByParentScope,
        IReadOnlyDictionary<
            string,
            IReadOnlyList<AttachedAlignedScopePlan>
        > AttachedAlignedScopePlansByParentScope
    );

    private sealed record CollectionScopeInstance(int RequestOrder, JsonObject ScopeNode);
}
