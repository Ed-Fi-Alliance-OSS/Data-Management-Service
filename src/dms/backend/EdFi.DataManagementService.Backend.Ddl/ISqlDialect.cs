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
}
