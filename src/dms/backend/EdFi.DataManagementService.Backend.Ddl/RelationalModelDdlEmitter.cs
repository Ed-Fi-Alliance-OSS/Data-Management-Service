// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits dialect-specific DDL (schemas, tables, indexes, and triggers) from a derived relational model set.
/// </summary>
public sealed class RelationalModelDdlEmitter
{
    private readonly ISqlDialectRules _dialectRules;

    /// <summary>
    /// Initializes a new DDL emitter using the specified SQL dialect rules.
    /// </summary>
    /// <param name="dialectRules">The dialect rules used for quoting and scalar type defaults.</param>
    public RelationalModelDdlEmitter(ISqlDialectRules dialectRules)
    {
        ArgumentNullException.ThrowIfNull(dialectRules);
        _dialectRules = dialectRules;
    }

    /// <summary>
    /// Builds a SQL script that creates all schemas, tables, indexes, and triggers in the model set.
    /// </summary>
    /// <param name="modelSet">The derived relational model set to emit.</param>
    /// <returns>The emitted DDL script.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the model set dialect does not match the emitter dialect rules.
    /// </exception>
    public string Emit(DerivedRelationalModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        if (modelSet.Dialect != _dialectRules.Dialect)
        {
            throw new InvalidOperationException(
                $"Dialect mismatch: model={modelSet.Dialect}, rules={_dialectRules.Dialect}."
            );
        }

        var builder = new StringBuilder();

        AppendSchemas(builder, modelSet.ProjectSchemasInEndpointOrder);
        AppendTables(builder, modelSet.ConcreteResourcesInNameOrder);
        AppendIndexes(builder, modelSet.IndexesInCreateOrder);
        var tablesByName = BuildTableLookup(modelSet);
        AppendTriggers(builder, modelSet.TriggersInCreateOrder, tablesByName);

        return builder.ToString();
    }

    /// <summary>
    /// Appends <c>CREATE SCHEMA</c> statements for each project schema.
    /// </summary>
    private void AppendSchemas(StringBuilder builder, IReadOnlyList<ProjectSchemaInfo> schemas)
    {
        foreach (var schema in schemas)
        {
            builder.Append("CREATE SCHEMA ");
            builder.Append(Quote(schema.PhysicalSchema));
            builder.AppendLine(";");
        }

        if (schemas.Count > 0)
        {
            builder.AppendLine();
        }
    }

    /// <summary>
    /// Appends <c>CREATE TABLE</c> statements for each table in each concrete resource model.
    /// </summary>
    private void AppendTables(StringBuilder builder, IReadOnlyList<ConcreteResourceModel> resources)
    {
        foreach (var resource in resources)
        {
            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                AppendCreateTable(builder, table);
            }
        }
    }

    /// <summary>
    /// Appends a <c>CREATE TABLE</c> statement including columns, key, and table constraints.
    /// </summary>
    private void AppendCreateTable(StringBuilder builder, DbTableModel table)
    {
        builder.Append("CREATE TABLE ");
        builder.Append(Quote(table.Table));
        builder.AppendLine(" (");

        var definitions = new List<string>();

        foreach (var column in table.Columns)
        {
            var type = ResolveColumnType(column);
            var nullability = column.IsNullable ? "NULL" : "NOT NULL";
            definitions.Add($"{Quote(column.ColumnName)} {type} {nullability}");
        }

        if (table.Key.Columns.Count > 0)
        {
            definitions.Add(
                $"CONSTRAINT {Quote(ResolvePrimaryKeyConstraintName(table))} PRIMARY KEY ({FormatColumnList(table.Key.Columns)})"
            );
        }

        foreach (var constraint in table.Constraints)
        {
            definitions.Add(FormatConstraint(constraint));
        }

        for (var i = 0; i < definitions.Count; i++)
        {
            builder.Append("    ");
            builder.Append(definitions[i]);

            if (i < definitions.Count - 1)
            {
                builder.Append(',');
            }

            builder.AppendLine();
        }

        builder.AppendLine(");");
        builder.AppendLine();
    }

    /// <summary>
    /// Appends <c>CREATE INDEX</c> statements for each index in create-order.
    /// </summary>
    private void AppendIndexes(StringBuilder builder, IReadOnlyList<DbIndexInfo> indexes)
    {
        foreach (var index in indexes)
        {
            var unique = index.IsUnique ? "UNIQUE " : string.Empty;
            builder.Append("CREATE ");
            builder.Append(unique);
            builder.Append("INDEX ");
            builder.Append(Quote(index.Name));
            builder.Append(" ON ");
            builder.Append(Quote(index.Table));
            builder.Append(" (");
            builder.Append(FormatColumnList(index.KeyColumns));
            builder.AppendLine(");");
            builder.AppendLine();
        }
    }

    /// <summary>
    /// Builds a lookup by table name for all concrete and abstract identity table models.
    /// </summary>
    private static IReadOnlyDictionary<DbTableName, DbTableModel> BuildTableLookup(
        DerivedRelationalModelSet modelSet
    )
    {
        Dictionary<DbTableName, DbTableModel> tablesByName = new();

        foreach (var resource in modelSet.ConcreteResourcesInNameOrder)
        {
            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                tablesByName.TryAdd(table.Table, table);
            }
        }

        foreach (var abstractIdentityTable in modelSet.AbstractIdentityTablesInNameOrder)
        {
            tablesByName.TryAdd(abstractIdentityTable.TableModel.Table, abstractIdentityTable.TableModel);
        }

        return tablesByName;
    }

    /// <summary>
    /// Appends <c>CREATE TRIGGER</c> statements for each trigger in create-order.
    /// </summary>
    private void AppendTriggers(
        StringBuilder builder,
        IReadOnlyList<DbTriggerInfo> triggers,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        foreach (var trigger in triggers)
        {
            builder.Append("CREATE TRIGGER ");
            builder.Append(FormatTriggerName(trigger));
            builder.Append(" ON ");
            builder.Append(Quote(trigger.TriggerTable));
            builder.Append(' ');
            builder.AppendLine(BuildTriggerBody(trigger, tablesByName));
            builder.AppendLine();
        }
    }

    /// <summary>
    /// Builds a dialect-specific trigger body statement.
    /// </summary>
    private string BuildTriggerBody(
        DbTriggerInfo trigger,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        return _dialectRules.Dialect switch
        {
            SqlDialect.Pgsql => BuildPgsqlTriggerBody(trigger, tablesByName),
            SqlDialect.Mssql => BuildMssqlTriggerBody(trigger, tablesByName),
            _ => throw new ArgumentOutOfRangeException(
                nameof(_dialectRules.Dialect),
                _dialectRules.Dialect,
                "Unsupported SQL dialect."
            ),
        };
    }

    /// <summary>
    /// Builds a PostgreSQL trigger body statement.
    /// </summary>
    private string BuildPgsqlTriggerBody(
        DbTriggerInfo trigger,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        if (!TriggerNeedsIdentityDiffCompare(trigger))
        {
            return $"EXECUTE FUNCTION {Quote("noop")}();";
        }

        var triggerTable = ResolveTriggerTable(trigger, tablesByName);
        var identityDiffPredicate = BuildIdentityDiffPredicate(
            trigger,
            triggerTable,
            oldRowAlias: "OLD",
            newRowAlias: "NEW"
        );

        return $"WHEN ({identityDiffPredicate}) EXECUTE FUNCTION {Quote("noop")}();";
    }

    /// <summary>
    /// Builds a SQL Server trigger body statement.
    /// </summary>
    private string BuildMssqlTriggerBody(
        DbTriggerInfo trigger,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        if (!TriggerNeedsIdentityDiffCompare(trigger))
        {
            return """
AS
BEGIN
    SET NOCOUNT ON;
END;
""";
        }

        if (trigger.KeyColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Trigger '{trigger.Name.Value}' on '{trigger.TriggerTable}' requires key columns for identity diff comparison."
            );
        }

        var triggerTable = ResolveTriggerTable(trigger, tablesByName);
        var keyJoinPredicate = BuildKeyJoinPredicate(trigger.KeyColumns, "i", "d");
        var identityDiffPredicate = BuildIdentityDiffPredicate(
            trigger,
            triggerTable,
            oldRowAlias: "d",
            newRowAlias: "i"
        );
        var firstKeyColumn = trigger.KeyColumns[0];

        return $$"""
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        FULL OUTER JOIN deleted d
            ON {{keyJoinPredicate}}
        WHERE {{QualifyColumn("i", firstKeyColumn)}} IS NULL
            OR {{QualifyColumn("d", firstKeyColumn)}} IS NULL
            OR {{identityDiffPredicate}}
    )
    BEGIN
    END
END;
""";
    }

    /// <summary>
    /// Resolves the trigger table model required for storage-aware identity expressions.
    /// </summary>
    private static DbTableModel ResolveTriggerTable(
        DbTriggerInfo trigger,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        if (tablesByName.TryGetValue(trigger.TriggerTable, out var triggerTable))
        {
            return triggerTable;
        }

        throw new InvalidOperationException(
            $"Trigger '{trigger.Name.Value}' referenced table '{trigger.TriggerTable}' that was not found in the model set."
        );
    }

    /// <summary>
    /// Determines whether a trigger kind requires identity value-diff comparison logic.
    /// </summary>
    private static bool TriggerNeedsIdentityDiffCompare(DbTriggerInfo trigger)
    {
        return trigger.Kind is not DbTriggerKind.IdentityPropagationFallback
            && trigger.IdentityProjectionColumns.Count > 0;
    }

    /// <summary>
    /// Builds an identity value-diff predicate by expanding each identity projection column through storage metadata.
    /// </summary>
    private string BuildIdentityDiffPredicate(
        DbTriggerInfo trigger,
        DbTableModel triggerTable,
        string oldRowAlias,
        string newRowAlias
    )
    {
        return string.Join(
            " OR ",
            trigger.IdentityProjectionColumns.Select(identityColumn =>
            {
                var oldValueExpression = BuildIdentityValueExpression(
                    trigger,
                    triggerTable,
                    identityColumn,
                    oldRowAlias
                );
                var newValueExpression = BuildIdentityValueExpression(
                    trigger,
                    triggerTable,
                    identityColumn,
                    newRowAlias
                );

                return BuildNullSafeDifferencePredicate(oldValueExpression, newValueExpression);
            })
        );
    }

    /// <summary>
    /// Builds the storage-aware SQL value expression for an identity projection column.
    /// </summary>
    private string BuildIdentityValueExpression(
        DbTriggerInfo trigger,
        DbTableModel triggerTable,
        DbColumnName identityColumn,
        string rowAlias
    )
    {
        var column = triggerTable.Columns.FirstOrDefault(c => c.ColumnName == identityColumn);

        if (column is null)
        {
            throw new InvalidOperationException(
                $"Trigger '{trigger.Name.Value}' on '{trigger.TriggerTable}' referenced identity projection column "
                    + $"'{identityColumn.Value}' that was not found on the trigger table."
            );
        }

        return column.Storage switch
        {
            ColumnStorage.Stored => QualifyColumn(rowAlias, identityColumn),
            ColumnStorage.UnifiedAlias unifiedAlias => BuildUnifiedAliasExpression(rowAlias, unifiedAlias),
            _ => throw new ArgumentOutOfRangeException(
                nameof(column.Storage),
                column.Storage,
                "Unsupported column storage."
            ),
        };
    }

    /// <summary>
    /// Builds the value expression for a unified alias column, including optional presence gating.
    /// </summary>
    private string BuildUnifiedAliasExpression(string rowAlias, ColumnStorage.UnifiedAlias unifiedAlias)
    {
        var canonicalExpression = QualifyColumn(rowAlias, unifiedAlias.CanonicalColumn);

        if (unifiedAlias.PresenceColumn is not { } presenceColumn)
        {
            return canonicalExpression;
        }

        return $"CASE WHEN {QualifyColumn(rowAlias, presenceColumn)} IS NULL THEN NULL ELSE {canonicalExpression} END";
    }

    /// <summary>
    /// Builds a null-safe value-diff predicate for a pair of SQL expressions.
    /// </summary>
    private string BuildNullSafeDifferencePredicate(string oldValueExpression, string newValueExpression)
    {
        return _dialectRules.Dialect switch
        {
            SqlDialect.Pgsql => $"(({oldValueExpression}) IS DISTINCT FROM ({newValueExpression}))",
            SqlDialect.Mssql => $"(({oldValueExpression} <> {newValueExpression}) "
                + $"OR ({oldValueExpression} IS NULL AND {newValueExpression} IS NOT NULL) "
                + $"OR ({oldValueExpression} IS NOT NULL AND {newValueExpression} IS NULL))",
            _ => throw new ArgumentOutOfRangeException(
                nameof(_dialectRules.Dialect),
                _dialectRules.Dialect,
                "Unsupported SQL dialect."
            ),
        };
    }

    /// <summary>
    /// Builds a null-safe join predicate for trigger key columns.
    /// </summary>
    private string BuildKeyJoinPredicate(
        IReadOnlyList<DbColumnName> keyColumns,
        string leftRowAlias,
        string rightRowAlias
    )
    {
        return string.Join(
            " AND ",
            keyColumns.Select(keyColumn =>
                BuildNullSafeEqualityPredicate(
                    QualifyColumn(leftRowAlias, keyColumn),
                    QualifyColumn(rightRowAlias, keyColumn)
                )
            )
        );
    }

    /// <summary>
    /// Builds a null-safe equality predicate for a pair of SQL expressions.
    /// </summary>
    private static string BuildNullSafeEqualityPredicate(
        string leftValueExpression,
        string rightValueExpression
    )
    {
        return $"(({leftValueExpression} = {rightValueExpression}) OR ({leftValueExpression} IS NULL AND {rightValueExpression} IS NULL))";
    }

    /// <summary>
    /// Formats a row-alias-qualified and quoted column reference.
    /// </summary>
    private string QualifyColumn(string rowAlias, DbColumnName column)
    {
        return $"{rowAlias}.{Quote(column)}";
    }

    /// <summary>
    /// Resolves the SQL type for a column using explicit scalar type metadata or dialect defaults.
    /// </summary>
    private string ResolveColumnType(DbColumnModel column)
    {
        var scalarType = column.ScalarType;

        if (scalarType is null)
        {
            return column.Kind switch
            {
                ColumnKind.Ordinal => _dialectRules.ScalarTypeDefaults.Int32Type,
                _ => _dialectRules.ScalarTypeDefaults.Int64Type,
            };
        }

        return ResolveColumnType(scalarType);
    }

    /// <summary>
    /// Resolves the SQL type for a required scalar type.
    /// </summary>
    private string ResolveColumnType(RelationalScalarType scalarType)
    {
        return scalarType.Kind switch
        {
            ScalarKind.String => FormatStringType(scalarType),
            ScalarKind.Decimal => FormatDecimalType(scalarType),
            ScalarKind.Int32 => _dialectRules.ScalarTypeDefaults.Int32Type,
            ScalarKind.Int64 => _dialectRules.ScalarTypeDefaults.Int64Type,
            ScalarKind.Boolean => _dialectRules.ScalarTypeDefaults.BooleanType,
            ScalarKind.Date => _dialectRules.ScalarTypeDefaults.DateType,
            ScalarKind.DateTime => _dialectRules.ScalarTypeDefaults.DateTimeType,
            ScalarKind.Time => _dialectRules.ScalarTypeDefaults.TimeType,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scalarType.Kind),
                scalarType.Kind,
                "Unsupported scalar kind."
            ),
        };
    }

    /// <summary>
    /// Formats a string scalar type including a length specifier when present.
    /// </summary>
    private string FormatStringType(RelationalScalarType scalarType)
    {
        if (scalarType.MaxLength is null)
        {
            return _dialectRules.ScalarTypeDefaults.StringType;
        }

        return $"{_dialectRules.ScalarTypeDefaults.StringType}({scalarType.MaxLength.Value})";
    }

    /// <summary>
    /// Formats a decimal scalar type including precision and scale when present.
    /// </summary>
    private string FormatDecimalType(RelationalScalarType scalarType)
    {
        if (scalarType.Decimal is null)
        {
            return _dialectRules.ScalarTypeDefaults.DecimalType;
        }

        var (precision, scale) = scalarType.Decimal.Value;
        return $"{_dialectRules.ScalarTypeDefaults.DecimalType}({precision},{scale})";
    }

    /// <summary>
    /// Formats a table constraint for inclusion within a <c>CREATE TABLE</c> statement.
    /// </summary>
    private string FormatConstraint(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique =>
                $"CONSTRAINT {Quote(unique.Name)} UNIQUE ({FormatColumnList(unique.Columns)})",
            TableConstraint.ForeignKey foreignKey =>
                $"CONSTRAINT {Quote(foreignKey.Name)} FOREIGN KEY ({FormatColumnList(foreignKey.Columns)}) "
                    + $"REFERENCES {Quote(foreignKey.TargetTable)} ({FormatColumnList(foreignKey.TargetColumns)})"
                    + FormatReferentialActions(foreignKey),
            TableConstraint.AllOrNoneNullability allOrNone =>
                $"CONSTRAINT {Quote(allOrNone.Name)} CHECK ({FormatAllOrNoneCheck(allOrNone)})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(constraint),
                constraint,
                "Unsupported table constraint."
            ),
        };
    }

    /// <summary>
    /// Formats the expression for an all-or-none nullability check constraint.
    /// </summary>
    private string FormatAllOrNoneCheck(TableConstraint.AllOrNoneNullability constraint)
    {
        var dependencies = string.Join(
            " AND ",
            constraint.DependentColumns.Select(column => $"{Quote(column)} IS NOT NULL")
        );

        return $"({Quote(constraint.FkColumn)} IS NULL) OR ({dependencies})";
    }

    /// <summary>
    /// Formats <c>ON DELETE</c> and <c>ON UPDATE</c> clauses for a foreign key constraint.
    /// </summary>
    private string FormatReferentialActions(TableConstraint.ForeignKey foreignKey)
    {
        var deleteAction = FormatReferentialAction("DELETE", foreignKey.OnDelete);
        var updateAction = FormatReferentialAction("UPDATE", foreignKey.OnUpdate);

        return $"{deleteAction}{updateAction}";
    }

    /// <summary>
    /// Formats a referential action keyword clause when the action is not the dialect default.
    /// </summary>
    private static string FormatReferentialAction(string keyword, ReferentialAction action)
    {
        return action switch
        {
            ReferentialAction.NoAction => string.Empty,
            ReferentialAction.Cascade => $" ON {keyword} CASCADE",
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "Unsupported referential action."
            ),
        };
    }

    /// <summary>
    /// Formats a comma-separated list of quoted column names.
    /// </summary>
    private string FormatColumnList(IReadOnlyList<DbColumnName> columns)
    {
        return string.Join(", ", columns.Select(Quote));
    }

    /// <summary>
    /// Formats a comma-separated list of quoted key column names.
    /// </summary>
    private string FormatColumnList(IReadOnlyList<DbKeyColumn> columns)
    {
        return string.Join(", ", columns.Select(column => Quote(column.ColumnName)));
    }

    /// <summary>
    /// Resolves the primary key constraint name, falling back to a conventional default when unset.
    /// </summary>
    private static string ResolvePrimaryKeyConstraintName(DbTableModel table)
    {
        return string.IsNullOrWhiteSpace(table.Key.ConstraintName)
            ? $"PK_{table.Table.Name}"
            : table.Key.ConstraintName;
    }

    /// <summary>
    /// Quotes a raw identifier using the configured dialect rules.
    /// </summary>
    private string Quote(string identifier)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, identifier);
    }

    /// <summary>
    /// Quotes a schema name using the configured dialect rules.
    /// </summary>
    private string Quote(DbSchemaName schema)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, schema);
    }

    /// <summary>
    /// Quotes a fully-qualified table name using the configured dialect rules.
    /// </summary>
    private string Quote(DbTableName table)
    {
        return SqlIdentifierQuoter.QuoteTableName(_dialectRules.Dialect, table);
    }

    /// <summary>
    /// Quotes a column name using the configured dialect rules.
    /// </summary>
    private string Quote(DbColumnName column)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, column);
    }

    /// <summary>
    /// Quotes an index name using the configured dialect rules.
    /// </summary>
    private string Quote(DbIndexName index)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, index);
    }

    /// <summary>
    /// Quotes a trigger name using the configured dialect rules.
    /// </summary>
    private string Quote(DbTriggerName trigger)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, trigger);
    }

    /// <summary>
    /// Formats a trigger identifier for dialect-specific trigger namespaces.
    /// </summary>
    private string FormatTriggerName(DbTriggerInfo trigger)
    {
        return _dialectRules.Dialect switch
        {
            SqlDialect.Mssql => $"{Quote(trigger.TriggerTable.Schema)}.{Quote(trigger.Name)}",
            SqlDialect.Pgsql => Quote(trigger.Name),
            _ => throw new ArgumentOutOfRangeException(
                nameof(_dialectRules.Dialect),
                _dialectRules.Dialect,
                "Unsupported SQL dialect."
            ),
        };
    }
}
