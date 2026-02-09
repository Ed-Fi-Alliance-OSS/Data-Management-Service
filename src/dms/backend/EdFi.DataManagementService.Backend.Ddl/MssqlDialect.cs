// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// SQL Server-specific SQL dialect implementation.
/// </summary>
public sealed class MssqlDialect : SqlDialectBase
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
    public override ISqlDialectRules Rules { get; }

    /// <inheritdoc />
    public override string DocumentIdColumnType => "bigint";

    /// <inheritdoc />
    public override string OrdinalColumnType => "int";

    /// <inheritdoc />
    public override DdlPattern TriggerCreationPattern => DdlPattern.CreateOrAlter;

    /// <inheritdoc />
    public override DdlPattern FunctionCreationPattern => DdlPattern.CreateOrAlter;

    /// <inheritdoc />
    public override DdlPattern ViewCreationPattern => DdlPattern.CreateOrAlter;

    /// <inheritdoc />
    public override string QuoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        // Escape any embedded right brackets by doubling them
        var escaped = identifier.Replace("]", "]]");
        return $"[{escaped}]";
    }

    /// <inheritdoc />
    public override string QualifyTable(DbTableName table)
    {
        return $"{QuoteIdentifier(table.Schema.Value)}.{QuoteIdentifier(table.Name)}";
    }

    /// <inheritdoc />
    public override string CreateSchemaIfNotExists(DbSchemaName schema)
    {
        // SQL Server does not support IF NOT EXISTS for CREATE SCHEMA,
        // so we use a catalog check pattern.
        var quotedSchema = QuoteIdentifier(schema.Value);
        var escapedSchemaForLiteral = schema.Value.Replace("'", "''");

        // Escape single quotes in the bracket-quoted identifier for embedding in EXEC string
        var quotedSchemaForExec = quotedSchema.Replace("'", "''");

        return $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{escapedSchemaForLiteral}')\n"
            + $"    EXEC('CREATE SCHEMA {quotedSchemaForExec}');";
    }

    /// <inheritdoc />
    public override string CreateTableHeader(DbTableName table)
    {
        // SQL Server does not support IF NOT EXISTS for CREATE TABLE directly,
        // so we use an OBJECT_ID check pattern.
        var qualifiedTable = QualifyTable(table);
        var escapedTableForObjectId = $"{table.Schema.Value}.{table.Name}".Replace("'", "''");

        return $"IF OBJECT_ID(N'{escapedTableForObjectId}', N'U') IS NULL\n"
            + $"CREATE TABLE {qualifiedTable}";
    }

    /// <inheritdoc />
    public override string DropTriggerIfExists(DbTableName table, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(triggerName);

        // SQL Server triggers are schema-scoped, not table-scoped
        return $"DROP TRIGGER IF EXISTS {QuoteIdentifier(table.Schema.Value)}.{QuoteIdentifier(triggerName)};";
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
        var escapedSchema = schema.Value.Replace("'", "''");
        var escapedSequence = sequenceName.Replace("'", "''");

        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.sequences s
                JOIN sys.schemas sch ON s.schema_id = sch.schema_id
                WHERE sch.name = N'{escapedSchema}' AND s.name = N'{escapedSequence}'
            )
            CREATE SEQUENCE {qualifiedName} START WITH {startWith};
            """;
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
        var quotedIndex = QuoteIdentifier(indexName);
        var columnList = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Value)));
        var escapedSchema = table.Schema.Value.Replace("'", "''");
        var escapedTable = table.Name.Replace("'", "''");
        var escapedIndex = indexName.Replace("'", "''");

        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes i
                JOIN sys.tables t ON i.object_id = t.object_id
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE s.name = N'{escapedSchema}' AND t.name = N'{escapedTable}' AND i.name = N'{escapedIndex}'
            )
            CREATE {uniqueKeyword}INDEX {quotedIndex} ON {QualifyTable(table)} ({columnList});
            """;
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
        var escapedTableForObjectId = $"{table.Schema.Value}.{table.Name}".Replace("'", "''");

        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.foreign_keys
                WHERE name = N'{escapedConstraint}' AND parent_object_id = OBJECT_ID(N'{escapedTableForObjectId}')
            )
            ALTER TABLE {QualifyTable(table)}
            ADD CONSTRAINT {quotedConstraint}
            FOREIGN KEY ({columnList})
            REFERENCES {QualifyTable(targetTable)} ({targetColumnList})
            ON DELETE {RenderReferentialAction(onDelete)}
            ON UPDATE {RenderReferentialAction(onUpdate)};
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
        var escapedTableForObjectId = $"{table.Schema.Value}.{table.Name}".Replace("'", "''");

        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.key_constraints
                WHERE name = N'{escapedConstraint}' AND type = 'UQ' AND parent_object_id = OBJECT_ID(N'{escapedTableForObjectId}')
            )
            ALTER TABLE {QualifyTable(table)}
            ADD CONSTRAINT {quotedConstraint} UNIQUE ({columnList});
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
        var escapedTableForObjectId = $"{table.Schema.Value}.{table.Name}".Replace("'", "''");

        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.check_constraints
                WHERE name = N'{escapedConstraint}' AND parent_object_id = OBJECT_ID(N'{escapedTableForObjectId}')
            )
            ALTER TABLE {QualifyTable(table)}
            ADD CONSTRAINT {quotedConstraint} CHECK ({checkExpression});
            """;
    }

    /// <inheritdoc />
    public override string CreateExtensionIfNotExists(string extensionName)
    {
        // SQL Server does not have a database extension concept.
        return string.Empty;
    }

    /// <inheritdoc />
    public override string CreateUuidv5Function(DbSchemaName schema)
    {
        var qualifiedName = $"{QuoteIdentifier(schema.Value)}.{QuoteIdentifier("uuidv5")}";

        return $"""
            CREATE OR ALTER FUNCTION {qualifiedName}(@namespace_uuid uniqueidentifier, @name_text nvarchar(max))
            RETURNS uniqueidentifier
            WITH SCHEMABINDING
            AS
            BEGIN
                DECLARE @ns_bytes varbinary(16) = CAST(@namespace_uuid AS varbinary(16));

                -- Convert SQL Server mixed-endian to RFC 4122 big-endian for hashing
                DECLARE @ns_be varbinary(16) =
                    SUBSTRING(@ns_bytes, 4, 1) + SUBSTRING(@ns_bytes, 3, 1)
                    + SUBSTRING(@ns_bytes, 2, 1) + SUBSTRING(@ns_bytes, 1, 1)
                    + SUBSTRING(@ns_bytes, 6, 1) + SUBSTRING(@ns_bytes, 5, 1)
                    + SUBSTRING(@ns_bytes, 8, 1) + SUBSTRING(@ns_bytes, 7, 1)
                    + SUBSTRING(@ns_bytes, 9, 8);

                DECLARE @name_bytes varbinary(max) = CAST(CAST(@name_text AS varchar(max)) AS varbinary(max));

                DECLARE @hash varbinary(20) = HASHBYTES('SHA1', @ns_be + @name_bytes);

                -- Take first 16 bytes and set version/variant bits
                DECLARE @result varbinary(16) = SUBSTRING(@hash, 1, 16);

                DECLARE @byte6 int = CAST(SUBSTRING(@result, 7, 1) AS int);
                SET @result = SUBSTRING(@result, 1, 6)
                    + CAST((@byte6 & 0x0F) | 0x50 AS binary(1))
                    + SUBSTRING(@result, 8, 9);

                DECLARE @byte8 int = CAST(SUBSTRING(@result, 9, 1) AS int);
                SET @result = SUBSTRING(@result, 1, 8)
                    + CAST((@byte8 & 0x3F) | 0x80 AS binary(1))
                    + SUBSTRING(@result, 10, 7);

                -- Convert big-endian result back to SQL Server mixed-endian
                RETURN CAST(
                    SUBSTRING(@result, 4, 1) + SUBSTRING(@result, 3, 1)
                    + SUBSTRING(@result, 2, 1) + SUBSTRING(@result, 1, 1)
                    + SUBSTRING(@result, 6, 1) + SUBSTRING(@result, 5, 1)
                    + SUBSTRING(@result, 8, 1) + SUBSTRING(@result, 7, 1)
                    + SUBSTRING(@result, 9, 8)
                    AS uniqueidentifier);
            END;
            """;
    }
}
