// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// PostgreSQL-specific SQL dialect implementation.
/// </summary>
public sealed class PgsqlDialect : SqlDialectBase
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
    public override ISqlDialectRules Rules { get; }

    /// <inheritdoc />
    public override string DocumentIdColumnType => "bigint";

    /// <inheritdoc />
    public override string OrdinalColumnType => "integer";

    /// <inheritdoc />
    public override DdlPattern TriggerCreationPattern => DdlPattern.DropThenCreate;

    /// <inheritdoc />
    public override DdlPattern FunctionCreationPattern => DdlPattern.CreateOrReplace;

    /// <inheritdoc />
    public override DdlPattern ViewCreationPattern => DdlPattern.CreateOrReplace;

    /// <inheritdoc />
    public override string QuoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        // Escape any embedded double quotes by doubling them
        var escaped = identifier.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    /// <inheritdoc />
    public override string QualifyTable(DbTableName table)
    {
        return $"{QuoteIdentifier(table.Schema.Value)}.{QuoteIdentifier(table.Name)}";
    }

    /// <inheritdoc />
    public override string CreateSchemaIfNotExists(DbSchemaName schema)
    {
        return $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(schema.Value)};";
    }

    /// <inheritdoc />
    public override string CreateTableHeader(DbTableName table)
    {
        return $"CREATE TABLE IF NOT EXISTS {QualifyTable(table)}";
    }

    /// <inheritdoc />
    public override string DropTriggerIfExists(DbTableName table, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(triggerName);

        return $"DROP TRIGGER IF EXISTS {QuoteIdentifier(triggerName)} ON {QualifyTable(table)};";
    }

    /// <inheritdoc />
    public override string CreateSequenceIfNotExists(
        DbSchemaName schema,
        string sequenceName,
        long startWith = 1
    )
    {
        ArgumentNullException.ThrowIfNull(sequenceName);

        var qualifiedName = $"{QuoteIdentifier(schema.Value)}.{QuoteIdentifier(sequenceName)}";
        return $"CREATE SEQUENCE IF NOT EXISTS {qualifiedName} START WITH {startWith};";
    }

    /// <inheritdoc />
    public override string CreateIndexIfNotExists(
        DbTableName table,
        string indexName,
        IReadOnlyList<DbColumnName> columns,
        bool isUnique = false
    )
    {
        ArgumentNullException.ThrowIfNull(indexName);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required for an index.", nameof(columns));
        }

        var uniqueKeyword = isUnique ? "UNIQUE " : "";
        var qualifiedIndex = $"{QuoteIdentifier(table.Schema.Value)}.{QuoteIdentifier(indexName)}";
        var columnList = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Value)));

        return $"CREATE {uniqueKeyword}INDEX IF NOT EXISTS {qualifiedIndex} ON {QualifyTable(table)} ({columnList});";
    }

    /// <inheritdoc />
    public override string AddForeignKeyConstraint(
        DbTableName table,
        string constraintName,
        IReadOnlyList<DbColumnName> columns,
        DbTableName targetTable,
        IReadOnlyList<DbColumnName> targetColumns,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        ReferentialAction onUpdate = ReferentialAction.NoAction
    )
    {
        ArgumentNullException.ThrowIfNull(constraintName);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(targetColumns);

        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "At least one column is required for a foreign key.",
                nameof(columns)
            );
        }

        if (columns.Count != targetColumns.Count)
        {
            throw new ArgumentException(
                "Foreign key column count must match target column count.",
                nameof(targetColumns)
            );
        }

        var columnList = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Value)));
        var targetColumnList = string.Join(", ", targetColumns.Select(c => QuoteIdentifier(c.Value)));
        var quotedConstraint = QuoteIdentifier(constraintName);
        var escapedConstraint = constraintName.Replace("'", "''");
        var escapedSchema = table.Schema.Value.Replace("'", "''");
        var escapedTable = table.Name.Replace("'", "''");

        // Use DO block to check if constraint exists before adding
        return $"""
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = '{escapedConstraint}'
                    AND conrelid = to_regclass('{escapedSchema}.{escapedTable}')
                )
                THEN
                    ALTER TABLE {QualifyTable(table)}
                    ADD CONSTRAINT {quotedConstraint}
                    FOREIGN KEY ({columnList})
                    REFERENCES {QualifyTable(targetTable)} ({targetColumnList})
                    ON DELETE {RenderReferentialAction(onDelete)}
                    ON UPDATE {RenderReferentialAction(onUpdate)};
                END IF;
            END $$;
            """;
    }

    /// <inheritdoc />
    public override string AddUniqueConstraint(
        DbTableName table,
        string constraintName,
        IReadOnlyList<DbColumnName> columns
    )
    {
        ArgumentNullException.ThrowIfNull(constraintName);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "At least one column is required for a unique constraint.",
                nameof(columns)
            );
        }

        var columnList = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Value)));
        var quotedConstraint = QuoteIdentifier(constraintName);
        var escapedConstraint = constraintName.Replace("'", "''");
        var escapedSchema = table.Schema.Value.Replace("'", "''");
        var escapedTable = table.Name.Replace("'", "''");

        return $"""
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = '{escapedConstraint}'
                    AND conrelid = to_regclass('{escapedSchema}.{escapedTable}')
                )
                THEN
                    ALTER TABLE {QualifyTable(table)}
                    ADD CONSTRAINT {quotedConstraint} UNIQUE ({columnList});
                END IF;
            END $$;
            """;
    }

    /// <inheritdoc />
    public override string AddCheckConstraint(
        DbTableName table,
        string constraintName,
        string checkExpression
    )
    {
        ArgumentNullException.ThrowIfNull(constraintName);
        ArgumentNullException.ThrowIfNull(checkExpression);

        var quotedConstraint = QuoteIdentifier(constraintName);
        var escapedConstraint = constraintName.Replace("'", "''");
        var escapedSchema = table.Schema.Value.Replace("'", "''");
        var escapedTable = table.Name.Replace("'", "''");

        return $"""
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = '{escapedConstraint}'
                    AND conrelid = to_regclass('{escapedSchema}.{escapedTable}')
                )
                THEN
                    ALTER TABLE {QualifyTable(table)}
                    ADD CONSTRAINT {quotedConstraint} CHECK ({checkExpression});
                END IF;
            END $$;
            """;
    }

    /// <inheritdoc />
    public override string CreateExtensionIfNotExists(string extensionName)
    {
        ArgumentNullException.ThrowIfNull(extensionName);

        return $"CREATE EXTENSION IF NOT EXISTS {QuoteIdentifier(extensionName)};";
    }

    /// <inheritdoc />
    public override string CreateUuidv5Function(DbSchemaName schema)
    {
        var qualifiedName = $"{QuoteIdentifier(schema.Value)}.{QuoteIdentifier("uuidv5")}";

        return $"""
            CREATE OR REPLACE FUNCTION {qualifiedName}(namespace_uuid uuid, name_text text)
            RETURNS uuid
            LANGUAGE plpgsql
            IMMUTABLE STRICT PARALLEL SAFE
            AS $uuidv5$
            DECLARE
                hash bytea;
            BEGIN
                hash := digest(
                    decode(replace(namespace_uuid::text, '-', ''), 'hex')
                    || convert_to(name_text, 'UTF8'),
                    'sha1'
                );
                hash := set_byte(hash, 6, (get_byte(hash, 6) & x'0f'::int) | x'50'::int);
                hash := set_byte(hash, 8, (get_byte(hash, 8) & x'3f'::int) | x'80'::int);
                RETURN encode(substring(hash from 1 for 16), 'hex')::uuid;
            END
            $uuidv5$;
            """;
    }

    // ── Core-table type properties ──────────────────────────────────────

    /// <inheritdoc />
    public override string SmallintColumnType => "smallint";

    /// <inheritdoc />
    public override string UuidColumnType => "uuid";

    /// <inheritdoc />
    public override string JsonColumnType => "jsonb";

    /// <inheritdoc />
    public override string IdentityBigintColumnType => "bigint GENERATED ALWAYS AS IDENTITY";

    /// <inheritdoc />
    public override string CurrentTimestampDefaultExpression => "now()";

    // ── Core-table type methods ─────────────────────────────────────────

    /// <inheritdoc />
    public override string RenderBinaryColumnType(int length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "Binary column length must be positive."
            );
        }

        // PostgreSQL bytea is variable-length; the length parameter is
        // enforced via a CHECK constraint, not the type itself.
        return "bytea";
    }

    /// <inheritdoc />
    public override string RenderSequenceDefaultExpression(DbSchemaName schema, string sequenceName)
    {
        ArgumentNullException.ThrowIfNull(sequenceName);

        var qualifiedName = $"{QuoteIdentifier(schema.Value)}.{QuoteIdentifier(sequenceName)}";
        var escapedForLiteral = qualifiedName.Replace("'", "''");
        return $"nextval('{escapedForLiteral}')";
    }

    /// <inheritdoc />
    public override string RenderColumnDefinitionWithNamedDefault(
        DbColumnName columnName,
        string sqlType,
        bool isNullable,
        string? constraintName,
        string? defaultExpression
    )
    {
        // PostgreSQL does not support named default constraints;
        // delegate to the standard column definition.
        return RenderColumnDefinition(columnName, sqlType, isNullable, defaultExpression);
    }

    /// <inheritdoc />
    public override string RenderNamedPrimaryKeyClause(
        string constraintName,
        IReadOnlyList<DbColumnName> columns,
        bool clustered = true
    )
    {
        ArgumentNullException.ThrowIfNull(constraintName);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "At least one column is required for a primary key.",
                nameof(columns)
            );
        }

        var columnList = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Value)));

        // PostgreSQL does not support CLUSTERED/NONCLUSTERED.
        return $"CONSTRAINT {QuoteIdentifier(constraintName)} PRIMARY KEY ({columnList})";
    }
}
