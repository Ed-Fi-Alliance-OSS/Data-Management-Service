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

        // Phase 1: Find root table and root row
        var rootTableRows = tableRowsInDependencyOrder[0];
        var rootTableModel = rootTableRows.TableModel;
        var rootRow = FindRootRow(documentId, rootTableRows);

        var result = new JsonObject();

        // Phase 1: Emit root scalars
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
                tableRowsInDependencyOrder,
                referenceProjectionPlans,
                descriptorProjectionSources,
                descriptorUriLookup
            );
        }

        return result;
    }

    /// <summary>
    /// Finds the root row matching the given document id.
    /// </summary>
    private static object?[] FindRootRow(long documentId, HydratedTableRows rootTableRows)
    {
        var rootScopeLocatorColumns = rootTableRows.TableModel.IdentityMetadata.RootScopeLocatorColumns;
        if (rootScopeLocatorColumns.Count == 0)
        {
            // Fallback: look for a ParentKeyPart column named DocumentId
            var docIdOrdinal = FindColumnOrdinalByKindAndName(
                rootTableRows.TableModel,
                ColumnKind.ParentKeyPart,
                "DocumentId"
            );
            if (docIdOrdinal >= 0)
            {
                foreach (var row in rootTableRows.Rows)
                {
                    if (Convert.ToInt64(row[docIdOrdinal]) == documentId)
                    {
                        return row;
                    }
                }
            }

            throw new InvalidOperationException(
                $"Cannot reconstitute document: no root row found for DocumentId {documentId}."
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
            targetObject[propertyName] = ConvertToJsonValue(value);
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
                continue;
            }

            var (targetObject, propertyName) = ResolvePathRelativeToScope(
                target,
                source.DescriptorValuePath,
                tableModel.JsonScope
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
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referenceProjectionPlans,
        IReadOnlyList<DescriptorEdgeSource> descriptorProjectionSources,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        var immediateChildren = FindImmediateChildTables(parentTableModel, tableRowsInDependencyOrder);

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
                        tableRowsInDependencyOrder,
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
                        tableRowsInDependencyOrder,
                        referenceProjectionPlans,
                        descriptorProjectionSources,
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
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referenceProjectionPlans,
        IReadOnlyList<DescriptorEdgeSource> descriptorProjectionSources,
        IReadOnlyDictionary<long, string> descriptorUriLookup
    )
    {
        var childTableModel = childTableRows.TableModel;
        var collectionPropertyName = ExtractCollectionPropertyName(
            childTableModel.JsonScope,
            parentTableModel.JsonScope
        );

        var matchingRows = FilterChildRows(
            documentId,
            parentCollectionItemId,
            parentTableModel,
            childTableRows
        );

        if (matchingRows.Count == 0)
        {
            return;
        }

        // Sort by ordinal if an Ordinal column exists
        var ordinalColumnOrdinal = FindColumnOrdinalByKind(childTableModel, ColumnKind.Ordinal);
        if (ordinalColumnOrdinal >= 0)
        {
            matchingRows.Sort(
                (a, b) =>
                    Convert
                        .ToInt32(a[ordinalColumnOrdinal])
                        .CompareTo(Convert.ToInt32(b[ordinalColumnOrdinal]))
            );
        }

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
                    tableRowsInDependencyOrder,
                    referenceProjectionPlans,
                    descriptorProjectionSources,
                    descriptorUriLookup
                );
            }

            array.Add(itemObject);
        }

        // For extension collections, the parent is an _ext.projectName scope,
        // so the property needs to land on the correct parent
        parentObject[collectionPropertyName] = array;
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
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
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
            childTableRows
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

        // Ensure _ext object exists on parent
        if (parentObject[extPropertyName] is not JsonObject extObject)
        {
            extObject = new JsonObject();
            parentObject[extPropertyName] = extObject;
        }

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
            tableRowsInDependencyOrder,
            referenceProjectionPlans,
            descriptorProjectionSources,
            descriptorUriLookup
        );

        extObject[projectName] = projectObject;
    }

    /// <summary>
    /// Filters child rows to those matching the given parent scope.
    /// </summary>
    private static List<object?[]> FilterChildRows(
        long documentId,
        long? parentCollectionItemId,
        DbTableModel parentTableModel,
        HydratedTableRows childTableRows
    )
    {
        var childTableModel = childTableRows.TableModel;
        var parentScopeColumns = childTableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns;

        if (parentScopeColumns.Count == 0)
        {
            // Fallback: match by root DocumentId
            var rootScopeColumns = childTableModel.IdentityMetadata.RootScopeLocatorColumns;
            if (rootScopeColumns.Count > 0)
            {
                var rootLocatorOrdinal = FindColumnOrdinalByName(childTableModel, rootScopeColumns[0]);
                List<object?[]> result = [];
                foreach (var row in childTableRows.Rows)
                {
                    if (Convert.ToInt64(row[rootLocatorOrdinal]) == documentId)
                    {
                        result.Add(row);
                    }
                }
                return result;
            }

            return [.. childTableRows.Rows];
        }

        // For root children, the parent scope locator is DocumentId
        // For nested children, the parent scope locator is the parent's CollectionItemId
        var parentLocatorOrdinal = FindColumnOrdinalByName(childTableModel, parentScopeColumns[0]);
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

        List<object?[]> filtered = [];
        foreach (var row in childTableRows.Rows)
        {
            if (Convert.ToInt64(row[parentLocatorOrdinal]) == matchValue)
            {
                filtered.Add(row);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Finds immediate child tables of the given parent table in the dependency-ordered list.
    /// </summary>
    private static List<HydratedTableRows> FindImmediateChildTables(
        DbTableModel parentTableModel,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder
    )
    {
        List<HydratedTableRows> children = [];
        var parentScope = parentTableModel.JsonScope.Canonical;

        foreach (var tableRows in tableRowsInDependencyOrder)
        {
            var childTableModel = tableRows.TableModel;
            if (childTableModel.Table.Equals(parentTableModel.Table))
            {
                continue;
            }

            var childScope = childTableModel.JsonScope.Canonical;

            if (!childScope.StartsWith(parentScope, StringComparison.Ordinal))
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
    private static bool IsImmediateChild(string parentScope, string childScope, DbTableKind childKind)
    {
        var suffix = childScope[parentScope.Length..];

        // Remove leading dot if present (e.g., "$.addresses[*]" parent -> ".city" suffix)
        if (suffix.StartsWith('.'))
        {
            suffix = suffix[1..];
        }

        return childKind switch
        {
            // Collections: one [*] deeper than parent
            // e.g., parent "$" -> child "$.addresses[*]" => suffix = "addresses[*]"
            // e.g., parent "$.addresses[*]" -> child "$.addresses[*].periods[*]" => suffix = "periods[*]"
            DbTableKind.Collection or DbTableKind.ExtensionCollection => IsOneArrayLevelDeeper(suffix),

            // Extensions: _ext.projectName at same array depth
            // e.g., parent "$" -> child "$._ext.sample" => suffix = "_ext.sample"
            // e.g., parent "$.addresses[*]" -> child "$.addresses[*]._ext.sample" => suffix = "_ext.sample"
            DbTableKind.RootExtension or DbTableKind.CollectionExtensionScope => IsExtensionScope(suffix),

            _ => false,
        };
    }

    /// <summary>
    /// Returns true if the suffix represents exactly one array level deeper.
    /// e.g., "addresses[*]" or "periods[*]" — a property name followed by [*] with no further nesting.
    /// </summary>
    private static bool IsOneArrayLevelDeeper(string suffix)
    {
        if (!suffix.EndsWith("[*]", StringComparison.Ordinal))
        {
            return false;
        }

        var propertyPart = suffix[..^3]; // Remove "[*]"

        // Should be a simple property name (no dots, no further array access)
        return propertyPart.Length > 0 && !propertyPart.Contains('.') && !propertyPart.Contains('[');
    }

    /// <summary>
    /// Returns true if the suffix represents an extension scope (_ext.projectName).
    /// </summary>
    private static bool IsExtensionScope(string suffix)
    {
        if (!suffix.StartsWith("_ext.", StringComparison.Ordinal))
        {
            return false;
        }

        var projectName = suffix["_ext.".Length..];

        // Should be a simple project name (no further nesting)
        return projectName.Length > 0 && !projectName.Contains('.') && !projectName.Contains('[');
    }

    /// <summary>
    /// Extracts the collection property name from a child collection scope relative to its parent.
    /// e.g., parent "$", child "$.addresses[*]" => "addresses"
    /// e.g., parent "$.addresses[*]", child "$.addresses[*].periods[*]" => "periods"
    /// </summary>
    private static string ExtractCollectionPropertyName(
        JsonPathExpression childScope,
        JsonPathExpression parentScope
    )
    {
        // Walk segments to find the first Property segment after the parent's segments
        var parentSegmentCount = parentScope.Segments.Count;
        for (var i = parentSegmentCount; i < childScope.Segments.Count; i++)
        {
            if (childScope.Segments[i] is JsonPathSegment.Property prop)
            {
                return prop.Name;
            }
        }

        throw new InvalidOperationException(
            $"Cannot extract collection property name from child scope '{childScope.Canonical}' "
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
    /// Converts a CLR value from a hydrated row to a <see cref="JsonValue"/>.
    /// </summary>
    private static JsonNode ConvertToJsonValue(object value)
    {
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
            DateTime dt => JsonValue.Create(dt.ToString("O")),
            DateTimeOffset dto => JsonValue.Create(dto.ToString("O")),
            DateOnly dateOnly => JsonValue.Create(dateOnly.ToString("yyyy-MM-dd")),
            TimeOnly timeOnly => JsonValue.Create(timeOnly.ToString("HH:mm:ss")),
            Guid g => JsonValue.Create(g.ToString()),
            _ => JsonValue.Create(value.ToString() ?? string.Empty),
        };
    }

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

    /// <summary>
    /// Finds the ordinal of the first column with the given kind and name.
    /// Returns -1 if not found.
    /// </summary>
    private static int FindColumnOrdinalByKindAndName(DbTableModel tableModel, ColumnKind kind, string name)
    {
        for (var i = 0; i < tableModel.Columns.Count; i++)
        {
            var column = tableModel.Columns[i];
            if (column.Kind == kind && column.ColumnName.Value == name)
            {
                return i;
            }
        }

        return -1;
    }
}
