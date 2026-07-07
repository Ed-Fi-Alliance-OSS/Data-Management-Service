// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

internal sealed class DocumentPageNode
{
    public DocumentPageNode(DocumentMetadataRow documentMetadata, RowNode rootRow)
    {
        ArgumentNullException.ThrowIfNull(rootRow);

        DocumentMetadata = documentMetadata;
        RootRow = rootRow;
    }

    public long DocumentId => DocumentMetadata.DocumentId;

    public DocumentMetadataRow DocumentMetadata { get; }

    public RowNode RootRow { get; }
}

internal sealed class RowNode
{
    private readonly Dictionary<DbTableName, List<RowNode>> _immediateChildrenByTable = [];

    public RowNode(
        TableReconstitutionPlan tablePlan,
        object?[] row,
        ScopeKey physicalRowIdentity,
        long rootDocumentId
    )
    {
        ArgumentNullException.ThrowIfNull(tablePlan);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(physicalRowIdentity);

        TablePlan = tablePlan;
        Row = row;
        PhysicalRowIdentity = physicalRowIdentity;
        RootDocumentId = rootDocumentId;
    }

    public DbTableName Table => TablePlan.Table;

    public TableReconstitutionPlan TablePlan { get; }

    public object?[] Row { get; }

    public ScopeKey PhysicalRowIdentity { get; }

    public long RootDocumentId { get; }

    public RowNode? Parent { get; private set; }

    public IReadOnlyList<RowNode> GetImmediateChildren(DbTableName childTable) =>
        _immediateChildrenByTable.TryGetValue(childTable, out var children)
            ? children
            : Array.Empty<RowNode>();

    internal void EnsureImmediateChildrenInOrdinalOrder(DbTableName childTable, int ordinalColumnOrdinal)
    {
        if (!_immediateChildrenByTable.TryGetValue(childTable, out var children))
        {
            return;
        }

        HydratedRowOrdering.EnsureOrdinalOrder(
            children,
            child => ConvertOrdinalToInt32OrThrow(child, ordinalColumnOrdinal)
        );
    }

    private static int ConvertOrdinalToInt32OrThrow(RowNode child, int ordinalColumnOrdinal)
    {
        var ordinalColumn = child.TablePlan.TableModel.Columns[ordinalColumnOrdinal].ColumnName;
        var ordinalValue = child.Row[ordinalColumnOrdinal];

        try
        {
            return ordinalValue is null
                ? throw new InvalidOperationException(
                    CreateOrdinalConversionFailureMessage(
                        child.Table,
                        ordinalColumn,
                        ordinalColumnOrdinal,
                        ordinalValue
                    )
                )
                : Convert.ToInt32(ordinalValue, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                CreateOrdinalConversionFailureMessage(
                    child.Table,
                    ordinalColumn,
                    ordinalColumnOrdinal,
                    ordinalValue
                ),
                ex
            );
        }
    }

    private static string CreateOrdinalConversionFailureMessage(
        DbTableName table,
        DbColumnName column,
        int columnOrdinal,
        object? value
    )
    {
        return $"Cannot order hydrated child rows: table '{table}' column '{column.Value}' at ordinal '{columnOrdinal}' "
            + $"contains {FormatValueAndType(value)} that cannot be converted to an ordinal.";
    }

    private static string FormatValueAndType(object? value) =>
        value is null ? "<null> (type: <null>)" : $"'{value}' (type: {value.GetType().FullName})";

    internal void AttachChild(RowNode child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (child.Parent is not null)
        {
            throw new InvalidOperationException(
                $"Cannot attach row from table '{child.Table}' more than once. "
                    + $"Physical row identity: {PageReconstitutionContext.FormatScopeKey(child.PhysicalRowIdentity)}."
            );
        }

        child.Parent = this;

        if (!_immediateChildrenByTable.TryGetValue(child.Table, out var children))
        {
            children = [];
            _immediateChildrenByTable[child.Table] = children;
        }

        children.Add(child);
    }
}

/// <summary>
/// Per-page link-emission configuration bundled for the reference-writer. <see langword="null"/>
/// on the test-only context build path; non-null when production callers want
/// <c>link.rel</c> / <c>link.href</c> emission on document references.
/// </summary>
internal sealed record LinkEmissionContext(MappingSet MappingSet, IDocumentLinkSlugResolver SlugResolver);

/// <summary>
/// One entry in the page-scoped <c>DocumentId → (DocumentUuid, ResourceKeyId)</c> map sourced
/// from <see cref="HydratedDocumentReferenceLookup"/>.
/// </summary>
internal readonly record struct DocumentLinkLookupEntry(Guid DocumentUuid, short ResourceKeyId);

internal sealed class PageReconstitutionContext
{
    private readonly Dictionary<long, DocumentPageNode> _documentsById;
    private readonly Dictionary<
        DbTableName,
        Dictionary<ScopeKey, RowNode>
    > _rowNodesByTableAndPhysicalIdentity;

    private PageReconstitutionContext(
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyDictionary<long, string> descriptorUrisById,
        IReadOnlyDictionary<long, DocumentLinkLookupEntry> documentLinkLookupById,
        LinkEmissionContext? linkEmission,
        ImmutableArray<DocumentPageNode> documentsInOrder,
        Dictionary<long, DocumentPageNode> documentsById,
        Dictionary<DbTableName, Dictionary<ScopeKey, RowNode>> rowNodesByTableAndPhysicalIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(compiledPlan);
        ArgumentNullException.ThrowIfNull(descriptorUrisById);
        ArgumentNullException.ThrowIfNull(documentLinkLookupById);
        ArgumentNullException.ThrowIfNull(documentsById);
        ArgumentNullException.ThrowIfNull(rowNodesByTableAndPhysicalIdentity);

        CompiledPlan = compiledPlan;
        DescriptorUrisById = descriptorUrisById;
        DocumentLinkLookupById = documentLinkLookupById;
        LinkEmission = linkEmission;
        DocumentsInOrder = documentsInOrder;
        _documentsById = documentsById;
        _rowNodesByTableAndPhysicalIdentity = rowNodesByTableAndPhysicalIdentity;
    }

    public CompiledReconstitutionPlan CompiledPlan { get; }

    public IReadOnlyDictionary<long, string> DescriptorUrisById { get; }

    /// <summary>
    /// Page-scoped <c>DocumentId → (DocumentUuid, ResourceKeyId)</c> map sourced from
    /// <see cref="HydratedDocumentReferenceLookup"/>. Empty when the read plan had no
    /// document-reference auxiliary lookup.
    /// </summary>
    public IReadOnlyDictionary<long, DocumentLinkLookupEntry> DocumentLinkLookupById { get; }

    /// <summary>
    /// Optional link-emission configuration. <see langword="null"/> when the caller is the
    /// test-only adapter or when link emission is otherwise off for this page.
    /// </summary>
    public LinkEmissionContext? LinkEmission { get; }

    public ImmutableArray<DocumentPageNode> DocumentsInOrder { get; }

    public static PageReconstitutionContext Build(ResourceReadPlan readPlan, HydratedPage hydratedPage)
    {
        ArgumentNullException.ThrowIfNull(readPlan);

        return Build(CompiledReconstitutionPlanCache.GetOrBuild(readPlan), hydratedPage);
    }

    public static PageReconstitutionContext Build(
        CompiledReconstitutionPlan compiledPlan,
        HydratedPage hydratedPage
    ) => Build(compiledPlan, hydratedPage, linkEmission: null);

    public static PageReconstitutionContext Build(
        CompiledReconstitutionPlan compiledPlan,
        HydratedPage hydratedPage,
        LinkEmissionContext? linkEmission
    )
    {
        ArgumentNullException.ThrowIfNull(compiledPlan);
        ArgumentNullException.ThrowIfNull(hydratedPage);

        return Build(
            compiledPlan,
            hydratedPage.DocumentMetadata,
            hydratedPage.TableRowsInDependencyOrder,
            BuildDescriptorUriLookup(hydratedPage.DescriptorRowsInPlanOrder),
            BuildDocumentLinkLookup(hydratedPage.DocumentReferenceLookup),
            linkEmission
        );
    }

    internal static PageReconstitutionContext Build(
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyList<DocumentMetadataRow> documentMetadataRows,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyDictionary<long, string> descriptorUrisById
    ) =>
        Build(
            compiledPlan,
            documentMetadataRows,
            tableRowsInDependencyOrder,
            descriptorUrisById,
            documentLinkLookupById: new Dictionary<long, DocumentLinkLookupEntry>(),
            linkEmission: null
        );

    internal static PageReconstitutionContext Build(
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyList<DocumentMetadataRow> documentMetadataRows,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyDictionary<long, string> descriptorUrisById,
        IReadOnlyDictionary<long, DocumentLinkLookupEntry> documentLinkLookupById,
        LinkEmissionContext? linkEmission
    )
    {
        ArgumentNullException.ThrowIfNull(compiledPlan);
        ArgumentNullException.ThrowIfNull(documentMetadataRows);
        ArgumentNullException.ThrowIfNull(tableRowsInDependencyOrder);
        ArgumentNullException.ThrowIfNull(descriptorUrisById);
        ArgumentNullException.ThrowIfNull(documentLinkLookupById);

        var hydratedRowsByTable = BuildHydratedRowsByTable(tableRowsInDependencyOrder);
        var rowNodesByTableAndPhysicalIdentity = new Dictionary<DbTableName, Dictionary<ScopeKey, RowNode>>(
            compiledPlan.TablePlansInDependencyOrder.Length
        );
        var rootRowsByDocumentId = new Dictionary<long, RowNode>(documentMetadataRows.Count);

        ValidateNoUnexpectedHydratedTables(compiledPlan, hydratedRowsByTable);

        foreach (var tablePlan in compiledPlan.TablePlansInDependencyOrder)
        {
            var tableRows = GetHydratedRowsOrThrow(hydratedRowsByTable, tablePlan.Table, compiledPlan);
            var rowNodesByPhysicalIdentity = new Dictionary<ScopeKey, RowNode>(tableRows.Rows.Count);

            foreach (var row in tableRows.Rows)
            {
                var physicalRowIdentity = CreateScopeKey(
                    row,
                    tablePlan.PhysicalRowIdentityOrdinals,
                    tablePlan.Table,
                    "physical row identity"
                );
                var rootDocumentId = ResolveRootDocumentIdOrThrow(tablePlan, row);
                var rowNode = new RowNode(tablePlan, row, physicalRowIdentity, rootDocumentId);

                if (
                    tablePlan.ImmediateParentTable is null
                    && !rootRowsByDocumentId.TryAdd(rootDocumentId, rowNode)
                )
                {
                    throw new InvalidOperationException(
                        $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                            + $"duplicate root row for DocumentId {rootDocumentId} in table '{tablePlan.Table}'."
                    );
                }

                if (!rowNodesByPhysicalIdentity.TryAdd(physicalRowIdentity, rowNode))
                {
                    throw new InvalidOperationException(
                        $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                            + $"table '{tablePlan.Table}' contains duplicate physical row identity "
                            + $"{FormatScopeKey(physicalRowIdentity)}."
                    );
                }

                if (tablePlan.ImmediateParentTable is not null)
                {
                    AttachToImmediateParentOrThrow(
                        compiledPlan,
                        rowNodesByTableAndPhysicalIdentity,
                        rowNode,
                        row
                    );
                }
            }

            rowNodesByTableAndPhysicalIdentity.Add(tablePlan.Table, rowNodesByPhysicalIdentity);

            if (
                tablePlan.ImmediateParentTable is DbTableName parentTable
                && tablePlan.OrdinalColumnOrdinal is int ordinalColumnOrdinal
            )
            {
                EnsureImmediateChildrenInOrdinalOrderOrThrow(
                    compiledPlan,
                    rowNodesByTableAndPhysicalIdentity,
                    parentTable,
                    tablePlan.Table,
                    ordinalColumnOrdinal
                );
            }
        }

        return CreateContextOrThrow(
            compiledPlan,
            descriptorUrisById,
            documentLinkLookupById,
            linkEmission,
            documentMetadataRows,
            rootRowsByDocumentId,
            rowNodesByTableAndPhysicalIdentity
        );
    }

    public DocumentPageNode GetDocumentOrThrow(long documentId)
    {
        if (_documentsById.TryGetValue(documentId, out var document))
        {
            return document;
        }

        throw new KeyNotFoundException(
            $"Page reconstitution context for '{GetResourceDisplayName(CompiledPlan)}' does not contain DocumentId {documentId}."
        );
    }

    public RowNode GetRowOrThrow(DbTableName table, ScopeKey physicalRowIdentity)
    {
        ArgumentNullException.ThrowIfNull(physicalRowIdentity);

        if (
            _rowNodesByTableAndPhysicalIdentity.TryGetValue(table, out var rowsByPhysicalIdentity)
            && rowsByPhysicalIdentity.TryGetValue(physicalRowIdentity, out var rowNode)
        )
        {
            return rowNode;
        }

        throw new KeyNotFoundException(
            $"Page reconstitution context for '{GetResourceDisplayName(CompiledPlan)}' does not contain table '{table}' "
                + $"row {FormatScopeKey(physicalRowIdentity)}."
        );
    }

    public string GetDescriptorUriOrThrow(long descriptorId)
    {
        if (DescriptorUrisById.TryGetValue(descriptorId, out var descriptorUri))
        {
            return descriptorUri;
        }

        throw new KeyNotFoundException(
            $"Page reconstitution context for '{GetResourceDisplayName(CompiledPlan)}' does not contain descriptor ID {descriptorId}."
        );
    }

    internal static string FormatScopeKey(ScopeKey scopeKey)
    {
        ArgumentNullException.ThrowIfNull(scopeKey);

        return $"[{string.Join(", ", scopeKey.Parts.Select(FormatScopeKeyPart))}]";
    }

    private static PageReconstitutionContext CreateContextOrThrow(
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyDictionary<long, string> descriptorUrisById,
        IReadOnlyDictionary<long, DocumentLinkLookupEntry> documentLinkLookupById,
        LinkEmissionContext? linkEmission,
        IReadOnlyList<DocumentMetadataRow> documentMetadataRows,
        IReadOnlyDictionary<long, RowNode> rootRowsByDocumentId,
        Dictionary<DbTableName, Dictionary<ScopeKey, RowNode>> rowNodesByTableAndPhysicalIdentity
    )
    {
        var documentsInOrder = ImmutableArray.CreateBuilder<DocumentPageNode>(documentMetadataRows.Count);
        var documentsById = new Dictionary<long, DocumentPageNode>(documentMetadataRows.Count);
        HashSet<long> metadataDocumentIds = new(documentMetadataRows.Count);

        foreach (var documentMetadata in documentMetadataRows)
        {
            if (!metadataDocumentIds.Add(documentMetadata.DocumentId))
            {
                throw new InvalidOperationException(
                    $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                        + $"duplicate document metadata row for DocumentId {documentMetadata.DocumentId}."
                );
            }

            if (!rootRowsByDocumentId.TryGetValue(documentMetadata.DocumentId, out var rootRow))
            {
                throw new InvalidOperationException(
                    $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                        + $"document metadata row for DocumentId {documentMetadata.DocumentId} has no matching root row."
                );
            }

            var documentPageNode = new DocumentPageNode(documentMetadata, rootRow);
            documentsInOrder.Add(documentPageNode);
            documentsById.Add(documentMetadata.DocumentId, documentPageNode);
        }

        var extraRootDocumentIds = rootRowsByDocumentId
            .Keys.Where(documentId => !metadataDocumentIds.Contains(documentId))
            .OrderBy(static documentId => documentId)
            .ToArray();

        if (extraRootDocumentIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                    + "root rows were hydrated for document ids not present in page metadata: "
                    + $"[{string.Join(", ", extraRootDocumentIds)}]."
            );
        }

        return new PageReconstitutionContext(
            compiledPlan,
            descriptorUrisById,
            documentLinkLookupById,
            linkEmission,
            documentsInOrder.MoveToImmutable(),
            documentsById,
            rowNodesByTableAndPhysicalIdentity
        );
    }

    private static IReadOnlyDictionary<long, DocumentLinkLookupEntry> BuildDocumentLinkLookup(
        HydratedDocumentReferenceLookup? documentReferenceLookup
    )
    {
        if (documentReferenceLookup is null || documentReferenceLookup.Rows.Count == 0)
        {
            return new Dictionary<long, DocumentLinkLookupEntry>();
        }

        Dictionary<long, DocumentLinkLookupEntry> lookup = new(documentReferenceLookup.Rows.Count);

        foreach (var row in documentReferenceLookup.Rows)
        {
            var entry = new DocumentLinkLookupEntry(row.DocumentUuid, row.ResourceKeyId);

            if (lookup.TryGetValue(row.DocumentId, out var existing))
            {
                if (
                    existing.DocumentUuid != entry.DocumentUuid
                    || existing.ResourceKeyId != entry.ResourceKeyId
                )
                {
                    throw new InvalidOperationException(
                        "Cannot build page reconstitution context: document-reference lookup returned "
                            + $"conflicting rows for DocumentId {row.DocumentId}."
                    );
                }

                continue;
            }

            lookup.Add(row.DocumentId, entry);
        }

        return lookup;
    }

    private static IReadOnlyDictionary<long, string> BuildDescriptorUriLookup(
        IReadOnlyList<HydratedDescriptorRows> descriptorRowsInPlanOrder
    )
    {
        var descriptorRowCount = 0;

        foreach (var descriptorRows in descriptorRowsInPlanOrder)
        {
            ArgumentNullException.ThrowIfNull(descriptorRows);

            descriptorRowCount += descriptorRows.Rows.Count;
        }

        Dictionary<long, string> descriptorUrisById = new(descriptorRowCount);

        foreach (var descriptorRows in descriptorRowsInPlanOrder)
        {
            foreach (var row in descriptorRows.Rows)
            {
                if (descriptorUrisById.TryGetValue(row.DescriptorId, out var existingUri))
                {
                    if (!string.Equals(existingUri, row.Uri, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Cannot build page reconstitution context: descriptor hydration returned conflicting "
                                + $"URIs for descriptor ID {row.DescriptorId}."
                        );
                    }

                    continue;
                }

                descriptorUrisById.Add(row.DescriptorId, row.Uri);
            }
        }

        return descriptorUrisById;
    }

    private static IReadOnlyDictionary<DbTableName, HydratedTableRows> BuildHydratedRowsByTable(
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder
    )
    {
        Dictionary<DbTableName, HydratedTableRows> hydratedRowsByTable = new(
            tableRowsInDependencyOrder.Count
        );

        foreach (var tableRows in tableRowsInDependencyOrder)
        {
            if (!hydratedRowsByTable.TryAdd(tableRows.TableModel.Table, tableRows))
            {
                throw new InvalidOperationException(
                    $"Cannot build page reconstitution context: duplicate hydrated row set for table '{tableRows.TableModel.Table}'."
                );
            }
        }

        return hydratedRowsByTable;
    }

    private static void ValidateNoUnexpectedHydratedTables(
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyDictionary<DbTableName, HydratedTableRows> hydratedRowsByTable
    )
    {
        var unexpectedTables = hydratedRowsByTable
            .Keys.Where(table => !compiledPlan.TablePlansByTable.ContainsKey(table))
            .OrderBy(static table => table.ToString(), StringComparer.Ordinal)
            .ToArray();

        if (unexpectedTables.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                + $"hydrated rows contained unexpected tables [{string.Join(", ", unexpectedTables)}]."
        );
    }

    private static HydratedTableRows GetHydratedRowsOrThrow(
        IReadOnlyDictionary<DbTableName, HydratedTableRows> hydratedRowsByTable,
        DbTableName table,
        CompiledReconstitutionPlan compiledPlan
    )
    {
        if (hydratedRowsByTable.TryGetValue(table, out var tableRows))
        {
            return tableRows;
        }

        throw new InvalidOperationException(
            $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                + $"hydrated rows did not contain required table '{table}'."
        );
    }

    private static void AttachToImmediateParentOrThrow(
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyDictionary<DbTableName, Dictionary<ScopeKey, RowNode>> rowNodesByTableAndPhysicalIdentity,
        RowNode rowNode,
        object?[] row
    )
    {
        var tablePlan = rowNode.TablePlan;
        var parentTable =
            tablePlan.ImmediateParentTable
            ?? throw new InvalidOperationException(
                $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                    + $"table '{tablePlan.Table}' did not resolve an immediate parent table."
            );

        if (
            !rowNodesByTableAndPhysicalIdentity.TryGetValue(parentTable, out var parentRowsByPhysicalIdentity)
        )
        {
            throw new InvalidOperationException(
                $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                    + $"parent table '{parentTable}' was not available before child table '{tablePlan.Table}'."
            );
        }

        var parentPhysicalIdentity = CreateScopeKey(
            row,
            tablePlan.ImmediateParentScopeLocatorOrdinals,
            tablePlan.Table,
            "immediate parent locator"
        );

        if (!parentRowsByPhysicalIdentity.TryGetValue(parentPhysicalIdentity, out var parentRowNode))
        {
            throw new InvalidOperationException(
                $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                    + $"orphaned row in table '{tablePlan.Table}' with immediate parent table '{parentTable}' and "
                    + $"locator {FormatScopeKey(parentPhysicalIdentity)}."
            );
        }

        if (parentRowNode.RootDocumentId != rowNode.RootDocumentId)
        {
            throw new InvalidOperationException(
                $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                    + $"row in table '{tablePlan.Table}' resolved to parent table '{parentTable}', but the child root "
                    + $"document id {rowNode.RootDocumentId} did not match parent root document id {parentRowNode.RootDocumentId}."
            );
        }

        parentRowNode.AttachChild(rowNode);
    }

    private static void EnsureImmediateChildrenInOrdinalOrderOrThrow(
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyDictionary<DbTableName, Dictionary<ScopeKey, RowNode>> rowNodesByTableAndPhysicalIdentity,
        DbTableName parentTable,
        DbTableName childTable,
        int ordinalColumnOrdinal
    )
    {
        if (
            !rowNodesByTableAndPhysicalIdentity.TryGetValue(parentTable, out var parentRowsByPhysicalIdentity)
        )
        {
            throw new InvalidOperationException(
                $"Cannot build page reconstitution context for '{GetResourceDisplayName(compiledPlan)}': "
                    + $"parent table '{parentTable}' was not available when ordering child table '{childTable}'."
            );
        }

        foreach (var parentRowNode in parentRowsByPhysicalIdentity.Values)
        {
            parentRowNode.EnsureImmediateChildrenInOrdinalOrder(childTable, ordinalColumnOrdinal);
        }
    }

    private static long ResolveRootDocumentIdOrThrow(TableReconstitutionPlan tablePlan, object?[] row)
    {
        var rootScopeKey = CreateScopeKey(
            row,
            tablePlan.RootScopeLocatorOrdinals,
            tablePlan.Table,
            "root scope locator"
        );

        if (rootScopeKey.Parts.Length == 1 && rootScopeKey.Parts[0] is long documentId)
        {
            return documentId;
        }

        throw new InvalidOperationException(
            $"Cannot build page reconstitution context: table '{tablePlan.Table}' root scope locator "
                + $"{FormatScopeKey(rootScopeKey)} could not be resolved to a single DocumentId."
        );
    }

    private static ScopeKey CreateScopeKey(
        object?[] row,
        ImmutableArray<int> ordinals,
        DbTableName table,
        string scopeDescription
    )
    {
        if (ordinals.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                $"Cannot build page reconstitution context: table '{table}' does not define a {scopeDescription}."
            );
        }

        var parts = new object?[ordinals.Length];

        for (var index = 0; index < ordinals.Length; index++)
        {
            parts[index] = row[ordinals[index]];
        }

        return new ScopeKey(parts);
    }

    private static string GetResourceDisplayName(CompiledReconstitutionPlan compiledPlan) =>
        $"{compiledPlan.ReadPlan.Model.Resource.ProjectName}.{compiledPlan.ReadPlan.Model.Resource.ResourceName}";

    private static string FormatScopeKeyPart(object? part) =>
        part switch
        {
            null => "null",
            string stringValue => $"\"{stringValue}\"",
            _ => part.ToString() ?? "<unknown>",
        };
}
