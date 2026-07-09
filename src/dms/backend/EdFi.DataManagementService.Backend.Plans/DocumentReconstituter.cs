// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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
    /// Removes the <c>link</c> property from every document-reference object reachable from
    /// the compiled plan's reference paths when <paramref name="linksOptions"/> has
    /// <c>Enabled = false</c>; otherwise a no-op pass-through. Plans-layer wrapper so the
    /// <c>Backend</c> response boundary can drive the strip pass without taking a dependency
    /// on the internal <c>CompiledReconstitutionPlan</c>.
    /// </summary>
    /// <remarks>
    /// Shapes the served body only. Served <c>_etag</c> composition happens at the backend
    /// response boundary from <c>ContentVersion</c> and representation variant inputs, including
    /// the links flag. This strip pass mutates <paramref name="document"/> in place but never
    /// touches identity fields, <c>_etag</c>, or <c>_lastModifiedDate</c>.
    /// </remarks>
    public static void StripReferenceLinks(
        JsonNode? document,
        ResourceReadPlan readPlan,
        ResourceLinksOptions linksOptions
    )
    {
        ArgumentNullException.ThrowIfNull(readPlan);
        ArgumentNullException.ThrowIfNull(linksOptions);

        if (linksOptions.Enabled || document is null)
        {
            return;
        }

        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
        LinkSubtreeStripper.Strip(document, compiledPlan);
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
    /// Reconstitutes all documents from a hydrated page using one page-scoped graph/context build.
    /// </summary>
    /// <remarks>
    /// This overload is the test-only / link-injection-disabled path: it builds the context
    /// without a <see cref="LinkEmissionContext"/>, so document-reference <c>link</c> objects
    /// are never written. Production callers that want link injection should use the overload
    /// taking <see cref="MappingSet"/> and <see cref="IDocumentLinkSlugResolver"/>.
    /// </remarks>
    public static IReadOnlyList<JsonNode> ReconstitutePage(
        ResourceReadPlan readPlan,
        HydratedPage hydratedPage
    )
    {
        ArgumentNullException.ThrowIfNull(readPlan);
        ArgumentNullException.ThrowIfNull(hydratedPage);

        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
        var pageReconstitutionContext = PageReconstitutionContext.Build(compiledPlan, hydratedPage);

        return ReconstituteAllDocuments(pageReconstitutionContext);
    }

    /// <summary>
    /// Reconstitutes all documents from a hydrated page with link injection driven by the
    /// supplied <see cref="MappingSet"/> and <see cref="IDocumentLinkSlugResolver"/>. The
    /// <see cref="LinkEmissionContext"/> is always built when a <see cref="MappingSet"/> and
    /// resolver are in scope — the reconstituted intermediate is caller-agnostic and
    /// link-bearing per <c>design-docs/link-injection.md</c> §Configuration and §Cache and Etag.
    /// <c>ResourceLinksOptions.Enabled = false</c> is honored by the final response-shaping
    /// pass in the repository wrapper via <see cref="StripReferenceLinks"/>, not by suppressing
    /// emission here.
    /// </summary>
    public static IReadOnlyList<JsonNode> ReconstitutePage(
        ResourceReadPlan readPlan,
        HydratedPage hydratedPage,
        MappingSet mappingSet,
        IDocumentLinkSlugResolver slugResolver
    )
    {
        ArgumentNullException.ThrowIfNull(readPlan);
        ArgumentNullException.ThrowIfNull(hydratedPage);
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(slugResolver);

        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
        var linkEmission = new LinkEmissionContext(mappingSet, slugResolver);
        var pageReconstitutionContext = PageReconstitutionContext.Build(
            compiledPlan,
            hydratedPage,
            linkEmission
        );

        return ReconstituteAllDocuments(pageReconstitutionContext);
    }

    private static IReadOnlyList<JsonNode> ReconstituteAllDocuments(
        PageReconstitutionContext pageReconstitutionContext
    )
    {
        List<JsonNode> documents = new(pageReconstitutionContext.DocumentsInOrder.Length);

        foreach (var documentPageNode in pageReconstitutionContext.DocumentsInOrder)
        {
            documents.Add(ReconstituteDocument(documentPageNode, pageReconstitutionContext));
        }

        return documents;
    }

    private static JsonNode ReconstituteDocument(
        DocumentPageNode documentPageNode,
        PageReconstitutionContext pageReconstitutionContext
    )
    {
        ArgumentNullException.ThrowIfNull(documentPageNode);
        ArgumentNullException.ThrowIfNull(pageReconstitutionContext);

        var rootRow = documentPageNode.RootRow;
        var rootTablePlan = rootRow.TablePlan;
        var result = new JsonObject();

        EmitScalars(result, rootRow.Row, rootTablePlan.TableModel);
        EmitReferences(result, rootRow.Row, rootTablePlan, pageReconstitutionContext);
        EmitDescriptors(result, rootRow.Row, rootTablePlan, pageReconstitutionContext.DescriptorUrisById);
        EmitChildScopes(result, rootRow, pageReconstitutionContext);

        return ReorderNode(result, pageReconstitutionContext.CompiledPlan.PropertyOrder);
    }

    /// <summary>
    /// Test-only adapter for legacy tests that predate full <see cref="ResourceReadPlan"/>
    /// construction. Production callers should use the read-plan overload or
    /// <see cref="ReconstitutePage(ResourceReadPlan, HydratedPage)"/>.
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
    internal static JsonNode Reconstitute(
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

        var readPlan = BuildReadPlanAdapter(
            tableRowsInDependencyOrder,
            referenceProjectionPlans,
            descriptorProjectionSources
        );

        return Reconstitute(documentId, readPlan, tableRowsInDependencyOrder, descriptorUriLookup);
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

        var singleDocumentRows = FilterRowsForDocument(documentId, tableRowsInDependencyOrder, compiledPlan);
        var pageReconstitutionContext = PageReconstitutionContext.Build(
            compiledPlan,
            [CreatePlaceholderDocumentMetadataRow(documentId)],
            singleDocumentRows,
            descriptorUriLookup
        );

        return ReconstituteDocument(
            pageReconstitutionContext.GetDocumentOrThrow(documentId),
            pageReconstitutionContext
        );
    }

    private static ResourceReadPlan BuildReadPlanAdapter(
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referenceProjectionPlans,
        IReadOnlyList<DescriptorEdgeSource> descriptorProjectionSources
    )
    {
        var rootTableModel = tableRowsInDependencyOrder[0].TableModel;
        var tableModelsInDependencyOrder = new DbTableModel[tableRowsInDependencyOrder.Count];

        for (var index = 0; index < tableRowsInDependencyOrder.Count; index++)
        {
            tableModelsInDependencyOrder[index] = tableRowsInDependencyOrder[index].TableModel;
        }

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("ReconstitutionAdapter", "Document"),
            PhysicalSchema: rootTableModel.Table.Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: tableModelsInDependencyOrder,
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: descriptorProjectionSources
        );

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: new KeysetTableContract(
                Table: new SqlRelationRef.TempTable("page"),
                DocumentIdColumnName: new DbColumnName("DocumentId")
            ),
            TablePlansInDependencyOrder: BuildTableReadPlanAdapters(tableRowsInDependencyOrder),
            ReferenceIdentityProjectionPlansInDependencyOrder: referenceProjectionPlans,
            DescriptorProjectionPlansInOrder: BuildDescriptorProjectionPlans(
                tableRowsInDependencyOrder,
                descriptorProjectionSources
            )
        );
    }

    private static IReadOnlyList<TableReadPlan> BuildTableReadPlanAdapters(
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder
    )
    {
        var tableReadPlans = new TableReadPlan[tableRowsInDependencyOrder.Count];

        for (var index = 0; index < tableRowsInDependencyOrder.Count; index++)
        {
            tableReadPlans[index] = new TableReadPlan(
                tableRowsInDependencyOrder[index].TableModel,
                string.Empty
            );
        }

        return tableReadPlans;
    }

    private static IReadOnlyList<DescriptorProjectionPlan> BuildDescriptorProjectionPlans(
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyList<DescriptorEdgeSource> descriptorProjectionSources
    )
    {
        if (descriptorProjectionSources.Count == 0)
        {
            return [];
        }

        Dictionary<DbTableName, HydratedTableRows> tableRowsByTable = new(tableRowsInDependencyOrder.Count);

        foreach (var tableRows in tableRowsInDependencyOrder)
        {
            if (!tableRowsByTable.TryAdd(tableRows.TableModel.Table, tableRows))
            {
                throw new InvalidOperationException(
                    $"Cannot build reconstitution read-plan adapter: duplicate hydrated rows were provided for table '{tableRows.TableModel.Table}'."
                );
            }
        }

        List<DescriptorProjectionSource> projectionSources = new(descriptorProjectionSources.Count);

        foreach (var descriptorSource in descriptorProjectionSources)
        {
            if (!tableRowsByTable.TryGetValue(descriptorSource.Table, out var tableRows))
            {
                throw new InvalidOperationException(
                    $"Cannot build reconstitution read-plan adapter: descriptor projection source for table '{descriptorSource.Table}' "
                        + "does not have matching hydrated table rows."
                );
            }

            projectionSources.Add(
                new DescriptorProjectionSource(
                    DescriptorValuePath: descriptorSource.DescriptorValuePath,
                    Table: descriptorSource.Table,
                    DescriptorResource: descriptorSource.DescriptorResource,
                    DescriptorIdColumnOrdinal: FindColumnOrdinalByName(
                        tableRows.TableModel,
                        descriptorSource.FkColumn
                    )
                )
            );
        }

        return
        [
            new DescriptorProjectionPlan(
                SelectByKeysetSql: string.Empty,
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: projectionSources
            ),
        ];
    }

    private static IReadOnlyList<HydratedTableRows> FilterRowsForDocument(
        long documentId,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        CompiledReconstitutionPlan compiledPlan
    )
    {
        List<HydratedTableRows> singleDocumentRows = new(tableRowsInDependencyOrder.Count);

        foreach (var tableRows in tableRowsInDependencyOrder)
        {
            var tablePlan = compiledPlan.GetTablePlanOrThrow(tableRows.TableModel.Table);
            var rootScopeLocatorOrdinal = tablePlan.ResolveSingleRootScopeLocatorOrdinalOrThrow();
            var rootScopeLocatorColumn = tablePlan.TableModel.Columns[rootScopeLocatorOrdinal].ColumnName;

            List<object?[]> rowsForDocument = new(tableRows.Rows.Count);

            for (var rowIndex = 0; rowIndex < tableRows.Rows.Count; rowIndex++)
            {
                var row = tableRows.Rows[rowIndex];

                if (
                    RowBelongsToDocument(
                        row,
                        tableRows.TableModel.Table,
                        rootScopeLocatorColumn,
                        rootScopeLocatorOrdinal,
                        documentId
                    )
                )
                {
                    rowsForDocument.Add(row);
                }
            }

            singleDocumentRows.Add(tableRows with { Rows = rowsForDocument });
        }

        return singleDocumentRows;
    }

    private static bool RowBelongsToDocument(
        object?[] row,
        DbTableName table,
        DbColumnName rootScopeLocatorColumn,
        int rootScopeLocatorOrdinal,
        long documentId
    )
    {
        return ConvertRootScopeLocatorToInt64OrThrow(
                row[rootScopeLocatorOrdinal],
                table,
                rootScopeLocatorColumn,
                rootScopeLocatorOrdinal
            ) == documentId;
    }

    private static long ConvertRootScopeLocatorToInt64OrThrow(
        object? value,
        DbTableName table,
        DbColumnName column,
        int columnOrdinal
    )
    {
        return ConvertToInt64OrThrow(value, table, column, columnOrdinal, "root-scope locator");
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

            if (column.ScalarType is not RelationalScalarType scalarType)
            {
                throw new InvalidOperationException(
                    $"Cannot reconstitute scalar column '{column.ColumnName.Value}' on table '{tableModel.Table}' because scalar type metadata is missing."
                );
            }

            var (targetObject, propertyName) = ResolvePathRelativeToScope(
                target,
                column.SourceJsonPath.Value,
                tableModel.JsonScope
            );
            targetObject[propertyName] = ConvertToJsonValue(value, scalarType);
        }
    }

    private static void EmitReferences(
        JsonObject target,
        object?[] row,
        TableReconstitutionPlan tablePlan,
        PageReconstitutionContext pageReconstitutionContext
    )
    {
        foreach (var binding in tablePlan.ReferenceBindingsInOrder)
        {
            var projectionResult = ReferenceIdentityProjector.Project(row, binding);
            if (projectionResult is ReferenceProjectionResult.Present present)
            {
                EmitReferenceObject(target, row, binding, present, tablePlan, pageReconstitutionContext);
            }
        }
    }

    /// <summary>
    /// Emits a single reference object into the target JSON object, optionally appending the
    /// document-reference <c>link</c> property when link emission is enabled and the FK value
    /// is present in the page-scoped lookup map.
    /// </summary>
    private static void EmitReferenceObject(
        JsonObject target,
        object?[] row,
        ReferenceIdentityProjectionBinding binding,
        ReferenceProjectionResult.Present present,
        TableReconstitutionPlan tablePlan,
        PageReconstitutionContext pageReconstitutionContext
    )
    {
        var scope = tablePlan.TableModel.JsonScope;
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
            fieldParent[fieldPropertyName] = ConvertToJsonValue(field.Value, field.ScalarType);
        }

        EmitReferenceLink(referenceObject, row, binding, tablePlan, pageReconstitutionContext);

        referenceParent[referencePropertyName] = referenceObject;
    }

    /// <summary>
    /// Writes a <c>link: { rel, href }</c> object into <paramref name="referenceObject"/> when
    /// link emission is configured for this page, the binding's FK column is non-null, and
    /// the FK value is present in the page-scoped lookup map. No-ops otherwise.
    /// </summary>
    private static void EmitReferenceLink(
        JsonObject referenceObject,
        object?[] row,
        ReferenceIdentityProjectionBinding binding,
        TableReconstitutionPlan tablePlan,
        PageReconstitutionContext pageReconstitutionContext
    )
    {
        var linkEmission = pageReconstitutionContext.LinkEmission;

        if (linkEmission is null)
        {
            return;
        }

        var fkValue = row[binding.FkColumnOrdinal];

        if (fkValue is null)
        {
            return;
        }

        var fkColumnName = tablePlan.TableModel.Columns[binding.FkColumnOrdinal].ColumnName;
        var documentId = ConvertToInt64OrThrow(
            fkValue,
            tablePlan.Table,
            fkColumnName,
            binding.FkColumnOrdinal,
            "document-reference FK"
        );

        if (!pageReconstitutionContext.DocumentLinkLookupById.TryGetValue(documentId, out var lookup))
        {
            return;
        }

        var slug = linkEmission.SlugResolver.Resolve(linkEmission.MappingSet, lookup.ResourceKeyId);
        var href = $"/{slug.ProjectEndpointName}/{slug.EndpointName}/" + lookup.DocumentUuid.ToString("D");

        referenceObject["link"] = new JsonObject
        {
            ["rel"] = JsonValue.Create(slug.ResourceName),
            ["href"] = JsonValue.Create(href),
        };
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

            var descriptorIdColumn = tablePlan
                .TableModel
                .Columns[binding.DescriptorIdColumnOrdinal]
                .ColumnName;
            var descriptorId = ConvertDescriptorIdToInt64OrThrow(
                descriptorIdValue,
                tablePlan.Table,
                descriptorIdColumn,
                binding.DescriptorIdColumnOrdinal
            );
            if (!descriptorUriLookup.TryGetValue(descriptorId, out var uri))
            {
                throw new InvalidOperationException(
                    $"Descriptor ID {descriptorId} in column '{descriptorIdColumn.Value}' at ordinal '{binding.DescriptorIdColumnOrdinal}' of table '{tablePlan.Table}' "
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

    private static long ConvertDescriptorIdToInt64OrThrow(
        object? value,
        DbTableName table,
        DbColumnName column,
        int columnOrdinal
    )
    {
        return ConvertToInt64OrThrow(value, table, column, columnOrdinal, "descriptor ID");
    }

    private static long ConvertToInt64OrThrow(
        object? value,
        DbTableName table,
        DbColumnName column,
        int columnOrdinal,
        string valueDescription
    )
    {
        try
        {
            return value is null
                ? throw new InvalidOperationException(
                    CreateConversionFailureMessage(valueDescription, table, column, columnOrdinal, value)
                )
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                CreateConversionFailureMessage(valueDescription, table, column, columnOrdinal, value),
                ex
            );
        }
    }

    private static string CreateConversionFailureMessage(
        string valueDescription,
        DbTableName table,
        DbColumnName column,
        int columnOrdinal,
        object? value
    )
    {
        return $"Cannot reconstitute document: table '{table}' column '{column.Value}' at ordinal '{columnOrdinal}' "
            + $"contains {FormatValueAndType(value)} that cannot be converted to {valueDescription}.";
    }

    private static string FormatValueAndType(object? value) =>
        value is null ? "<null> (type: <null>)" : $"'{value}' (type: {value.GetType().FullName})";

    private static void EmitChildScopes(
        JsonObject parentObject,
        RowNode parentRow,
        PageReconstitutionContext pageReconstitutionContext
    )
    {
        foreach (var childTable in parentRow.TablePlan.ImmediateChildrenInDependencyOrder)
        {
            var childRows = parentRow.GetImmediateChildren(childTable);
            if (childRows.Count == 0)
            {
                continue;
            }

            var childTablePlan = pageReconstitutionContext.CompiledPlan.GetTablePlanOrThrow(childTable);
            var childKind = childTablePlan.TableModel.IdentityMetadata.TableKind;

            switch (childKind)
            {
                case DbTableKind.Collection:
                case DbTableKind.ExtensionCollection:
                    EmitCollectionArray(
                        parentObject,
                        parentRow,
                        childTablePlan,
                        childRows,
                        pageReconstitutionContext
                    );
                    break;

                case DbTableKind.RootExtension:
                case DbTableKind.CollectionExtensionScope:
                    EmitExtensionScope(
                        parentObject,
                        parentRow,
                        childTablePlan,
                        childRows,
                        pageReconstitutionContext
                    );
                    break;
            }
        }
    }

    private static void EmitCollectionArray(
        JsonObject parentObject,
        RowNode parentRow,
        TableReconstitutionPlan childTablePlan,
        IReadOnlyList<RowNode> childRows,
        PageReconstitutionContext pageReconstitutionContext
    )
    {
        var childTableModel = childTablePlan.TableModel;
        var (collectionTarget, collectionPropertyName) = ResolveCollectionTarget(
            parentObject,
            childTableModel.JsonScope,
            parentRow.TablePlan.TableModel.JsonScope,
            childTableModel.IdentityMetadata.TableKind
        );

        var array = new JsonArray();

        foreach (var childRow in childRows)
        {
            var itemObject = new JsonObject();

            EmitScalars(itemObject, childRow.Row, childTableModel);
            EmitReferences(itemObject, childRow.Row, childTablePlan, pageReconstitutionContext);
            EmitDescriptors(
                itemObject,
                childRow.Row,
                childTablePlan,
                pageReconstitutionContext.DescriptorUrisById
            );
            EmitChildScopes(itemObject, childRow, pageReconstitutionContext);

            array.Add(itemObject);
        }

        collectionTarget[collectionPropertyName] = array;
    }

    private static void EmitExtensionScope(
        JsonObject parentObject,
        RowNode parentRow,
        TableReconstitutionPlan childTablePlan,
        IReadOnlyList<RowNode> childRows,
        PageReconstitutionContext pageReconstitutionContext
    )
    {
        var childTableModel = childTablePlan.TableModel;
        var (extPropertyName, projectName) = ParseExtensionScope(
            childTableModel.JsonScope,
            parentRow.TablePlan.TableModel.JsonScope,
            childTableModel.IdentityMetadata.TableKind
        );
        if (childRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Cannot reconstitute extension scope: expected exactly one row from child table '{childTablePlan.Table}' "
                    + $"for parent table '{parentRow.Table}', but found {childRows.Count}. "
                    + $"Parent row identity: {PageReconstitutionContext.FormatScopeKey(parentRow.PhysicalRowIdentity)}."
            );
        }

        var extensionRow = childRows[0];
        var projectObject = new JsonObject();

        EmitScalars(projectObject, extensionRow.Row, childTableModel);
        EmitReferences(projectObject, extensionRow.Row, childTablePlan, pageReconstitutionContext);
        EmitDescriptors(
            projectObject,
            extensionRow.Row,
            childTablePlan,
            pageReconstitutionContext.DescriptorUrisById
        );
        EmitChildScopes(projectObject, extensionRow, pageReconstitutionContext);

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
    /// Resolves the target JSON object and property name for a collection array, navigating
    /// through intermediate inlined-object segments and creating them as needed.
    /// e.g., parent "$", child "$.addresses[*]" => (rootObject, "addresses")
    /// e.g., parent "$", child "$.contentStandard.authors[*]" => (contentStandardObject, "authors")
    /// </summary>
    private static (JsonObject TargetObject, string PropertyName) ResolveCollectionTarget(
        JsonObject scopeObject,
        JsonPathExpression collectionScope,
        JsonPathExpression parentScope,
        DbTableKind childKind
    )
    {
        var relativeSegments = JsonScopeAttachmentResolver.ResolveRelativeAttachmentSegmentsOrThrow(
            parentScope,
            collectionScope,
            childKind
        );
        var target = scopeObject;

        for (var i = 0; i < relativeSegments.Length; i++)
        {
            if (relativeSegments[i] is not JsonPathSegment.Property prop)
            {
                continue;
            }

            // If the next segment is AnyArrayElement, this property is the collection name
            if (i + 1 < relativeSegments.Length && relativeSegments[i + 1] is JsonPathSegment.AnyArrayElement)
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
        JsonPathExpression parentScope,
        DbTableKind childKind
    )
    {
        var relativeSegments = JsonScopeAttachmentResolver.ResolveRelativeAttachmentSegmentsOrThrow(
            parentScope,
            childScope,
            childKind
        );

        if (
            relativeSegments.Length != 2
            || relativeSegments[0] is not JsonPathSegment.Property { Name: "_ext" }
            || relativeSegments[1] is not JsonPathSegment.Property { Name.Length: > 0 } project
        )
        {
            throw new InvalidOperationException(
                $"Cannot parse extension scope from '{childScope.Canonical}' "
                    + $"relative to parent scope '{parentScope.Canonical}'."
            );
        }

        return ("_ext", project.Name);
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
        HashSet<string> emittedProperties = new(jsonObject.Count, StringComparer.Ordinal);

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
        return jsonObject.Any(property => HasMeaningfulContent(property.Value));
    }

    /// <summary>
    /// Converts a CLR value from a hydrated row to a <see cref="JsonValue"/>.
    /// </summary>
    private static JsonNode ConvertToJsonValue(object value, RelationalScalarType scalarType)
    {
        return scalarType.Kind switch
        {
            ScalarKind.String => value switch
            {
                string str => JsonValue.Create(str),
                Guid g => JsonValue.Create(g.ToString()),
                _ => ThrowUnsupportedScalarValue(value, scalarType),
            },
            ScalarKind.Int32 => value switch
            {
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                short s => JsonValue.Create(s),
                _ => ThrowUnsupportedScalarValue(value, scalarType),
            },
            ScalarKind.Int64 => value switch
            {
                long l => JsonValue.Create(l),
                int i => JsonValue.Create(i),
                short s => JsonValue.Create(s),
                _ => ThrowUnsupportedScalarValue(value, scalarType),
            },
            ScalarKind.Decimal => value switch
            {
                decimal d => JsonValue.Create(DecimalValueCanonicalizer.NormalizeScale(d)),
                double dbl => JsonValue.Create(dbl),
                float f => JsonValue.Create(f),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                short s => JsonValue.Create(s),
                _ => ThrowUnsupportedScalarValue(value, scalarType),
            },
            ScalarKind.Boolean => value switch
            {
                bool b => JsonValue.Create(b),
                _ => ThrowUnsupportedScalarValue(value, scalarType),
            },
            ScalarKind.Date => ConvertDateValue(value, scalarType),
            ScalarKind.DateTime => ConvertDateTimeValue(value, scalarType),
            ScalarKind.Time => ConvertTimeValue(value, scalarType),
            _ => throw new InvalidOperationException(
                $"Cannot reconstitute scalar value: unsupported relational scalar kind '{scalarType.Kind}'."
            ),
        };
    }

    private static JsonNode ConvertDateValue(object value, RelationalScalarType scalarType)
    {
        return value switch
        {
            DateOnly dateOnly => JsonValue.Create(dateOnly.ToString("yyyy-MM-dd")),
            DateTime dateTime => JsonValue.Create(DateOnly.FromDateTime(dateTime).ToString("yyyy-MM-dd")),
            DateTimeOffset dateTimeOffset => JsonValue.Create(
                DateOnly.FromDateTime(dateTimeOffset.DateTime).ToString("yyyy-MM-dd")
            ),
            _ => ThrowUnsupportedScalarValue(value, scalarType),
        };
    }

    private static JsonNode ConvertDateTimeValue(object value, RelationalScalarType scalarType)
    {
        return value switch
        {
            DateTime dateTime => JsonValue.Create(
                NormalizeUtcDateTime(dateTime).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            ),
            DateTimeOffset dateTimeOffset => JsonValue.Create(
                dateTimeOffset.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            ),
            _ => ThrowUnsupportedScalarValue(value, scalarType),
        };
    }

    private static JsonNode ConvertTimeValue(object value, RelationalScalarType scalarType)
    {
        return value switch
        {
            TimeOnly timeOnly => JsonValue.Create(timeOnly.ToString("HH:mm:ss")),
            TimeSpan timeSpan => JsonValue.Create(TimeOnly.FromTimeSpan(timeSpan).ToString("HH:mm:ss")),
            DateTime dateTime => JsonValue.Create(TimeOnly.FromDateTime(dateTime).ToString("HH:mm:ss")),
            DateTimeOffset dateTimeOffset => JsonValue.Create(
                TimeOnly.FromDateTime(dateTimeOffset.DateTime).ToString("HH:mm:ss")
            ),
            _ => ThrowUnsupportedScalarValue(value, scalarType),
        };
    }

    private static JsonNode ThrowUnsupportedScalarValue(object value, RelationalScalarType scalarType)
    {
        throw new InvalidOperationException(
            $"Cannot reconstitute scalar value of CLR type '{value.GetType().FullName}' as relational scalar kind '{scalarType.Kind}'."
        );
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

    private static DocumentMetadataRow CreatePlaceholderDocumentMetadataRow(long documentId) =>
        new(
            DocumentId: documentId,
            DocumentUuid: Guid.Empty,
            ContentVersion: 0L,
            IdentityVersion: 0L,
            ContentLastModifiedAt: DateTimeOffset.UnixEpoch,
            IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
        );
}
