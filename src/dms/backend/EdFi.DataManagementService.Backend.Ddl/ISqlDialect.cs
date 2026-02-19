// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// SQL dialect abstraction for DDL generation and SQL plan emission.
/// Composes over <see cref="ISqlDialectRules"/> for shared identifier and type rules.
/// </summary>
public interface ISqlDialect
{
    /// <summary>
    /// Gets the underlying shared dialect rules for identifier limits and scalar type defaults.
    /// </summary>
    ISqlDialectRules Rules { get; }

    /// <summary>
    /// Quotes a schema, table, or column identifier using dialect-specific quoting rules.
    /// </summary>
    /// <param name="identifier">The raw identifier.</param>
    /// <returns>The quoted identifier.</returns>
    string QuoteIdentifier(string identifier);

    /// <summary>
    /// Renders a fully qualified table name with schema (schema.table), both quoted.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <returns>The qualified and quoted table reference.</returns>
    string QualifyTable(DbTableName table);

    /// <summary>
    /// Renders a column type definition for a scalar type.
    /// </summary>
    /// <param name="scalarType">The scalar type metadata.</param>
    /// <returns>The dialect-specific SQL type string.</returns>
    string RenderColumnType(RelationalScalarType scalarType);

    /// <summary>
    /// Gets the dialect's type for DocumentId columns (typically bigint).
    /// </summary>
    string DocumentIdColumnType { get; }

    /// <summary>
    /// Gets the dialect's type for Ordinal columns (typically integer/int).
    /// </summary>
    string OrdinalColumnType { get; }

    /// <summary>
    /// Returns the CREATE SCHEMA statement with IF NOT EXISTS semantics.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <returns>The idempotent CREATE SCHEMA statement.</returns>
    string CreateSchemaIfNotExists(DbSchemaName schema);

    /// <summary>
    /// Returns the CREATE TABLE header with IF NOT EXISTS semantics.
    /// Does not include the column list or closing parenthesis.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <returns>The CREATE TABLE IF NOT EXISTS header.</returns>
    string CreateTableHeader(DbTableName table);

    /// <summary>
    /// Returns the DROP TRIGGER IF EXISTS statement.
    /// </summary>
    /// <param name="table">The table the trigger is attached to.</param>
    /// <param name="triggerName">The trigger name.</param>
    /// <returns>The DROP TRIGGER IF EXISTS statement.</returns>
    string DropTriggerIfExists(DbTableName table, string triggerName);

    /// <summary>
    /// Gets the DDL pattern used for trigger creation.
    /// </summary>
    DdlPattern TriggerCreationPattern { get; }

    /// <summary>
    /// Gets the DDL pattern used for function creation.
    /// </summary>
    DdlPattern FunctionCreationPattern { get; }

    /// <summary>
    /// Gets the DDL pattern used for view creation.
    /// </summary>
    DdlPattern ViewCreationPattern { get; }

    /// <summary>
    /// Returns the CREATE SEQUENCE statement with IF NOT EXISTS semantics.
    /// </summary>
    /// <param name="schema">The schema containing the sequence.</param>
    /// <param name="sequenceName">The sequence name.</param>
    /// <param name="startWith">The starting value for the sequence.</param>
    /// <returns>The idempotent CREATE SEQUENCE statement.</returns>
    string CreateSequenceIfNotExists(DbSchemaName schema, string sequenceName, long startWith = 1);

    /// <summary>
    /// Returns the CREATE INDEX statement with IF NOT EXISTS semantics.
    /// </summary>
    /// <param name="table">The table to create the index on.</param>
    /// <param name="indexName">The index name.</param>
    /// <param name="columns">The columns to include in the index.</param>
    /// <param name="isUnique">Whether the index should enforce uniqueness.</param>
    /// <returns>The idempotent CREATE INDEX statement.</returns>
    string CreateIndexIfNotExists(
        DbTableName table,
        string indexName,
        IReadOnlyList<DbColumnName> columns,
        bool isUnique = false
    );

    /// <summary>
    /// Returns the ALTER TABLE ADD CONSTRAINT statement for a foreign key.
    /// Uses catalog checks to make the operation idempotent.
    /// </summary>
    /// <param name="table">The table containing the foreign key columns.</param>
    /// <param name="constraintName">The constraint name.</param>
    /// <param name="columns">The local foreign key columns.</param>
    /// <param name="targetTable">The referenced table.</param>
    /// <param name="targetColumns">The referenced columns.</param>
    /// <param name="onDelete">The referential action on delete.</param>
    /// <param name="onUpdate">The referential action on update.</param>
    /// <returns>The idempotent ADD FOREIGN KEY statement.</returns>
    string AddForeignKeyConstraint(
        DbTableName table,
        string constraintName,
        IReadOnlyList<DbColumnName> columns,
        DbTableName targetTable,
        IReadOnlyList<DbColumnName> targetColumns,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        ReferentialAction onUpdate = ReferentialAction.NoAction
    );

    /// <summary>
    /// Returns the ALTER TABLE ADD CONSTRAINT statement for a unique constraint.
    /// Uses catalog checks to make the operation idempotent.
    /// </summary>
    /// <param name="table">The table to add the constraint to.</param>
    /// <param name="constraintName">The constraint name.</param>
    /// <param name="columns">The columns that must be unique.</param>
    /// <returns>The idempotent ADD UNIQUE CONSTRAINT statement.</returns>
    string AddUniqueConstraint(DbTableName table, string constraintName, IReadOnlyList<DbColumnName> columns);

    /// <summary>
    /// Returns the ALTER TABLE ADD CONSTRAINT statement for a check constraint.
    /// Uses catalog checks to make the operation idempotent.
    /// </summary>
    /// <param name="table">The table to add the constraint to.</param>
    /// <param name="constraintName">The constraint name.</param>
    /// <param name="checkExpression">The SQL boolean expression for the check.</param>
    /// <returns>The idempotent ADD CHECK CONSTRAINT statement.</returns>
    string AddCheckConstraint(DbTableName table, string constraintName, string checkExpression);

    /// <summary>
    /// Renders a complete column definition for use in CREATE TABLE statements.
    /// </summary>
    /// <param name="columnName">The column name.</param>
    /// <param name="sqlType">The SQL type (from RenderColumnType, DocumentIdColumnType, etc.).</param>
    /// <param name="isNullable">Whether the column allows NULL values.</param>
    /// <param name="defaultExpression">Optional default expression (dialect-specific SQL).</param>
    /// <returns>The complete column definition (e.g., "column_name" bigint NOT NULL DEFAULT 1).</returns>
    string RenderColumnDefinition(
        DbColumnName columnName,
        string sqlType,
        bool isNullable,
        string? defaultExpression = null
    );

    /// <summary>
    /// Renders a column definition with an optional named default constraint.
    /// SQL Server emits <c>CONSTRAINT [name] DEFAULT (expr)</c>; PostgreSQL ignores
    /// the constraint name and emits a plain <c>DEFAULT expr</c>.
    /// </summary>
    /// <param name="columnName">The column name.</param>
    /// <param name="sqlType">The SQL type string.</param>
    /// <param name="isNullable">Whether the column allows NULL values.</param>
    /// <param name="constraintName">
    /// Optional constraint name for the default (used by SQL Server; ignored by PostgreSQL).
    /// </param>
    /// <param name="defaultExpression">Optional default expression.</param>
    /// <returns>The complete column definition.</returns>
    string RenderColumnDefinitionWithNamedDefault(
        DbColumnName columnName,
        string sqlType,
        bool isNullable,
        string? constraintName,
        string? defaultExpression
    );

    /// <summary>
    /// Renders a PRIMARY KEY constraint clause for use in CREATE TABLE statements.
    /// </summary>
    /// <param name="columns">The primary key columns in order.</param>
    /// <returns>The PRIMARY KEY clause (e.g., PRIMARY KEY ("col1", "col2")).</returns>
    string RenderPrimaryKeyClause(IReadOnlyList<DbColumnName> columns);

    /// <summary>
    /// Renders a named PRIMARY KEY constraint clause with optional clustered storage.
    /// SQL Server uses <c>CLUSTERED</c> or <c>NONCLUSTERED</c>; PostgreSQL ignores the
    /// clustered parameter.
    /// </summary>
    /// <param name="constraintName">The constraint name.</param>
    /// <param name="columns">The primary key columns in order.</param>
    /// <param name="clustered">
    /// Whether the index uses clustered storage (SQL Server only; ignored by PostgreSQL).
    /// </param>
    /// <returns>The named PRIMARY KEY constraint clause.</returns>
    string RenderNamedPrimaryKeyClause(
        string constraintName,
        IReadOnlyList<DbColumnName> columns,
        bool clustered = true
    );

    /// <summary>
    /// Renders the referential action keyword for foreign key constraints.
    /// </summary>
    /// <param name="action">The referential action.</param>
    /// <returns>The SQL keyword (NO ACTION or CASCADE).</returns>
    string RenderReferentialAction(ReferentialAction action);

    /// <summary>
    /// Returns the CREATE EXTENSION IF NOT EXISTS statement for a database extension.
    /// Returns an empty string for dialects that do not support extensions (e.g., SQL Server).
    /// </summary>
    /// <param name="extensionName">The extension name (e.g., "pgcrypto").</param>
    /// <returns>The idempotent CREATE EXTENSION statement, or empty string.</returns>
    string CreateExtensionIfNotExists(string extensionName);

    /// <summary>
    /// Returns the DDL statement to create the UUIDv5 (RFC 4122) helper function in the
    /// specified schema. The function accepts a namespace UUID and a name string and returns
    /// a deterministic UUID that matches DMS Core's ReferentialIdCalculator output byte-for-byte.
    /// </summary>
    /// <param name="schema">The schema to create the function in (typically "dms").</param>
    /// <returns>The complete CREATE FUNCTION statement.</returns>
    string CreateUuidv5Function(DbSchemaName schema);

    // ── Core-table type properties ──────────────────────────────────────

    /// <summary>
    /// Gets the dialect's type for smallint columns.
    /// </summary>
    string SmallintColumnType { get; }

    /// <summary>
    /// Gets the dialect's type for UUID columns (uuid / uniqueidentifier).
    /// </summary>
    string UuidColumnType { get; }

    /// <summary>
    /// Gets the dialect's type for JSON document columns (jsonb / nvarchar(max)).
    /// </summary>
    string JsonColumnType { get; }

    /// <summary>
    /// Gets the dialect's identity bigint column type declaration.
    /// PostgreSQL: <c>bigint GENERATED ALWAYS AS IDENTITY</c>.
    /// SQL Server: <c>bigint IDENTITY(1,1)</c>.
    /// </summary>
    string IdentityBigintColumnType { get; }

    /// <summary>
    /// Gets the dialect's DEFAULT expression for the current UTC timestamp.
    /// PostgreSQL: <c>now()</c>. SQL Server: <c>(sysutcdatetime())</c>.
    /// </summary>
    string CurrentTimestampDefaultExpression { get; }

    // ── Core-table type methods ─────────────────────────────────────────

    /// <summary>
    /// Renders the binary column type for a fixed-length byte array.
    /// PostgreSQL returns <c>bytea</c> (ignores length); SQL Server returns <c>binary(n)</c>.
    /// </summary>
    /// <param name="length">The fixed byte length (used by SQL Server).</param>
    /// <returns>The binary column type string.</returns>
    string RenderBinaryColumnType(int length);

    /// <summary>
    /// Renders a DEFAULT expression that references the next value from a sequence.
    /// PostgreSQL: <c>nextval('"schema"."sequence"')</c>.
    /// SQL Server: <c>(NEXT VALUE FOR [schema].[sequence])</c>.
    /// </summary>
    /// <param name="schema">The schema containing the sequence.</param>
    /// <param name="sequenceName">The sequence name.</param>
    /// <returns>The sequence default expression.</returns>
    string RenderSequenceDefaultExpression(DbSchemaName schema, string sequenceName);

    // ── Literal rendering (for seed DML) ──────────────────────────────────

    /// <summary>
    /// Renders a binary byte[] as a SQL literal.
    /// PostgreSQL: <c>'\xHEX...'::bytea</c>. SQL Server: <c>0xHEX...</c>.
    /// </summary>
    /// <param name="value">The byte array to render.</param>
    /// <returns>The dialect-specific binary literal.</returns>
    string RenderBinaryLiteral(byte[] value);

    /// <summary>
    /// Renders a boolean value as a SQL literal.
    /// PostgreSQL: <c>true</c> / <c>false</c>. SQL Server: <c>1</c> / <c>0</c>.
    /// </summary>
    /// <param name="value">The boolean value.</param>
    /// <returns>The dialect-specific boolean literal.</returns>
    string RenderBooleanLiteral(bool value);

    /// <summary>
    /// Renders a string as a safely-escaped SQL literal.
    /// PostgreSQL: <c>'text'</c>. SQL Server: <c>N'text'</c>.
    /// </summary>
    /// <param name="value">The string value.</param>
    /// <returns>The dialect-specific string literal.</returns>
    string RenderStringLiteral(string value);

    /// <summary>
    /// Renders a smallint as a SQL literal.
    /// </summary>
    /// <param name="value">The smallint value.</param>
    /// <returns>The literal string representation.</returns>
    string RenderSmallintLiteral(short value);

    /// <summary>
    /// Renders an integer as a SQL literal.
    /// </summary>
    /// <param name="value">The integer value.</param>
    /// <returns>The literal string representation.</returns>
    string RenderIntegerLiteral(int value);

    /// <summary>
    /// Renders a computed column definition for a unified alias column.
    /// The computed value is the canonical column when the presence column is non-NULL
    /// (or always, when there is no presence column), otherwise NULL.
    /// PostgreSQL: <c>"alias" type GENERATED ALWAYS AS (CASE WHEN "presence" IS NULL THEN NULL ELSE "canonical" END) STORED</c>.
    /// SQL Server: <c>[alias] AS (CASE WHEN [presence] IS NULL THEN NULL ELSE [canonical] END) PERSISTED</c>.
    /// </summary>
    /// <param name="columnName">The alias column name.</param>
    /// <param name="sqlType">The SQL type for the column.</param>
    /// <param name="canonicalColumn">The source canonical column name.</param>
    /// <param name="presenceColumn">Optional presence-gate column; when NULL, no presence check is performed.</param>
    /// <returns>The computed column definition.</returns>
    string RenderComputedColumnDefinition(
        DbColumnName columnName,
        string sqlType,
        DbColumnName canonicalColumn,
        DbColumnName? presenceColumn
    );
}
