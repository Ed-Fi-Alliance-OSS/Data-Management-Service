// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Reconstitutes a JSON document from hydrated database rows by reversing the
/// flattening operation. Given a root document identity and the complete set of
/// hydrated table rows (root + child), produces a <see cref="JsonNode"/> tree
/// that matches the original API resource shape.
/// </summary>
public static class DocumentReconstituter
{
    /// <summary>
    /// Reorders an existing JSON document into the deterministic member order defined by the
    /// compiled read plan.
    /// </summary>
    public static JsonNode ReorderToReadPlanOrder(JsonNode document, ResourceReadPlan readPlan)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(readPlan);

        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);

        return ReorderNode(document, compiledPlan.PropertyOrder);
    }

    /// <summary>
    /// Reconstitutes a single JSON document from hydrated row data using cached compiled metadata
    /// derived from the supplied read plan.
    /// </summary>
    public static JsonNode Reconstitute(
        long documentId,
        ResourceReadPlan readPlan,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        ArgumentNullException.ThrowIfNull(readPlan);
        ArgumentNullException.ThrowIfNull(tableRowsInDependencyOrder);
        ArgumentNullException.ThrowIfNull(descriptorUriLookup);

        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);

        return Reconstitute(documentId, tableRowsInDependencyOrder, compiledPlan, descriptorUriLookup);
    }

    /// <summary>
    /// Reconstitutes a single JSON document from hydrated row data.
    /// </summary>
    /// <param name="documentId">The root document identity to reconstitute.</param>
    /// <param name="tableRowsInDependencyOrder">
    /// Hydrated rows per table in dependency order (root first, then children).
    /// </param>
    /// <param name="referenceProjectionPlans">
    /// Reference identity projection metadata for emitting reference objects.
    /// </param>
    /// <param name="descriptorProjectionSources">
    /// Descriptor edge sources for emitting descriptor URI values.
    /// </param>
    /// <param name="descriptorUriLookup">
    /// Resolved <c>DescriptorId → URI</c> lookup for descriptor reconstitution.
    /// </param>
    /// <returns>A <see cref="JsonNode"/> representing the reconstituted JSON document.</returns>
    public static JsonNode Reconstitute(
        long documentId,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referenceProjectionPlans,
        IReadOnlyList<DescriptorEdgeSource> descriptorProjectionSources,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        ArgumentNullException.ThrowIfNull(tableRowsInDependencyOrder);
        ArgumentNullException.ThrowIfNull(referenceProjectionPlans);
        ArgumentNullException.ThrowIfNull(descriptorProjectionSources);
        ArgumentNullException.ThrowIfNull(descriptorUriLookup);

        if (tableRowsInDependencyOrder.Count == 0)
        {
            throw new InvalidOperationException("Cannot reconstitute document: no table rows provided.");
        }

        // Phase 1: Find root table and root row, emit root scalars
        var rootTableRows = tableRowsInDependencyOrder[0];
        var rootTableModel = rootTableRows.TableModel;
        var rootRow = FindRootRow(documentId, rootTableRows);
        var reconstitutionContext = new ReconstitutionContext(tableRowsInDependencyOrder);

        var result = new JsonObject();

        EmitScalars(result, rootRow, rootTableModel);

        // Phase 2: Emit references on root
        EmitReferences(result, rootRow, rootTableModel, referenceProjectionPlans);

        // Phase 3: Emit descriptors on root
        EmitDescriptors(result, rootRow, rootTableModel, descriptorProjectionSources, descriptorUriLookup);

        // Phase 4: Process child tables (collections, extensions, nested)
        if (tableRowsInDependencyOrder.Count > 1)
        {
            EmitChildScopes(
                result,
                documentId,
                null,
                rootTableModel,
                reconstitutionContext,
                referenceProjectionPlans,
                descriptorProjectionSources,
                descriptorUriLookup
            );
        }

        List<DbTableModel> tableModelsInDependencyOrder = [];

        foreach (var tableRows in tableRowsInDependencyOrder)
        {
            tableModelsInDependencyOrder.Add(tableRows.TableModel);
        }

        return ReorderNode(
            result,
            BuildPropertyOrderTree(
                tableModelsInDependencyOrder,
                referenceProjectionPlans,
                descriptorProjectionSources
            )
        );
    }

    private static JsonNode Reconstitute(
        long documentId,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        ArgumentNullException.ThrowIfNull(tableRowsInDependencyOrder);
        ArgumentNullException.ThrowIfNull(compiledPlan);
        ArgumentNullException.ThrowIfNull(descriptorUriLookup);

        if (tableRowsInDependencyOrder.Count == 0)
        {
            throw new InvalidOperationException("Cannot reconstitute document: no table rows provided.");
        }

        var rootTableRows = tableRowsInDependencyOrder[0];
        var rootTablePlan = compiledPlan.GetTablePlanOrThrow(rootTableRows.TableModel.Table);
        var rootRow = FindRootRow(documentId, rootTableRows, rootTablePlan);
        var reconstitutionContext = new ReconstitutionContext(tableRowsInDependencyOrder, compiledPlan);

        var result = new JsonObject();

        EmitScalars(result, rootRow, rootTablePlan.TableModel);
        EmitReferences(result, rootRow, rootTablePlan);
        EmitDescriptors(result, rootRow, rootTablePlan, descriptorUriLookup);

        if (tableRowsInDependencyOrder.Count > 1)
        {
            EmitChildScopes(
                result,
                documentId,
                null,
                rootTablePlan,
                reconstitutionContext,
                compiledPlan,
                descriptorUriLookup
            );
        }

        return ReorderNode(result, compiledPlan.PropertyOrder);
    }

    /// <summary>
    /// Finds the root row matching the given document id.
    /// </summary>
    private static object?[] FindRootRow(long documentId, HydratedTableRows rootTableRows)
    {
        var rootScopeLocatorColumns = rootTableRows.TableModel.IdentityMetadata.RootScopeLocatorColumns;
        if (rootScopeLocatorColumns.Count != 1)
        {
            throw new InvalidOperationException(
                "Cannot reconstitute document: expected exactly one RootScopeLocatorColumn on the root table model, "
                    + $"but found {rootScopeLocatorColumns.Count}."
            );
        }

        var locatorColumnName = rootScopeLocatorColumns[0];
        var locatorOrdinal = FindColumnOrdinalByName(rootTableRows.TableModel, locatorColumnName);

        foreach (var row in rootTableRows.Rows)
        {
            if (Convert.ToInt64(row[locatorOrdinal]) == documentId)
            {
                return row;
            }
        }

        throw new InvalidOperationException(
            $"Cannot reconstitute document: no root row found for DocumentId {documentId}."
        );
    }

    private static object?[] FindRootRow(
        long documentId,
        HydratedTableRows rootTableRows,
        TableReconstitutionPlan rootTablePlan
    )
    {
        var locatorOrdinal = rootTablePlan.ResolveSingleRootScopeLocatorOrdinalOrThrow();

        foreach (var row in rootTableRows.Rows)
        {
            if (Convert.ToInt64(row[locatorOrdinal]) == documentId)
            {
                return row;
            }
        }

        throw new InvalidOperationException(
            $"Cannot reconstitute document: no root row found for DocumentId {documentId}."
        );
    }

    /// <summary>
    /// Emits scalar values from a row into the target JSON object.
    /// Only emits non-null scalars whose <see cref="DbColumnModel.SourceJsonPath"/> is defined.
    /// </summary>
    private static void EmitScalars(JsonObject target, object?[] row, DbTableModel tableModel)
    {
        for (var i = 0; i < tableModel.Columns.Count; i++)
        {
            var column = tableModel.Columns[i];

            if (column.Kind != ColumnKind.Scalar)
            {
                continue;
            }

            if (column.SourceJsonPath is null)
            {
                continue;
            }

            var value = row[i];
            if (value is null)
            {
                continue;
            }

            var (targetObject, propertyName) = ResolvePathRelativeToScope(
                target,
                column.SourceJsonPath.Value,
                tableModel.JsonScope
            );
            targetObject[propertyName] = ConvertToJsonValue(value, column.ScalarType);
        }
    }

    /// <summary>
    /// Emits reference objects from the root row.
    /// </summary>
    private static void EmitReferences(
        JsonObject target,
        object?[] row,
        DbTableModel tableModel,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referenceProjectionPlans
    )
    {
        foreach (var plan in referenceProjectionPlans)
        {
            if (!plan.Table.Equals(tableModel.Table))
            {
                continue;
            }

            foreach (var binding in plan.BindingsInOrder)
            {
                var projectionResult = ReferenceIdentityProjector.Project(row, binding);
                if (projectionResult is ReferenceProjectionResult.Present present)
                {
                    EmitReferenceObject(target, present, tableModel.JsonScope);
                }
            }
        }
    }

    private static void EmitReferences(JsonObject target, object?[] row, TableReconstitutionPlan tablePlan)
    {
        foreach (var binding in tablePlan.ReferenceBindingsInOrder)
        {
            var projectionResult = ReferenceIdentityProjector.Project(row, binding);
            if (projectionResult is ReferenceProjectionResult.Present present)
            {
                EmitReferenceObject(target, present, tablePlan.TableModel.JsonScope);
            }
        }
    }

    /// <summary>
    /// Emits a single reference object into the target JSON object.
    /// </summary>
    private static void EmitReferenceObject(
        JsonObject target,
        ReferenceProjectionResult.Present present,
        JsonPathExpression scope
    )
    {
        var (referenceParent, referencePropertyName) = ResolvePathRelativeToScope(
            target,
            present.ReferenceObjectPath,
            scope
        );
        var referenceObject = new JsonObject();

        foreach (var field in present.FieldsInOrder)
        {
            var (fieldParent, fieldPropertyName) = ResolvePathRelativeToScope(
                referenceObject,
                field.ReferenceJsonPath,
                present.ReferenceObjectPath
            );
            fieldParent[fieldPropertyName] = ConvertToJsonValue(field.Value);
        }

        referenceParent[referencePropertyName] = referenceObject;
    }

    /// <summary>
    /// Emits descriptor URI values from the row.
    /// </summary>
    private static void EmitDescriptors(
        JsonObject target,
        object?[] row,
        DbTableModel tableModel,
        IReadOnlyList<DescriptorEdgeSource> descriptorSources,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        foreach (var source in descriptorSources)
        {
            if (!source.Table.Equals(tableModel.Table))
            {
                continue;
            }

            var fkOrdinal = FindColumnOrdinalByName(tableModel, source.FkColumn);
            var descriptorIdValue = row[fkOrdinal];
            if (descriptorIdValue is null)
            {
                continue;
            }

            var descriptorId = Convert.ToInt64(descriptorIdValue);
            if (!descriptorUriLookup.TryGetValue(descriptorId, out var uri))
            {
                throw new InvalidOperationException(
                    $"Descriptor ID {descriptorId} in column '{source.FkColumn}' of table '{tableModel.Table}' "
                        + "has no resolved URI in the descriptor lookup. "
                        + "This indicates a descriptor projection plan or executor defect."
                );
            }

            var (targetObject, propertyName) = ResolvePathRelativeToScope(
                target,
                source.DescriptorValuePath,
                tableModel.JsonScope
            );
            targetObject[propertyName] = JsonValue.Create(uri);
        }
    }

    private static void EmitDescriptors(
        JsonObject target,
        object?[] row,
        TableReconstitutionPlan tablePlan,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        foreach (var binding in tablePlan.DescriptorBindingsInOrder)
        {
            var descriptorIdValue = row[binding.DescriptorIdColumnOrdinal];
            if (descriptorIdValue is null)
            {
                continue;
            }

            var descriptorId = Convert.ToInt64(descriptorIdValue);
            if (!descriptorUriLookup.TryGetValue(descriptorId, out var uri))
            {
                throw new InvalidOperationException(
                    $"Descriptor ID {descriptorId} at ordinal '{binding.DescriptorIdColumnOrdinal}' of table '{tablePlan.Table}' "
                        + "has no resolved URI in the descriptor lookup. "
                        + "This indicates a descriptor projection plan or executor defect."
                );
            }

            var (targetObject, propertyName) = ResolvePathRelativeToScope(
                target,
                binding.DescriptorValuePath,
                tablePlan.TableModel.JsonScope
            );
            targetObject[propertyName] = JsonValue.Create(uri);
        }
    }

    /// <summary>
    /// Processes child tables (collections, extensions, nested) and emits them into the parent scope.
    /// </summary>
    private static void EmitChildScopes(
        JsonObject parentObject,
        long documentId,
        long? parentCollectionItemId,
        DbTableModel parentTableModel,
        ReconstitutionContext reconstitutionContext,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referenceProjectionPlans,
        IReadOnlyList<DescriptorEdgeSource> descriptorProjectionSources,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        var immediateChildren = FindImmediateChildTables(
            parentTableModel,
            reconstitutionContext.TableRowsInDependencyOrder
        );

        foreach (var childTableRows in immediateChildren)
        {
            var childTableModel = childTableRows.TableModel;
            var childKind = childTableModel.IdentityMetadata.TableKind;

            switch (childKind)
            {
                case DbTableKind.Collection:
                case DbTableKind.ExtensionCollection:
                    EmitCollectionArray(
                        parentObject,
                        documentId,
                        parentCollectionItemId,
                        parentTableModel,
                        childTableRows,
                        reconstitutionContext,
                        referenceProjectionPlans,
                        descriptorProjectionSources,
                        descriptorUriLookup
                    );
                    break;

                case DbTableKind.RootExtension:
                case DbTableKind.CollectionExtensionScope:
                    EmitExtensionScope(
                        parentObject,
                        documentId,
                        parentCollectionItemId,
                        parentTableModel,
                        childTableRows,
                        reconstitutionContext,
                        referenceProjectionPlans,
                        descriptorProjectionSources,
                        descriptorUriLookup
                    );
                    break;
            }
        }
    }

    private static void EmitChildScopes(
        JsonObject parentObject,
        long documentId,
        long? parentCollectionItemId,
        TableReconstitutionPlan parentTablePlan,
        ReconstitutionContext reconstitutionContext,
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        var immediateChildren = FindImmediateChildTables(
            parentTablePlan.TableModel,
            reconstitutionContext.TableRowsInDependencyOrder
        );

        foreach (var childTableRows in immediateChildren)
        {
            var childTablePlan = compiledPlan.GetTablePlanOrThrow(childTableRows.TableModel.Table);
            var childKind = childTablePlan.TableModel.IdentityMetadata.TableKind;

            switch (childKind)
            {
                case DbTableKind.Collection:
                case DbTableKind.ExtensionCollection:
                    EmitCollectionArray(
                        parentObject,
                        documentId,
                        parentCollectionItemId,
                        parentTablePlan,
                        childTableRows,
                        childTablePlan,
                        reconstitutionContext,
                        compiledPlan,
                        descriptorUriLookup
                    );
                    break;

                case DbTableKind.RootExtension:
                case DbTableKind.CollectionExtensionScope:
                    EmitExtensionScope(
                        parentObject,
                        documentId,
                        parentCollectionItemId,
                        parentTablePlan,
                        childTableRows,
                        childTablePlan,
                        reconstitutionContext,
                        compiledPlan,
                        descriptorUriLookup
                    );
                    break;
            }
        }
    }

    /// <summary>
    /// Emits a collection array property from child table rows.
    /// </summary>
    private static void EmitCollectionArray(
        JsonObject parentObject,
        long documentId,
        long? parentCollectionItemId,
        DbTableModel parentTableModel,
        HydratedTableRows childTableRows,
        ReconstitutionContext reconstitutionContext,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referenceProjectionPlans,
        IReadOnlyList<DescriptorEdgeSource> descriptorProjectionSources,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        var childTableModel = childTableRows.TableModel;

        var matchingRows = FilterChildRows(
            documentId,
            parentCollectionItemId,
            parentTableModel,
            childTableRows,
            reconstitutionContext
        );

        if (matchingRows.Count == 0)
        {
            return;
        }

        var (collectionTarget, collectionPropertyName) = ResolveCollectionTarget(
            parentObject,
            childTableModel.JsonScope,
            parentTableModel.JsonScope
        );

        var array = new JsonArray();

        foreach (var childRow in matchingRows)
        {
            var itemObject = new JsonObject();

            // Emit scalars
            EmitScalars(itemObject, childRow, childTableModel);

            // Emit references on collection items
            EmitReferences(itemObject, childRow, childTableModel, referenceProjectionPlans);

            // Emit descriptors on collection items
            EmitDescriptors(
                itemObject,
                childRow,
                childTableModel,
                descriptorProjectionSources,
                descriptorUriLookup
            );

            // Find CollectionKey for nested children
            var collectionKeyOrdinal = FindColumnOrdinalByKind(childTableModel, ColumnKind.CollectionKey);
            if (collectionKeyOrdinal >= 0)
            {
                var collectionItemId = Convert.ToInt64(childRow[collectionKeyOrdinal]);

                // Recurse for nested child tables
                EmitChildScopes(
                    itemObject,
                    documentId,
                    collectionItemId,
                    childTableModel,
                    reconstitutionContext,
                    referenceProjectionPlans,
                    descriptorProjectionSources,
                    descriptorUriLookup
                );
            }

            array.Add(itemObject);
        }

        // For extension collections, the parent is an _ext.projectName scope,
        // so the property needs to land on the correct parent
        collectionTarget[collectionPropertyName] = array;
    }

    private static void EmitCollectionArray(
        JsonObject parentObject,
        long documentId,
        long? parentCollectionItemId,
        TableReconstitutionPlan parentTablePlan,
        HydratedTableRows childTableRows,
        TableReconstitutionPlan childTablePlan,
        ReconstitutionContext reconstitutionContext,
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        var childTableModel = childTablePlan.TableModel;

        var matchingRows = FilterChildRows(
            documentId,
            parentCollectionItemId,
            parentTablePlan.TableModel,
            childTableRows,
            reconstitutionContext
        );

        if (matchingRows.Count == 0)
        {
            return;
        }

        var (collectionTarget, collectionPropertyName) = ResolveCollectionTarget(
            parentObject,
            childTableModel.JsonScope,
            parentTablePlan.TableModel.JsonScope
        );

        var array = new JsonArray();

        foreach (var childRow in matchingRows)
        {
            var itemObject = new JsonObject();

            EmitScalars(itemObject, childRow, childTableModel);
            EmitReferences(itemObject, childRow, childTablePlan);
            EmitDescriptors(itemObject, childRow, childTablePlan, descriptorUriLookup);

            var collectionKeyOrdinal = FindColumnOrdinalByKind(childTableModel, ColumnKind.CollectionKey);
            if (collectionKeyOrdinal >= 0)
            {
                var collectionItemId = Convert.ToInt64(childRow[collectionKeyOrdinal]);

                EmitChildScopes(
                    itemObject,
                    documentId,
                    collectionItemId,
                    childTablePlan,
                    reconstitutionContext,
                    compiledPlan,
                    descriptorUriLookup
                );
            }

            array.Add(itemObject);
        }

        collectionTarget[collectionPropertyName] = array;
    }

    /// <summary>
    /// Emits an extension scope (_ext.projectName) from a root extension or collection extension scope table.
    /// </summary>
    private static void EmitExtensionScope(
        JsonObject parentObject,
        long documentId,
        long? parentCollectionItemId,
        DbTableModel parentTableModel,
        HydratedTableRows childTableRows,
        ReconstitutionContext reconstitutionContext,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referenceProjectionPlans,
        IReadOnlyList<DescriptorEdgeSource> descriptorProjectionSources,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        var childTableModel = childTableRows.TableModel;

        var matchingRows = FilterChildRows(
            documentId,
            parentCollectionItemId,
            parentTableModel,
            childTableRows,
            reconstitutionContext
        );

        if (matchingRows.Count == 0)
        {
            return;
        }

        // Parse the extension scope to get _ext and project name
        var (extPropertyName, projectName) = ParseExtensionScope(
            childTableModel.JsonScope,
            parentTableModel.JsonScope
        );

        // Extension scope rows are 1:1 with the parent (for root extensions) or
        // 1:1 with each collection item (for collection extension scopes)
        var extensionRow = matchingRows[0];
        var projectObject = new JsonObject();

        // Emit scalars
        EmitScalars(projectObject, extensionRow, childTableModel);

        // Emit references
        EmitReferences(projectObject, extensionRow, childTableModel, referenceProjectionPlans);

        // Emit descriptors
        EmitDescriptors(
            projectObject,
            extensionRow,
            childTableModel,
            descriptorProjectionSources,
            descriptorUriLookup
        );

        // Recurse for extension collection children.
        // Use the extension scope's own CollectionKey if it has one; otherwise
        // fall back to parentCollectionItemId. CollectionExtensionScope tables
        // use BaseCollectionItemId (a ParentKeyPart, not CollectionKey) as their
        // row identity, which equals the parent collection item's key.
        var collectionKeyOrdinal = FindColumnOrdinalByKind(childTableModel, ColumnKind.CollectionKey);
        long? extensionScopeId = parentCollectionItemId;
        if (collectionKeyOrdinal >= 0 && extensionRow[collectionKeyOrdinal] is not null)
        {
            extensionScopeId = Convert.ToInt64(extensionRow[collectionKeyOrdinal]);
        }

        EmitChildScopes(
            projectObject,
            documentId,
            extensionScopeId,
            childTableModel,
            reconstitutionContext,
            referenceProjectionPlans,
            descriptorProjectionSources,
            descriptorUriLookup
        );

        if (!HasMeaningfulContent(projectObject))
        {
            return;
        }

        if (parentObject[extPropertyName] is not JsonObject extObject)
        {
            extObject = new JsonObject();
            parentObject[extPropertyName] = extObject;
        }

        extObject[projectName] = projectObject;
    }

    private static void EmitExtensionScope(
        JsonObject parentObject,
        long documentId,
        long? parentCollectionItemId,
        TableReconstitutionPlan parentTablePlan,
        HydratedTableRows childTableRows,
        TableReconstitutionPlan childTablePlan,
        ReconstitutionContext reconstitutionContext,
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        var childTableModel = childTablePlan.TableModel;

        var matchingRows = FilterChildRows(
            documentId,
            parentCollectionItemId,
            parentTablePlan.TableModel,
            childTableRows,
            reconstitutionContext
        );

        if (matchingRows.Count == 0)
        {
            return;
        }

        var (extPropertyName, projectName) = ParseExtensionScope(
            childTableModel.JsonScope,
            parentTablePlan.TableModel.JsonScope
        );

        var extensionRow = matchingRows[0];
        var projectObject = new JsonObject();

        EmitScalars(projectObject, extensionRow, childTableModel);
        EmitReferences(projectObject, extensionRow, childTablePlan);
        EmitDescriptors(projectObject, extensionRow, childTablePlan, descriptorUriLookup);

        var collectionKeyOrdinal = FindColumnOrdinalByKind(childTableModel, ColumnKind.CollectionKey);
        long? extensionScopeId = parentCollectionItemId;

        if (collectionKeyOrdinal >= 0 && extensionRow[collectionKeyOrdinal] is not null)
        {
            extensionScopeId = Convert.ToInt64(extensionRow[collectionKeyOrdinal]);
        }

        EmitChildScopes(
            projectObject,
            documentId,
            extensionScopeId,
            childTablePlan,
            reconstitutionContext,
            compiledPlan,
            descriptorUriLookup
        );

        if (!HasMeaningfulContent(projectObject))
        {
            return;
        }

        if (parentObject[extPropertyName] is not JsonObject extObject)
        {
            extObject = new JsonObject();
            parentObject[extPropertyName] = extObject;
        }

        extObject[projectName] = projectObject;
    }

    /// <summary>
    /// Filters child rows to those matching the given parent scope.
    /// </summary>
    private static IReadOnlyList<object?[]> FilterChildRows(
        long documentId,
        long? parentCollectionItemId,
        DbTableModel parentTableModel,
        HydratedTableRows childTableRows,
        ReconstitutionContext reconstitutionContext
    )
    {
        // For root children, the parent scope locator is DocumentId
        // For nested children, the parent scope locator is the parent's CollectionItemId
        long matchValue;

        if (
            parentCollectionItemId.HasValue
            && parentTableModel.IdentityMetadata.TableKind != DbTableKind.Root
        )
        {
            matchValue = parentCollectionItemId.Value;
        }
        else
        {
            matchValue = documentId;
        }

        return reconstitutionContext.GetRowsByParentLocator(childTableRows, matchValue);
    }

    /// <summary>
    /// Finds immediate child tables of the given parent table in the dependency-ordered list.
    /// </summary>
    internal static IReadOnlyList<HydratedTableRows> FindImmediateChildTables(
        DbTableModel parentTableModel,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder
    )
    {
        List<HydratedTableRows> children = [];
        var parentScope = parentTableModel.JsonScope;

        foreach (var tableRows in tableRowsInDependencyOrder)
        {
            var childTableModel = tableRows.TableModel;
            if (childTableModel.Table.Equals(parentTableModel.Table))
            {
                continue;
            }

            var childScope = childTableModel.JsonScope;

            if (!IsScopePrefix(parentScope, childScope))
            {
                continue;
            }

            if (!IsImmediateChild(parentScope, childScope, childTableModel.IdentityMetadata.TableKind))
            {
                continue;
            }

            children.Add(tableRows);
        }

        return children;
    }

    /// <summary>
    /// Determines whether a child scope is an immediate child of the parent scope.
    /// </summary>
    private static bool IsImmediateChild(
        JsonPathExpression parentScope,
        JsonPathExpression childScope,
        DbTableKind childKind
    )
    {
        if (!IsScopePrefix(parentScope, childScope))
        {
            return false;
        }

        var relativeSegments = GetRelativeScopeSegments(parentScope, childScope);

        return childKind switch
        {
            // Collections: one [*] deeper than parent
            // e.g., parent "$" -> child "$.addresses[*]"
            // e.g., parent "$.addresses[*]" -> child "$.addresses[*].periods[*]"
            DbTableKind.Collection => IsOneArrayLevelDeeper(relativeSegments),

            // Extension collections must recurse through their owning _ext.projectName scope.
            // Without this guard, a base scope like "$.addresses[*]" incorrectly sees
            // "$.addresses[*]._ext.sample.deliveryNotes[*]" as a direct child.
            DbTableKind.ExtensionCollection => !StartsWithExtensionScope(relativeSegments)
                && IsOneArrayLevelDeeper(relativeSegments),

            // Extensions: _ext.projectName at same array depth
            // e.g., parent "$" -> child "$._ext.sample"
            // e.g., parent "$.addresses[*]" -> child "$.addresses[*]._ext.sample"
            DbTableKind.RootExtension or DbTableKind.CollectionExtensionScope => IsExtensionScope(
                relativeSegments
            ),

            _ => false,
        };
    }

    /// <summary>
    /// Returns true if the suffix represents exactly one array level deeper.
    /// Allows intermediate property segments from inlined object paths
    /// (e.g., "$" -> "$.contentStandard.authors[*]").
    /// </summary>
    private static bool IsOneArrayLevelDeeper(IReadOnlyList<JsonPathSegment> relativeSegments)
    {
        if (relativeSegments.Count == 0 || relativeSegments[^1] is not JsonPathSegment.AnyArrayElement)
        {
            return false;
        }

        var count = 0;
        foreach (var segment in relativeSegments)
        {
            if (segment is JsonPathSegment.AnyArrayElement)
            {
                count++;
            }
        }

        return count == 1;
    }

    /// <summary>
    /// Returns true if the suffix represents an extension scope (_ext.projectName).
    /// </summary>
    private static bool IsExtensionScope(IReadOnlyList<JsonPathSegment> relativeSegments)
    {
        return relativeSegments.Count == 2
            && relativeSegments[0] is JsonPathSegment.Property { Name: "_ext" }
            && relativeSegments[1] is JsonPathSegment.Property { Name.Length: > 0 };
    }

    private static bool StartsWithExtensionScope(IReadOnlyList<JsonPathSegment> relativeSegments) =>
        relativeSegments.Count >= 2
        && relativeSegments[0] is JsonPathSegment.Property { Name: "_ext" }
        && relativeSegments[1] is JsonPathSegment.Property { Name.Length: > 0 };

    private static JsonPathSegment[] GetRelativeScopeSegments(
        JsonPathExpression parentScope,
        JsonPathExpression childScope
    )
    {
        var parentSegments = GetRestrictedSegments(parentScope);
        var childSegments = GetRestrictedSegments(childScope);

        return [.. childSegments.Skip(parentSegments.Count)];
    }

    private static bool IsScopePrefix(JsonPathExpression parentScope, JsonPathExpression childScope)
    {
        var parentSegments = GetRestrictedSegments(parentScope);
        var childSegments = GetRestrictedSegments(childScope);

        if (parentSegments.Count > childSegments.Count)
        {
            return false;
        }

        for (var index = 0; index < parentSegments.Count; index++)
        {
            var parentSegment = parentSegments[index];
            var childSegment = childSegments[index];

            if (parentSegment.GetType() != childSegment.GetType())
            {
                return false;
            }

            if (
                parentSegment is JsonPathSegment.Property parentProperty
                && childSegment is JsonPathSegment.Property childProperty
                && !string.Equals(parentProperty.Name, childProperty.Name, StringComparison.Ordinal)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<JsonPathSegment> GetRestrictedSegments(JsonPathExpression path)
    {
        if (path.Canonical == "$")
        {
            return [];
        }

        if (path.Segments.Count > 0)
        {
            return path.Segments;
        }

        return ParseRestrictedCanonical(path.Canonical);
    }

    private static JsonPathSegment[] ParseRestrictedCanonical(string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath) || canonicalPath[0] != '$')
        {
            throw new InvalidOperationException(
                $"Restricted JSONPath '{canonicalPath}' must start with '$'."
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
                        $"Restricted JSONPath '{canonicalPath}' contains an unsupported segment."
                    );
            }
        }

        return [.. segments];
    }

    private static int AppendProperty(string path, int dotIndex, ICollection<JsonPathSegment> segments)
    {
        var startIndex = dotIndex + 1;
        var index = startIndex;

        while (index < path.Length && path[index] is not ('.' or '['))
        {
            index++;
        }

        if (index == startIndex)
        {
            throw new InvalidOperationException(
                $"Restricted JSONPath '{path}' contains an empty property segment."
            );
        }

        segments.Add(new JsonPathSegment.Property(path[startIndex..index]));

        return index;
    }

    private static bool IsArrayWildcard(string path, int openBracketIndex) =>
        openBracketIndex + 2 < path.Length
        && path[openBracketIndex] == '['
        && path[openBracketIndex + 1] == '*'
        && path[openBracketIndex + 2] == ']';

    /// <summary>
    /// Resolves the target JSON object and property name for a collection array, navigating
    /// through intermediate inlined-object segments and creating them as needed.
    /// e.g., parent "$", child "$.addresses[*]" => (rootObject, "addresses")
    /// e.g., parent "$", child "$.contentStandard.authors[*]" => (contentStandardObject, "authors")
    /// </summary>
    private static (JsonObject TargetObject, string PropertyName) ResolveCollectionTarget(
        JsonObject scopeObject,
        JsonPathExpression collectionScope,
        JsonPathExpression parentScope
    )
    {
        var startIndex = parentScope.Segments.Count;
        var target = scopeObject;

        for (var i = startIndex; i < collectionScope.Segments.Count; i++)
        {
            if (collectionScope.Segments[i] is not JsonPathSegment.Property prop)
            {
                continue;
            }

            // If the next segment is AnyArrayElement, this property is the collection name
            if (
                i + 1 < collectionScope.Segments.Count
                && collectionScope.Segments[i + 1] is JsonPathSegment.AnyArrayElement
            )
            {
                return (target, prop.Name);
            }

            // Otherwise this is an intermediate inlined-object segment — navigate or create
            if (target[prop.Name] is not JsonObject child)
            {
                child = new JsonObject();
                target[prop.Name] = child;
            }

            target = child;
        }

        throw new InvalidOperationException(
            $"Cannot resolve collection target from child scope '{collectionScope.Canonical}' "
                + $"relative to parent scope '{parentScope.Canonical}'."
        );
    }

    /// <summary>
    /// Parses an extension scope to extract the _ext property name and project name.
    /// e.g., parent "$", child "$._ext.sample" => ("_ext", "sample")
    /// </summary>
    private static (string ExtPropertyName, string ProjectName) ParseExtensionScope(
        JsonPathExpression childScope,
        JsonPathExpression parentScope
    )
    {
        var parentSegmentCount = parentScope.Segments.Count;
        string? extName = null;
        string? projectName = null;

        for (var i = parentSegmentCount; i < childScope.Segments.Count; i++)
        {
            if (childScope.Segments[i] is JsonPathSegment.Property prop)
            {
                if (extName is null)
                {
                    extName = prop.Name;
                }
                else
                {
                    projectName = prop.Name;
                    break;
                }
            }
        }

        if (extName is null || projectName is null)
        {
            throw new InvalidOperationException(
                $"Cannot parse extension scope from '{childScope.Canonical}' "
                    + $"relative to parent scope '{parentScope.Canonical}'."
            );
        }

        return (extName, projectName);
    }

    /// <summary>
    /// Given a full JSON path and the owning table/scope path, resolves the correct
    /// target JSON object and property name. Creates intermediate JSON objects for any
    /// inlined nested property segments between the scope and the target property.
    /// <para>
    /// For example, with scope <c>$</c> and path <c>$.contentStandard.beginDate</c>,
    /// this creates the <c>contentStandard</c> intermediate object and returns it with
    /// property name <c>beginDate</c>.
    /// </para>
    /// </summary>
    private static (JsonObject TargetObject, string PropertyName) ResolvePathRelativeToScope(
        JsonObject scopeObject,
        JsonPathExpression fullPath,
        JsonPathExpression scopePath
    )
    {
        var startIndex = scopePath.Segments.Count;
        var target = scopeObject;

        // Walk intermediate property segments, creating nested objects as needed
        for (var i = startIndex; i < fullPath.Segments.Count - 1; i++)
        {
            if (fullPath.Segments[i] is JsonPathSegment.Property prop)
            {
                if (target[prop.Name] is not JsonObject child)
                {
                    child = new JsonObject();
                    target[prop.Name] = child;
                }

                target = child;
            }
        }

        // The last segment is the property name
        if (fullPath.Segments[^1] is not JsonPathSegment.Property lastProp)
        {
            throw new InvalidOperationException(
                $"Expected last segment of '{fullPath.Canonical}' to be a property."
            );
        }

        return (target, lastProp.Name);
    }

    /// <summary>
    /// Builds a deterministic member-order tree from the compiled JSON paths that participate in
    /// scalar, reference, descriptor, collection, and extension reconstitution.
    /// </summary>
    private static PropertyOrderNode BuildPropertyOrderTree(
        IReadOnlyList<DbTableModel> tableModelsInDependencyOrder,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referenceProjectionPlans,
        IReadOnlyList<DescriptorEdgeSource> descriptorProjectionSources
    )
    {
        List<JsonPathExpression> orderedPaths = [];

        foreach (var tableModel in tableModelsInDependencyOrder)
        {
            if (!string.Equals(tableModel.JsonScope.Canonical, "$", StringComparison.Ordinal))
            {
                orderedPaths.Add(tableModel.JsonScope);
            }

            foreach (var column in tableModel.Columns)
            {
                if (column.SourceJsonPath is JsonPathExpression sourcePath)
                {
                    orderedPaths.Add(sourcePath);
                }
            }
        }

        foreach (var plan in referenceProjectionPlans)
        {
            foreach (var binding in plan.BindingsInOrder)
            {
                orderedPaths.Add(binding.ReferenceObjectPath);

                foreach (var field in binding.IdentityFieldOrdinalsInOrder)
                {
                    orderedPaths.Add(field.ReferenceJsonPath);
                }
            }
        }

        foreach (var descriptorSource in descriptorProjectionSources)
        {
            orderedPaths.Add(descriptorSource.DescriptorValuePath);
        }

        orderedPaths.Sort(
            static (left, right) => string.Compare(left.Canonical, right.Canonical, StringComparison.Ordinal)
        );

        var root = new PropertyOrderNode();
        HashSet<string> seenPaths = new(StringComparer.Ordinal);

        foreach (var path in orderedPaths)
        {
            if (!seenPaths.Add(path.Canonical))
            {
                continue;
            }

            AddPath(root, path);
        }

        return root;
    }

    private static void AddPath(PropertyOrderNode root, JsonPathExpression path)
    {
        var current = root;

        foreach (var segment in path.Segments)
        {
            if (segment is JsonPathSegment.Property property)
            {
                current = current.GetOrAddChild(property.Name);
            }
        }
    }

    private static JsonNode ReorderNode(JsonNode node, PropertyOrderNode propertyOrder)
    {
        return node switch
        {
            JsonObject jsonObject => ReorderObject(jsonObject, propertyOrder),
            JsonArray jsonArray => ReorderArray(jsonArray, propertyOrder),
            _ => node.DeepClone(),
        };
    }

    private static JsonObject ReorderObject(JsonObject jsonObject, PropertyOrderNode propertyOrder)
    {
        var reordered = new JsonObject();
        HashSet<string> emittedProperties = new(StringComparer.Ordinal);

        foreach (var (propertyName, childOrder) in propertyOrder.ChildrenInOrder)
        {
            if (!jsonObject.TryGetPropertyValue(propertyName, out var propertyValue))
            {
                continue;
            }

            reordered[propertyName] = propertyValue is null ? null : ReorderNode(propertyValue, childOrder);
            emittedProperties.Add(propertyName);
        }

        foreach (var property in jsonObject)
        {
            if (!emittedProperties.Add(property.Key))
            {
                continue;
            }

            reordered[property.Key] = property.Value is null
                ? null
                : ReorderNode(property.Value, PropertyOrderNode.Empty);
        }

        return reordered;
    }

    private static JsonArray ReorderArray(JsonArray jsonArray, PropertyOrderNode propertyOrder)
    {
        var reordered = new JsonArray();

        foreach (var item in jsonArray)
        {
            reordered.Add(item is null ? null : ReorderNode(item, propertyOrder));
        }

        return reordered;
    }

    private static bool HasMeaningfulContent(JsonNode? node)
    {
        return node switch
        {
            null => false,
            JsonObject jsonObject => JsonObjectHasMeaningfulContent(jsonObject),
            JsonArray jsonArray => jsonArray.Count > 0,
            _ => true,
        };
    }

    private static bool JsonObjectHasMeaningfulContent(JsonObject jsonObject)
    {
        foreach (var property in jsonObject)
        {
            if (HasMeaningfulContent(property.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a CLR value from a hydrated row to a <see cref="JsonValue"/>.
    /// </summary>
    private static JsonNode ConvertToJsonValue(object value, RelationalScalarType? scalarType = null)
    {
        if (scalarType?.Kind == ScalarKind.Date)
        {
            return value switch
            {
                DateOnly dateOnly => JsonValue.Create(dateOnly.ToString("yyyy-MM-dd")),
                DateTime dateTime => JsonValue.Create(DateOnly.FromDateTime(dateTime).ToString("yyyy-MM-dd")),
                DateTimeOffset dateTimeOffset => JsonValue.Create(
                    DateOnly.FromDateTime(dateTimeOffset.DateTime).ToString("yyyy-MM-dd")
                ),
                _ => JsonValue.Create(value.ToString() ?? string.Empty),
            };
        }

        if (scalarType?.Kind == ScalarKind.Time)
        {
            return value switch
            {
                TimeOnly timeOnly => JsonValue.Create(timeOnly.ToString("HH:mm:ss")),
                TimeSpan timeSpan => JsonValue.Create(TimeOnly.FromTimeSpan(timeSpan).ToString("HH:mm:ss")),
                DateTime dateTime => JsonValue.Create(TimeOnly.FromDateTime(dateTime).ToString("HH:mm:ss")),
                DateTimeOffset dateTimeOffset => JsonValue.Create(
                    TimeOnly.FromDateTime(dateTimeOffset.DateTime).ToString("HH:mm:ss")
                ),
                _ => JsonValue.Create(value.ToString() ?? string.Empty),
            };
        }

        return value switch
        {
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            short s => JsonValue.Create(s),
            decimal d => JsonValue.Create(d),
            double dbl => JsonValue.Create(dbl),
            float f => JsonValue.Create(f),
            bool b => JsonValue.Create(b),
            string str => JsonValue.Create(str),
            DateTime dt => JsonValue.Create(
                NormalizeUtcDateTime(dt)
                    .ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
            ),
            DateTimeOffset dto => JsonValue.Create(
                dto.UtcDateTime.ToString(
                    "yyyy-MM-ddTHH:mm:ssZ",
                    System.Globalization.CultureInfo.InvariantCulture
                )
            ),
            DateOnly dateOnly => JsonValue.Create(dateOnly.ToString("yyyy-MM-dd")),
            TimeOnly timeOnly => JsonValue.Create(timeOnly.ToString("HH:mm:ss")),
            Guid g => JsonValue.Create(g.ToString()),
            _ => JsonValue.Create(value.ToString() ?? string.Empty),
        };
    }

    private static DateTime NormalizeUtcDateTime(DateTime dateTime) =>
        dateTime.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            DateTimeKind.Utc => dateTime,
            _ => dateTime.ToUniversalTime(),
        };

    /// <summary>
    /// Finds the ordinal of a column by its <see cref="DbColumnName"/>.
    /// </summary>
    private static int FindColumnOrdinalByName(DbTableModel tableModel, DbColumnName columnName)
    {
        for (var i = 0; i < tableModel.Columns.Count; i++)
        {
            if (tableModel.Columns[i].ColumnName.Equals(columnName))
            {
                return i;
            }
        }

        throw new InvalidOperationException(
            $"Column '{columnName.Value}' not found in table '{tableModel.Table}'."
        );
    }

    /// <summary>
    /// Finds the ordinal of the first column with the given kind.
    /// Returns -1 if not found.
    /// </summary>
    private static int FindColumnOrdinalByKind(DbTableModel tableModel, ColumnKind kind)
    {
        for (var i = 0; i < tableModel.Columns.Count; i++)
        {
            if (tableModel.Columns[i].Kind == kind)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class ReconstitutionContext(
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        CompiledReconstitutionPlan? compiledPlan = null
    )
    {
        private readonly Dictionary<DbTableName, ChildRowIndex> _childRowIndexesByTable = [];

        public IReadOnlyList<HydratedTableRows> TableRowsInDependencyOrder => tableRowsInDependencyOrder;

        public IReadOnlyList<object?[]> GetRowsByParentLocator(
            HydratedTableRows childTableRows,
            long parentLocatorValue
        )
        {
            if (!_childRowIndexesByTable.TryGetValue(childTableRows.TableModel.Table, out var childRowIndex))
            {
                childRowIndex = compiledPlan is not null
                    ? ChildRowIndex.Create(
                        compiledPlan.GetTablePlanOrThrow(childTableRows.TableModel.Table),
                        childTableRows
                    )
                    : ChildRowIndex.Create(childTableRows);
                _childRowIndexesByTable[childTableRows.TableModel.Table] = childRowIndex;
            }

            return childRowIndex.GetRows(parentLocatorValue);
        }
    }

    private sealed class ChildRowIndex(
        IReadOnlyDictionary<long, IReadOnlyList<object?[]>> rowsByParentLocator
    )
    {
        public IReadOnlyList<object?[]> GetRows(long parentLocatorValue) =>
            rowsByParentLocator.TryGetValue(parentLocatorValue, out var rows)
                ? rows
                : Array.Empty<object?[]>();

        public static ChildRowIndex Create(HydratedTableRows childTableRows)
        {
            var childTableModel = childTableRows.TableModel;
            var parentScopeColumns = childTableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns;

            if (parentScopeColumns.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Cannot filter child rows for table '{childTableModel.Table}': "
                        + $"expected exactly one ImmediateParentScopeLocatorColumn, but found {parentScopeColumns.Count}."
                );
            }

            var parentLocatorOrdinal = FindColumnOrdinalByName(childTableModel, parentScopeColumns[0]);
            var ordinalColumnOrdinal = FindColumnOrdinalByKind(childTableModel, ColumnKind.Ordinal);
            Dictionary<long, List<object?[]>> rowsByParentLocator = [];

            foreach (var row in childTableRows.Rows)
            {
                var parentLocatorValue = Convert.ToInt64(row[parentLocatorOrdinal]);

                if (!rowsByParentLocator.TryGetValue(parentLocatorValue, out var rows))
                {
                    rows = [];
                    rowsByParentLocator[parentLocatorValue] = rows;
                }

                rows.Add(row);
            }

            if (ordinalColumnOrdinal >= 0)
            {
                foreach (var rows in rowsByParentLocator.Values)
                {
                    rows.Sort(
                        (a, b) =>
                            Convert
                                .ToInt32(a[ordinalColumnOrdinal])
                                .CompareTo(Convert.ToInt32(b[ordinalColumnOrdinal]))
                    );
                }
            }

            return new ChildRowIndex(
                rowsByParentLocator.ToDictionary(
                    static entry => entry.Key,
                    static entry => (IReadOnlyList<object?[]>)entry.Value
                )
            );
        }

        public static ChildRowIndex Create(
            TableReconstitutionPlan tablePlan,
            HydratedTableRows childTableRows
        )
        {
            var parentLocatorOrdinal = tablePlan.ResolveSingleImmediateParentScopeLocatorOrdinalOrThrow();
            var ordinalColumnOrdinal = tablePlan.OrdinalColumnOrdinal;
            Dictionary<long, List<object?[]>> rowsByParentLocator = [];

            foreach (var row in childTableRows.Rows)
            {
                var parentLocatorValue = Convert.ToInt64(row[parentLocatorOrdinal]);

                if (!rowsByParentLocator.TryGetValue(parentLocatorValue, out var rows))
                {
                    rows = [];
                    rowsByParentLocator[parentLocatorValue] = rows;
                }

                rows.Add(row);
            }

            if (ordinalColumnOrdinal is not null)
            {
                foreach (var rows in rowsByParentLocator.Values)
                {
                    rows.Sort(
                        (a, b) =>
                            Convert
                                .ToInt32(a[ordinalColumnOrdinal.Value])
                                .CompareTo(Convert.ToInt32(b[ordinalColumnOrdinal.Value]))
                    );
                }
            }

            return new ChildRowIndex(
                rowsByParentLocator.ToDictionary(
                    static entry => entry.Key,
                    static entry => (IReadOnlyList<object?[]>)entry.Value
                )
            );
        }
    }
}
