// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

public sealed class RelationalModelDdlEmitter
{
    private readonly ISqlDialectRules _dialectRules;

    public RelationalModelDdlEmitter(ISqlDialectRules dialectRules)
    {
        ArgumentNullException.ThrowIfNull(dialectRules);
        _dialectRules = dialectRules;
    }

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
        AppendTriggers(builder, modelSet.TriggersInCreateOrder);

        return builder.ToString();
    }

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

    private void AppendTriggers(StringBuilder builder, IReadOnlyList<DbTriggerInfo> triggers)
    {
        foreach (var trigger in triggers)
        {
            builder.Append("CREATE TRIGGER ");
            builder.Append(Quote(trigger.Name));
            builder.Append(" ON ");
            builder.Append(Quote(trigger.Table));
            builder.Append(' ');
            builder.AppendLine(BuildTriggerBody());
            builder.AppendLine();
        }
    }

    private string BuildTriggerBody()
    {
        var body = _dialectRules.Dialect switch
        {
            SqlDialect.Pgsql => $"EXECUTE FUNCTION {Quote("noop")}();",
            SqlDialect.Mssql => "AS BEGIN END;",
            _ => throw new ArgumentOutOfRangeException(
                nameof(_dialectRules.Dialect),
                _dialectRules.Dialect,
                "Unsupported SQL dialect."
            ),
        };

        return body;
    }

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

    private string FormatStringType(RelationalScalarType scalarType)
    {
        if (scalarType.MaxLength is null)
        {
            return _dialectRules.ScalarTypeDefaults.StringType;
        }

        return $"{_dialectRules.ScalarTypeDefaults.StringType}({scalarType.MaxLength.Value})";
    }

    private string FormatDecimalType(RelationalScalarType scalarType)
    {
        if (scalarType.Decimal is null)
        {
            return _dialectRules.ScalarTypeDefaults.DecimalType;
        }

        var (precision, scale) = scalarType.Decimal.Value;
        return $"{_dialectRules.ScalarTypeDefaults.DecimalType}({precision},{scale})";
    }

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

    private string FormatAllOrNoneCheck(TableConstraint.AllOrNoneNullability constraint)
    {
        var dependencies = string.Join(
            " AND ",
            constraint.DependentColumns.Select(column => $"{Quote(column)} IS NOT NULL")
        );

        return $"({Quote(constraint.FkColumn)} IS NULL) OR ({dependencies})";
    }

    private string FormatReferentialActions(TableConstraint.ForeignKey foreignKey)
    {
        var deleteAction = FormatReferentialAction("DELETE", foreignKey.OnDelete);
        var updateAction = FormatReferentialAction("UPDATE", foreignKey.OnUpdate);

        return $"{deleteAction}{updateAction}";
    }

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

    private string FormatColumnList(IReadOnlyList<DbColumnName> columns)
    {
        return string.Join(", ", columns.Select(Quote));
    }

    private string FormatColumnList(IReadOnlyList<DbKeyColumn> columns)
    {
        return string.Join(", ", columns.Select(column => Quote(column.ColumnName)));
    }

    private static string ResolvePrimaryKeyConstraintName(DbTableModel table)
    {
        return string.IsNullOrWhiteSpace(table.Key.ConstraintName)
            ? $"PK_{table.Table.Name}"
            : table.Key.ConstraintName;
    }

    private string Quote(string identifier)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, identifier);
    }

    private string Quote(DbSchemaName schema)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, schema);
    }

    private string Quote(DbTableName table)
    {
        return SqlIdentifierQuoter.QuoteTableName(_dialectRules.Dialect, table);
    }

    private string Quote(DbColumnName column)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, column);
    }

    private string Quote(DbIndexName index)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, index);
    }

    private string Quote(DbTriggerName trigger)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, trigger);
    }
}
