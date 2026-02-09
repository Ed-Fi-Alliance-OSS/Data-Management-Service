// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Abstract base class for SQL dialect implementations.
/// Provides shared implementations for methods that are identical across dialects.
/// </summary>
public abstract class SqlDialectBase : ISqlDialect
{
    /// <inheritdoc />
    public abstract ISqlDialectRules Rules { get; }

    /// <inheritdoc />
    public abstract string DocumentIdColumnType { get; }

    /// <inheritdoc />
    public abstract string OrdinalColumnType { get; }

    /// <inheritdoc />
    public abstract DdlPattern TriggerCreationPattern { get; }

    /// <inheritdoc />
    public abstract DdlPattern FunctionCreationPattern { get; }

    /// <inheritdoc />
    public abstract DdlPattern ViewCreationPattern { get; }

    /// <inheritdoc />
    public abstract string QuoteIdentifier(string identifier);

    /// <inheritdoc />
    public abstract string QualifyTable(DbTableName table);

    /// <inheritdoc />
    public abstract string CreateSchemaIfNotExists(DbSchemaName schema);

    /// <inheritdoc />
    public abstract string CreateTableHeader(DbTableName table);

    /// <inheritdoc />
    public abstract string DropTriggerIfExists(DbTableName table, string triggerName);

    /// <inheritdoc />
    public abstract string CreateSequenceIfNotExists(
        DbSchemaName schema,
        string sequenceName,
        long startWith = 1
    );

    /// <inheritdoc />
    public abstract string CreateIndexIfNotExists(
        DbTableName table,
        string indexName,
        IReadOnlyList<DbColumnName> columns,
        bool isUnique = false
    );

    /// <inheritdoc />
    public abstract string AddForeignKeyConstraint(
        DbTableName table,
        string constraintName,
        IReadOnlyList<DbColumnName> columns,
        DbTableName targetTable,
        IReadOnlyList<DbColumnName> targetColumns,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        ReferentialAction onUpdate = ReferentialAction.NoAction
    );

    /// <inheritdoc />
    public abstract string AddUniqueConstraint(
        DbTableName table,
        string constraintName,
        IReadOnlyList<DbColumnName> columns
    );

    /// <inheritdoc />
    public abstract string AddCheckConstraint(
        DbTableName table,
        string constraintName,
        string checkExpression
    );

    /// <inheritdoc />
    public virtual string RenderColumnType(RelationalScalarType scalarType)
    {
        ArgumentNullException.ThrowIfNull(scalarType);

        var defaults = Rules.ScalarTypeDefaults;

        return scalarType.Kind switch
        {
            ScalarKind.String when scalarType.MaxLength.HasValue =>
                $"{defaults.StringType}({scalarType.MaxLength.Value})",
            ScalarKind.String => defaults.StringType,

            ScalarKind.Int32 => defaults.Int32Type,
            ScalarKind.Int64 => defaults.Int64Type,
            ScalarKind.Boolean => defaults.BooleanType,
            ScalarKind.Date => defaults.DateType,
            ScalarKind.DateTime => defaults.DateTimeType,
            ScalarKind.Time => defaults.TimeType,

            ScalarKind.Decimal when scalarType.Decimal.HasValue =>
                $"{defaults.DecimalType}({scalarType.Decimal.Value.Precision},{scalarType.Decimal.Value.Scale})",
            ScalarKind.Decimal => defaults.DecimalType,

            _ => throw new ArgumentOutOfRangeException(
                nameof(scalarType),
                scalarType.Kind,
                "Unsupported scalar kind."
            ),
        };
    }

    /// <inheritdoc />
    public virtual string RenderColumnDefinition(
        DbColumnName columnName,
        string sqlType,
        bool isNullable,
        string? defaultExpression = null
    )
    {
        ArgumentNullException.ThrowIfNull(sqlType);

        var nullability = isNullable ? "NULL" : "NOT NULL";
        var defaultClause = defaultExpression is not null ? $" DEFAULT {defaultExpression}" : "";

        return $"{QuoteIdentifier(columnName.Value)} {sqlType} {nullability}{defaultClause}";
    }

    /// <inheritdoc />
    public virtual string RenderPrimaryKeyClause(IReadOnlyList<DbColumnName> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "At least one column is required for a primary key.",
                nameof(columns)
            );
        }

        var columnList = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Value)));
        return $"PRIMARY KEY ({columnList})";
    }

    /// <inheritdoc />
    public virtual string RenderReferentialAction(ReferentialAction action)
    {
        return action switch
        {
            ReferentialAction.NoAction => "NO ACTION",
            ReferentialAction.Cascade => "CASCADE",
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "Unsupported referential action."
            ),
        };
    }

    /// <inheritdoc />
    public abstract string CreateExtensionIfNotExists(string extensionName);

    /// <inheritdoc />
    public abstract string CreateUuidv5Function(DbSchemaName schema);
}
