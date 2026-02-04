// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// SQL Server-specific SQL dialect implementation.
/// </summary>
public sealed class MssqlDialect : ISqlDialect
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MssqlDialect"/> class.
    /// </summary>
    /// <param name="rules">The shared dialect rules for identifier limits and type defaults.</param>
    public MssqlDialect(ISqlDialectRules rules)
    {
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));

        if (rules.Dialect != SqlDialect.Mssql)
        {
            throw new ArgumentException(
                $"Expected SQL Server dialect rules, but received {rules.Dialect}.",
                nameof(rules)
            );
        }
    }

    /// <inheritdoc />
    public ISqlDialectRules Rules { get; }

    /// <inheritdoc />
    public string DocumentIdColumnType => "bigint";

    /// <inheritdoc />
    public string OrdinalColumnType => "int";

    /// <inheritdoc />
    public DdlPattern TriggerCreationPattern => DdlPattern.CreateOrAlter;

    /// <inheritdoc />
    public DdlPattern FunctionCreationPattern => DdlPattern.CreateOrAlter;

    /// <inheritdoc />
    public DdlPattern ViewCreationPattern => DdlPattern.CreateOrAlter;

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        // Escape any embedded right brackets by doubling them
        var escaped = identifier.Replace("]", "]]");
        return $"[{escaped}]";
    }

    /// <inheritdoc />
    public string QualifyTable(DbTableName table)
    {
        return $"{QuoteIdentifier(table.Schema.Value)}.{QuoteIdentifier(table.Name)}";
    }

    /// <inheritdoc />
    public string RenderColumnType(RelationalScalarType scalarType)
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
    public string CreateSchemaIfNotExists(DbSchemaName schema)
    {
        // SQL Server does not support IF NOT EXISTS for CREATE SCHEMA,
        // so we use a catalog check pattern.
        var quotedSchema = QuoteIdentifier(schema.Value);
        var escapedSchemaForLiteral = schema.Value.Replace("'", "''");

        return $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{escapedSchemaForLiteral}')\n"
            + $"    EXEC('CREATE SCHEMA {quotedSchema}');";
    }

    /// <inheritdoc />
    public string CreateTableHeader(DbTableName table)
    {
        // SQL Server does not support IF NOT EXISTS for CREATE TABLE directly,
        // so we use an OBJECT_ID check pattern.
        var qualifiedTable = QualifyTable(table);
        var escapedTableForObjectId = $"{table.Schema.Value}.{table.Name}".Replace("'", "''");

        return $"IF OBJECT_ID(N'{escapedTableForObjectId}', N'U') IS NULL\n"
            + $"CREATE TABLE {qualifiedTable}";
    }

    /// <inheritdoc />
    public string DropTriggerIfExists(DbTableName table, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(triggerName);

        // SQL Server triggers are schema-scoped, not table-scoped
        return $"DROP TRIGGER IF EXISTS {QuoteIdentifier(table.Schema.Value)}.{QuoteIdentifier(triggerName)};";
    }
}
