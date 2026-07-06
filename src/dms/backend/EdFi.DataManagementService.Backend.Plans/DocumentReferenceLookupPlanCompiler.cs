// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles the document-reference auxiliary lookup plan from
/// <see cref="RelationalResourceModel.DocumentReferenceBindings"/>. Emits a
/// SELECT that returns one <c>(DocumentId, DocumentUuid, ResourceKeyId)</c>
/// row per distinct <c>..._DocumentId</c> value reachable from the source tables.
/// </summary>
internal sealed class DocumentReferenceLookupPlanCompiler(SqlDialect dialect)
{
    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly DbColumnName _documentUuidColumn = new("DocumentUuid");
    private static readonly DbColumnName _resourceKeyIdColumn = new("ResourceKeyId");

    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);
    private readonly IPlanSqlDialect _planSqlDialect = PlanSqlDialectFactory.Create(dialect);

    public DocumentReferenceLookupPlan? Compile(
        RelationalResourceModel resourceModel,
        KeysetTableContract keysetTable,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(keysetTable);
        ArgumentNullException.ThrowIfNull(tablesByName);

        if (resourceModel.DocumentReferenceBindings.Count == 0)
        {
            return null;
        }

        var deduplicatedSources = CompileDeduplicatedSources(resourceModel, tablesByName);

        var compiledSources = deduplicatedSources
            .Select(source => new DocumentReferenceLookupSource(
                Table: source.TableModel.Table,
                FkColumn: source.FkColumn
            ))
            .ToArray();

        return new DocumentReferenceLookupPlan(
            SelectByKeysetSql: EmitSelectByKeysetSql(deduplicatedSources, keysetTable),
            ResultShape: new DocumentReferenceLookupResultShape(
                DocumentIdOrdinal: 0,
                DocumentUuidOrdinal: 1,
                ResourceKeyIdOrdinal: 2
            ),
            SourcesInOrder: compiledSources,
            SelectBySingleDocumentSql: _planSqlDialect.SupportsSingleDocumentHydration
                ? EmitSelectBySingleDocumentSql(deduplicatedSources)
                : null
        );
    }

    private static IReadOnlyList<DocumentReferenceLookupSqlSource> CompileDeduplicatedSources(
        RelationalResourceModel resourceModel,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        var tableDependencyOrder = resourceModel
            .TablesInDependencyOrder.Select((table, index) => (table.Table, index))
            .ToDictionary(entry => entry.Table, entry => entry.index);

        Dictionary<DocumentReferenceLookupSqlSourceKey, DocumentReferenceLookupSqlSource> sourcesByKey = [];

        foreach (var binding in resourceModel.DocumentReferenceBindings)
        {
            var tableModel = ProjectionMetadataResolver.ResolveTableModelOrThrow(
                binding.Table,
                tablesByName,
                missingTable => new InvalidOperationException(
                    $"Cannot compile document-reference lookup plan for '{missingTable}': owning table is not present in TablesInDependencyOrder."
                )
            );
            var fkColumnModel = ResolveFkColumnOrThrow(tableModel, binding);

            ValidateDocumentFkColumnKindOrThrow(fkColumnModel, tableModel.Table, binding);

            var sqlSource = new DocumentReferenceLookupSqlSource(
                TableModel: tableModel,
                FkColumn: fkColumnModel.ColumnName,
                TableDependencyOrdinal: ResolveTableDependencyOrdinalOrThrow(
                    tableDependencyOrder,
                    binding.Table
                ),
                FkColumnOrdinal: ProjectionMetadataResolver.ResolveTableColumnOrdinalOrThrow(
                    tableModel,
                    fkColumnModel.ColumnName,
                    missingColumn => new InvalidOperationException(
                        $"Cannot compile document-reference lookup plan for '{tableModel.Table}': "
                            + $"document-reference binding '{binding.ReferenceObjectPath.Canonical}' FK column "
                            + $"'{missingColumn.Value}' does not exist in table columns."
                    )
                )
            );

            sourcesByKey.TryAdd(
                new DocumentReferenceLookupSqlSourceKey(tableModel.Table, fkColumnModel.ColumnName),
                sqlSource
            );
        }

        return sourcesByKey
            .Values.OrderBy(source => source.TableDependencyOrdinal)
            .ThenBy(source => source.FkColumnOrdinal)
            .ToArray();
    }

    private string EmitSelectByKeysetSql(
        IReadOnlyList<DocumentReferenceLookupSqlSource> sqlSources,
        KeysetTableContract keysetTable
    )
    {
        return EmitSelectSql(sqlSources, ProjectionSourceFilter.Keyset(keysetTable));
    }

    private string EmitSelectBySingleDocumentSql(IReadOnlyList<DocumentReferenceLookupSqlSource> sqlSources)
    {
        return EmitSelectSql(sqlSources, ProjectionSourceFilter.SingleDocument);
    }

    private string EmitSelectSql(
        IReadOnlyList<DocumentReferenceLookupSqlSource> sqlSources,
        ProjectionSourceFilter sourceFilter
    )
    {
        const string projectionAlias = "p";

        var documentAlias = PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Document);
        var tableAliasAllocator = PlanNamingConventions.CreateTableAliasAllocator();
        var writer = new SqlWriter(_sqlDialect);

        writer.AppendLine("SELECT");

        using (writer.Indent())
        {
            writer.Append($"{documentAlias}.").AppendQuoted(_documentIdColumn.Value).AppendLine(",");
            writer.Append($"{documentAlias}.").AppendQuoted(_documentUuidColumn.Value).AppendLine(",");
            writer.Append($"{documentAlias}.").AppendQuoted(_resourceKeyIdColumn.Value).AppendLine();
        }

        writer.AppendLine("FROM");

        using (writer.Indent())
        {
            writer.AppendLine("(");

            using (writer.Indent())
            {
                for (var index = 0; index < sqlSources.Count; index++)
                {
                    var sqlSource = sqlSources[index];
                    var tableModel = sqlSource.TableModel;
                    var tableAlias = tableAliasAllocator.AllocateNext();

                    writer.Append("SELECT ");

                    if (sqlSources.Count == 1)
                    {
                        writer.Append("DISTINCT ");
                    }

                    AppendQualifiedColumn(writer, tableAlias, sqlSource.FkColumn);
                    writer.Append(" AS ").AppendQuoted(_documentIdColumn.Value).AppendLine();
                    writer.Append("FROM ").AppendTable(tableModel.Table).AppendLine($" {tableAlias}");
                    ProjectionSourceFilterSql.Append(
                        writer,
                        tableModel,
                        tableAlias,
                        sqlSource.FkColumn,
                        sourceFilter,
                        "document-reference lookup plan"
                    );

                    if (index + 1 < sqlSources.Count)
                    {
                        writer.AppendLine("UNION");
                    }
                }
            }

            writer.AppendLine($") {projectionAlias}");
        }

        writer.Append("INNER JOIN ").AppendTable(_documentTable).Append($" {documentAlias} ON ");
        AppendQualifiedColumn(writer, documentAlias, _documentIdColumn);
        writer.Append(" = ");
        writer.Append($"{projectionAlias}.").AppendQuoted(_documentIdColumn.Value).AppendLine();
        writer.AppendLine("ORDER BY");

        using (writer.Indent())
        {
            writer.Append($"{documentAlias}.").AppendQuoted(_documentIdColumn.Value);
            writer.AppendLine(" ASC");
        }

        writer.AppendLine(";");

        return writer.ToString();
    }

    private static DbColumnModel ResolveFkColumnOrThrow(
        DbTableModel tableModel,
        DocumentReferenceBinding binding
    )
    {
        var contextDescription =
            $"Cannot compile document-reference lookup plan for '{tableModel.Table}': "
            + $"document-reference binding '{binding.ReferenceObjectPath.Canonical}' FK column";

        return ProjectionMetadataResolver.ResolveTableColumnOrThrow(
            tableModel,
            binding.FkColumn,
            missingColumn => new InvalidOperationException(
                $"{contextDescription} '{missingColumn.Value}' does not exist in table columns."
            )
        );
    }

    private static void ValidateDocumentFkColumnKindOrThrow(
        DbColumnModel columnModel,
        DbTableName table,
        DocumentReferenceBinding binding
    )
    {
        if (columnModel.Kind is ColumnKind.DocumentFk)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot compile document-reference lookup plan for '{table}': "
                + $"document-reference binding '{binding.ReferenceObjectPath.Canonical}' FK column "
                + $"'{columnModel.ColumnName.Value}' has kind '{columnModel.Kind}'. "
                + $"Expected '{ColumnKind.DocumentFk}'."
        );
    }

    private static int ResolveTableDependencyOrdinalOrThrow(
        IReadOnlyDictionary<DbTableName, int> tableDependencyOrder,
        DbTableName table
    )
    {
        if (tableDependencyOrder.TryGetValue(table, out var tableOrdinal))
        {
            return tableOrdinal;
        }

        throw new InvalidOperationException(
            $"Cannot compile document-reference lookup plan for '{table}': owning table is not present in TablesInDependencyOrder."
        );
    }

    private static void AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName columnName)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(columnName.Value);
    }

    private readonly record struct DocumentReferenceLookupSqlSourceKey(
        DbTableName Table,
        DbColumnName FkColumn
    );

    private sealed record DocumentReferenceLookupSqlSource(
        DbTableModel TableModel,
        DbColumnName FkColumn,
        int TableDependencyOrdinal,
        int FkColumnOrdinal
    );
}
