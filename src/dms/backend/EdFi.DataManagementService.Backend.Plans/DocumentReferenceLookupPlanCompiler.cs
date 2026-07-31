// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Resolution target for one document-reference binding's <c>TargetResource</c>. Concrete
/// targets resolve to the target resource's root table plus a compile-time
/// <c>"{ProjectName}:{ResourceName}"</c> discriminator literal; abstract targets resolve to the
/// <c>{Abstract}Identity</c> table, whose stored <c>Discriminator</c> column carries the concrete
/// subclass — so <see cref="DiscriminatorLiteral"/> is <see langword="null"/> and the emitted
/// branch selects <c>tgt."Discriminator"</c> instead.
/// </summary>
/// <param name="LookupTable">
/// Table the branch joins to resolve <c>DocumentUuid</c>: the target root table (concrete) or the
/// <c>{Abstract}Identity</c> table (abstract). Referenced by literal name — the compiler is handed
/// a hydration-projected model whose <c>tablesByName</c> covers only the OWNING tables.
/// </param>
/// <param name="DiscriminatorLiteral">
/// <c>"{ProjectName}:{ResourceName}"</c> for a concrete target; <see langword="null"/> for an
/// abstract target.
/// </param>
internal sealed record DocumentReferenceLookupTarget(DbTableName LookupTable, string? DiscriminatorLiteral);

/// <summary>
/// Compiles the document-reference auxiliary lookup plan from
/// <see cref="RelationalResourceModel.DocumentReferenceBindings"/>. Emits a
/// SELECT that returns one <c>(DocumentId, DocumentUuid, Discriminator)</c>
/// row per distinct <c>..._DocumentId</c> value reachable from the source tables. Each UNION
/// branch joins its own target — the target root table for a concrete reference, the
/// <c>{Abstract}Identity</c> table for an abstract one — so the lookup never touches
/// <c>dms.Document</c>.
/// </summary>
internal sealed class DocumentReferenceLookupPlanCompiler(SqlDialect dialect)
{
    private const string TargetAliasPrefix = "tgt";

    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly DbColumnName _documentUuidColumn = new("DocumentUuid");
    private static readonly DbColumnName _discriminatorColumn = new("Discriminator");

    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);
    private readonly IPlanSqlDialect _planSqlDialect = PlanSqlDialectFactory.Create(dialect);

    public DocumentReferenceLookupPlan? Compile(
        RelationalResourceModel resourceModel,
        KeysetTableContract keysetTable,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName,
        IReadOnlyDictionary<QualifiedResourceName, DocumentReferenceLookupTarget> targetsByResource
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(keysetTable);
        ArgumentNullException.ThrowIfNull(tablesByName);
        ArgumentNullException.ThrowIfNull(targetsByResource);

        if (resourceModel.DocumentReferenceBindings.Count == 0)
        {
            return null;
        }

        var deduplicatedSources = CompileDeduplicatedSources(
            resourceModel,
            tablesByName,
            targetsByResource
        );

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
                DiscriminatorOrdinal: 2
            ),
            SourcesInOrder: compiledSources,
            SelectBySingleDocumentSql: _planSqlDialect.SupportsSingleDocumentHydration
                ? EmitSelectBySingleDocumentSql(deduplicatedSources)
                : null
        );
    }

    private static IReadOnlyList<DocumentReferenceLookupSqlSource> CompileDeduplicatedSources(
        RelationalResourceModel resourceModel,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName,
        IReadOnlyDictionary<QualifiedResourceName, DocumentReferenceLookupTarget> targetsByResource
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
                Target: ResolveTargetOrThrow(tableModel.Table, binding, targetsByResource),
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

        var tableAliasAllocator = PlanNamingConventions.CreateTableAliasAllocator();
        var writer = new SqlWriter(_sqlDialect);

        writer.AppendLine("SELECT");

        using (writer.Indent())
        {
            writer.Append($"{projectionAlias}.").AppendQuoted(_documentIdColumn.Value).AppendLine(",");
            writer.Append($"{projectionAlias}.").AppendQuoted(_documentUuidColumn.Value).AppendLine(",");
            writer.Append($"{projectionAlias}.").AppendQuoted(_discriminatorColumn.Value).AppendLine();
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
                    var targetAlias = $"{TargetAliasPrefix}{index}";

                    writer.Append("SELECT ");

                    if (sqlSources.Count == 1)
                    {
                        writer.Append("DISTINCT ");
                    }

                    AppendQualifiedColumn(writer, tableAlias, sqlSource.FkColumn);
                    writer.Append(" AS ").AppendQuoted(_documentIdColumn.Value).Append(", ");
                    AppendQualifiedColumn(writer, targetAlias, _documentUuidColumn);
                    writer.Append(" AS ").AppendQuoted(_documentUuidColumn.Value).Append(", ");
                    AppendDiscriminatorExpression(writer, targetAlias, sqlSource.Target);
                    writer.Append(" AS ").AppendQuoted(_discriminatorColumn.Value).AppendLine();
                    writer.Append("FROM ").AppendTable(tableModel.Table).AppendLine($" {tableAlias}");

                    // The target join replaces the former outer dms.Document join. It stays an
                    // INNER JOIN so a dangling FK (target row concurrently deleted) drops the row
                    // and reconstitution suppresses the link, exactly as the dms.Document join did.
                    writer
                        .Append("INNER JOIN ")
                        .AppendTable(sqlSource.Target.LookupTable)
                        .Append($" {targetAlias} ON ");
                    AppendQualifiedColumn(writer, targetAlias, _documentIdColumn);
                    writer.Append(" = ");
                    AppendQualifiedColumn(writer, tableAlias, sqlSource.FkColumn);
                    writer.AppendLine();

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

        writer.AppendLine("ORDER BY");

        using (writer.Indent())
        {
            writer.Append($"{projectionAlias}.").AppendQuoted(_documentIdColumn.Value);
            writer.AppendLine(" ASC");
        }

        writer.AppendLine(";");

        return writer.ToString();
    }

    /// <summary>
    /// Appends the branch's third select-list expression: a compile-time string literal for a
    /// concrete target, or the <c>{Abstract}Identity</c> table's stored <c>Discriminator</c>
    /// column for an abstract target (which resolves to the concrete subclass).
    /// </summary>
    private void AppendDiscriminatorExpression(
        SqlWriter writer,
        string targetAlias,
        DocumentReferenceLookupTarget target
    )
    {
        if (target.DiscriminatorLiteral is { } discriminatorLiteral)
        {
            writer.Append(_sqlDialect.RenderStringLiteral(discriminatorLiteral));
            return;
        }

        AppendQualifiedColumn(writer, targetAlias, _discriminatorColumn);
    }

    private static DocumentReferenceLookupTarget ResolveTargetOrThrow(
        DbTableName table,
        DocumentReferenceBinding binding,
        IReadOnlyDictionary<QualifiedResourceName, DocumentReferenceLookupTarget> targetsByResource
    )
    {
        if (targetsByResource.TryGetValue(binding.TargetResource, out var target))
        {
            return target;
        }

        throw new InvalidOperationException(
            $"Cannot compile document-reference lookup plan for '{table}': "
                + $"document-reference binding '{binding.ReferenceObjectPath.Canonical}' target resource "
                + $"'{binding.TargetResource.ProjectName}.{binding.TargetResource.ResourceName}' is not "
                + "present in the document-reference lookup target map."
        );
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
        DocumentReferenceLookupTarget Target,
        int TableDependencyOrdinal,
        int FkColumnOrdinal
    );
}
