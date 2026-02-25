// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits deterministic DDL for the <c>auth.*</c> schema objects that support
/// EducationOrganization hierarchy authorization.
/// </summary>
/// <remarks>
/// This includes the <c>auth.EducationOrganizationIdToEducationOrganizationId</c>
/// table, its covering index, and triggers on all concrete EducationOrganization
/// tables that keep the hierarchy up to date on INSERT, UPDATE, and DELETE.
///
/// Emission follows a strict phased order for deterministic output:
/// Schemas → Tables → Indexes → Triggers.
/// </remarks>
public sealed class AuthDdlEmitter(ISqlDialect dialect, AuthEdOrgHierarchy hierarchy)
{
    private readonly ISqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    private readonly AuthEdOrgHierarchy _hierarchy =
        hierarchy ?? throw new ArgumentNullException(nameof(hierarchy));

    private static readonly DbTableName _authTable = AuthTableNames.EdOrgIdToEdOrgId;
    private static readonly DbColumnName _sourceCol = AuthTableNames.SourceEdOrgId;
    private static readonly DbColumnName _targetCol = AuthTableNames.TargetEdOrgId;

    /// <summary>
    /// Generates the complete <c>auth.*</c> DDL script for the configured dialect.
    /// </summary>
    public string Emit()
    {
        var writer = new SqlWriter(_dialect);

        EmitSchemas(writer);
        EmitTables(writer);
        EmitIndexes(writer);
        EmitTriggers(writer);

        return writer.ToString();
    }

    // ── Phase 1: Schemas ────────────────────────────────────────────────

    private void EmitSchemas(SqlWriter writer)
    {
        writer.WritePhaseHeader(1, "Schemas");

        writer.AppendLine(_dialect.CreateSchemaIfNotExists(AuthTableNames.AuthSchema));
        writer.AppendLine();
    }

    // ── Phase 2: Tables ─────────────────────────────────────────────────

    private void EmitTables(SqlWriter writer)
    {
        writer.WritePhaseHeader(2, "Tables");

        EmitEdOrgIdToEdOrgIdTable(writer);
    }

    /// <summary>
    /// Emits the <c>auth.EducationOrganizationIdToEducationOrganizationId</c> table.
    /// Two BIGINT NOT NULL columns with a composite clustered primary key.
    /// </summary>
    private void EmitEdOrgIdToEdOrgIdTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_authTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine($"{_dialect.RenderColumnDefinition(_sourceCol, "bigint", false)},");
            writer.AppendLine($"{_dialect.RenderColumnDefinition(_targetCol, "bigint", false)},");
            writer.AppendLine(
                _dialect.RenderNamedPrimaryKeyClause(
                    "PK_EducationOrganizationIdToEducationOrganizationId",
                    [_sourceCol, _targetCol]
                )
            );
        }
        writer.AppendLine(");");
        writer.AppendLine();
    }

    // ── Phase 3: Indexes ────────────────────────────────────────────────

    private void EmitIndexes(SqlWriter writer)
    {
        writer.WritePhaseHeader(3, "Indexes");

        EmitEdOrgIdCoveringIndex(writer);
    }

    /// <summary>
    /// Emits a covering nonclustered index on <c>TargetEducationOrganizationId</c>
    /// with <c>SourceEducationOrganizationId</c> as an INCLUDE column.
    /// </summary>
    private void EmitEdOrgIdCoveringIndex(SqlWriter writer)
    {
        var indexName = "IX_EducationOrganizationIdToEducationOrganizationId_Target";
        var qualifiedTable = _dialect.QualifyTable(_authTable);
        var quotedTarget = _dialect.QuoteIdentifier(_targetCol.Value);
        var quotedSource = _dialect.QuoteIdentifier(_sourceCol.Value);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            var qualifiedIndex =
                $"{_dialect.QuoteIdentifier(AuthTableNames.AuthSchema.Value)}.{_dialect.QuoteIdentifier(indexName)}";
            writer.AppendLine(
                $"CREATE INDEX IF NOT EXISTS {qualifiedIndex} ON {qualifiedTable} ({quotedTarget}) INCLUDE ({quotedSource});"
            );
        }
        else
        {
            var escapedSchema = AuthTableNames.AuthSchema.Value.Replace("'", "''");
            var escapedTable = _authTable.Name.Replace("'", "''");
            var escapedIndex = indexName.Replace("'", "''");
            var quotedIndex = _dialect.QuoteIdentifier(indexName);

            writer.AppendLine("IF NOT EXISTS (");
            using (writer.Indent())
            {
                writer.AppendLine("SELECT 1 FROM sys.indexes i");
                writer.AppendLine("JOIN sys.tables t ON i.object_id = t.object_id");
                writer.AppendLine("JOIN sys.schemas s ON t.schema_id = s.schema_id");
                writer.AppendLine(
                    $"WHERE s.name = N'{escapedSchema}' AND t.name = N'{escapedTable}' AND i.name = N'{escapedIndex}'"
                );
            }
            writer.AppendLine(")");
            writer.AppendLine(
                $"CREATE NONCLUSTERED INDEX {quotedIndex} ON {qualifiedTable} ({quotedTarget}) INCLUDE ({quotedSource});"
            );
        }
        writer.AppendLine();
    }

    // ── Phase 4: Triggers ───────────────────────────────────────────────

    private void EmitTriggers(SqlWriter writer)
    {
        if (_hierarchy.EntitiesInNameOrder.Count == 0)
        {
            return;
        }

        writer.WritePhaseHeader(4, "Triggers");

        // Triggers are emitted alphabetically by entity name,
        // then by trigger type (Delete, Insert, Update) for determinism.
        foreach (var entity in _hierarchy.EntitiesInNameOrder)
        {
            bool isLeaf = entity.ParentEdOrgFks.Count == 0;

            // Delete trigger (alphabetically first)
            if (isLeaf)
            {
                EmitTrigger(writer, entity, "Delete", "DELETE", EmitLeafDeleteBody);
            }

            // Insert trigger
            if (isLeaf)
            {
                EmitTrigger(writer, entity, "Insert", "INSERT", EmitLeafInsertBody);
            }

            // Hierarchical triggers (Insert, Update, Delete) will be added in tasks 4-6.
        }
    }

    // ── Trigger scaffolding ─────────────────────────────────────────────

    /// <summary>
    /// Emits a complete trigger (scaffolding + body) dispatching to the correct dialect.
    /// </summary>
    private void EmitTrigger(
        SqlWriter writer,
        AuthEdOrgEntity entity,
        string triggerSuffix,
        string triggerEvent,
        Action<SqlWriter, AuthEdOrgEntity> emitBody
    )
    {
        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlTrigger(writer, entity, triggerSuffix, triggerEvent, emitBody);
        }
        else
        {
            EmitMssqlTrigger(writer, entity, triggerSuffix, triggerEvent, emitBody);
        }
    }

    private void EmitPgsqlTrigger(
        SqlWriter writer,
        AuthEdOrgEntity entity,
        string triggerSuffix,
        string triggerEvent,
        Action<SqlWriter, AuthEdOrgEntity> emitBody
    )
    {
        var schema = entity.Table.Schema;
        var funcName = $"TF_{entity.EntityName}_AuthHierarchy_{triggerSuffix}";
        var triggerName = $"TR_{entity.EntityName}_AuthHierarchy_{triggerSuffix}";

        // Trigger function
        writer.AppendLine($"CREATE OR REPLACE FUNCTION {Quote(schema)}.{Quote(funcName)}()");
        writer.AppendLine("RETURNS TRIGGER AS $$");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            emitBody(writer, entity);
            writer.AppendLine("RETURN NULL;");
        }
        writer.AppendLine("END;");
        writer.AppendLine("$$ LANGUAGE plpgsql;");
        writer.AppendLine();

        // Drop + Create trigger
        writer.AppendLine(_dialect.DropTriggerIfExists(entity.Table, triggerName));
        writer.AppendLine($"CREATE TRIGGER {Quote(triggerName)}");
        using (writer.Indent())
        {
            writer.AppendLine($"AFTER {triggerEvent} ON {Quote(entity.Table)}");
            writer.AppendLine("FOR EACH ROW");
            writer.AppendLine($"EXECUTE FUNCTION {Quote(schema)}.{Quote(funcName)}();");
        }
        writer.AppendLine();
    }

    private void EmitMssqlTrigger(
        SqlWriter writer,
        AuthEdOrgEntity entity,
        string triggerSuffix,
        string triggerEvent,
        Action<SqlWriter, AuthEdOrgEntity> emitBody
    )
    {
        var schema = entity.Table.Schema;
        var triggerName = $"TR_{entity.EntityName}_AuthHierarchy_{triggerSuffix}";

        writer.AppendLine("GO");
        writer.AppendLine($"CREATE OR ALTER TRIGGER {Quote(schema)}.{Quote(triggerName)}");
        writer.AppendLine($"ON {Quote(entity.Table)}");
        writer.AppendLine($"AFTER {triggerEvent}");
        writer.AppendLine("AS");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("SET NOCOUNT ON;");
            emitBody(writer, entity);
        }
        writer.AppendLine("END;");
        writer.AppendLine();
    }

    // ── Leaf trigger bodies ─────────────────────────────────────────────

    /// <summary>
    /// Emits the INSERT trigger body for a leaf EdOrg (no parent FKs).
    /// Inserts the self-referencing tuple <c>(EdOrgId, EdOrgId)</c>.
    /// </summary>
    private void EmitLeafInsertBody(SqlWriter writer, AuthEdOrgEntity entity)
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"INSERT INTO {authTable} ({source}, {target})");
            writer.AppendLine($"VALUES (NEW.{idCol}, NEW.{idCol});");
        }
        else
        {
            writer.AppendLine($"INSERT INTO {authTable} ({source}, {target})");
            writer.AppendLine($"SELECT new.{idCol}, new.{idCol}");
            writer.AppendLine("FROM inserted new;");
        }
    }

    /// <summary>
    /// Emits the DELETE trigger body for a leaf EdOrg (no parent FKs).
    /// Removes the self-referencing tuple.
    /// </summary>
    private void EmitLeafDeleteBody(SqlWriter writer, AuthEdOrgEntity entity)
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"DELETE FROM {authTable}");
            writer.AppendLine($"WHERE {source} = OLD.{idCol} AND {target} = OLD.{idCol};");
        }
        else
        {
            writer.AppendLine($"DELETE tuples");
            writer.AppendLine($"FROM {authTable} AS tuples");
            using (writer.Indent())
            {
                writer.AppendLine("INNER JOIN deleted old");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON tuples.{source} = old.{idCol}");
                    writer.AppendLine($"AND tuples.{target} = old.{idCol};");
                }
            }
        }
    }

    // ── Quoting helpers ─────────────────────────────────────────────────

    private string Quote(string identifier) => _dialect.QuoteIdentifier(identifier);

    private string Quote(DbSchemaName schema) => _dialect.QuoteIdentifier(schema.Value);

    private string Quote(DbTableName table) => _dialect.QualifyTable(table);

    private string Quote(DbColumnName column) => _dialect.QuoteIdentifier(column.Value);
}
