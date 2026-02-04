// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// PostgreSQL-specific SQL dialect implementation.
/// </summary>
public sealed class PgsqlDialect : ISqlDialect
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PgsqlDialect"/> class.
    /// </summary>
    /// <param name="rules">The shared dialect rules for identifier limits and type defaults.</param>
    public PgsqlDialect(ISqlDialectRules rules)
    {
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));

        if (rules.Dialect != SqlDialect.Pgsql)
        {
            throw new ArgumentException(
                $"Expected PostgreSQL dialect rules, but received {rules.Dialect}.",
                nameof(rules)
            );
        }
    }

    /// <inheritdoc />
    public ISqlDialectRules Rules { get; }

    /// <inheritdoc />
    public string DocumentIdColumnType => "bigint";

    /// <inheritdoc />
    public string OrdinalColumnType => "integer";

    /// <inheritdoc />
    public DdlPattern TriggerCreationPattern => DdlPattern.DropThenCreate;

    /// <inheritdoc />
    public DdlPattern FunctionCreationPattern => DdlPattern.CreateOrReplace;

    /// <inheritdoc />
    public DdlPattern ViewCreationPattern => DdlPattern.CreateOrReplace;

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        // Escape any embedded double quotes by doubling them
        var escaped = identifier.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
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
        return $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(schema.Value)};";
    }

    /// <inheritdoc />
    public string CreateTableHeader(DbTableName table)
    {
        return $"CREATE TABLE IF NOT EXISTS {QualifyTable(table)}";
    }

    /// <inheritdoc />
    public string DropTriggerIfExists(DbTableName table, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(triggerName);

        return $"DROP TRIGGER IF EXISTS {QuoteIdentifier(triggerName)} ON {QualifyTable(table)};";
    }
}
