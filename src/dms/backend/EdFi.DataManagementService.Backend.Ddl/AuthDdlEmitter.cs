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
    private static readonly DbColumnName _documentIdCol = new("DocumentId");

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
            EmitTrigger(
                writer,
                entity,
                "Delete",
                "DELETE",
                isLeaf ? EmitLeafDeleteBody : EmitHierarchicalDeleteBody
            );

            // Insert trigger
            EmitTrigger(
                writer,
                entity,
                "Insert",
                "INSERT",
                isLeaf ? EmitLeafInsertBody : EmitHierarchicalInsertBody
            );

            // Update trigger (hierarchical only — leaf entities have no parent FKs to update)
            if (!isLeaf)
            {
                EmitTrigger(writer, entity, "Update", "UPDATE", EmitHierarchicalUpdateBody);
            }
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

    // ── Hierarchical INSERT trigger body ────────────────────────────────

    /// <summary>
    /// Emits the INSERT trigger body for a hierarchical EdOrg (>= 1 parent FK).
    /// Inserts the self-referencing tuple, then ancestor tuples via CROSS JOIN
    /// of source ancestors (resolved through parent DocumentId joins) and
    /// existing descendants. Multi-parent entities combine sources with UNION.
    /// </summary>
    private void EmitHierarchicalInsertBody(SqlWriter writer, AuthEdOrgEntity entity)
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);
        bool isPgsql = _dialect.Rules.Dialect == SqlDialect.Pgsql;

        // Step 1: Self-referencing tuple
        if (isPgsql)
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
        writer.AppendLine();

        // Step 2: Ancestor tuples via CROSS JOIN (sources x descendants)
        writer.AppendLine($"INSERT INTO {authTable} ({source}, {target})");
        writer.AppendLine($"SELECT sources.{source}, targets.{target}");
        writer.AppendLine("FROM (");

        // Source ancestors: one block per parent FK, combined with UNION
        using (writer.Indent())
        {
            for (int i = 0; i < entity.ParentEdOrgFks.Count; i++)
            {
                if (i > 0)
                {
                    writer.AppendLine();
                    writer.AppendLine("UNION");
                    writer.AppendLine();
                }

                EmitSourceAncestorsForFk(writer, entity, entity.ParentEdOrgFks[i], "inserted", "new", "NEW");
            }
        }
        writer.AppendLine(") AS sources");

        writer.AppendLine("CROSS JOIN");
        writer.AppendLine("(");

        // Targets/descendants
        using (writer.Indent())
        {
            EmitDescendantsSubquery(writer, entity, "inserted", "new", "NEW");
        }

        if (isPgsql)
        {
            writer.AppendLine(") AS targets;");
        }
        else
        {
            writer.AppendLine(") AS targets");
            writer.AppendLine($"WHERE sources.{idCol} = targets.{idCol};");
        }
    }

    // ── Hierarchical UPDATE trigger body ────────────────────────────────

    /// <summary>
    /// Emits the UPDATE trigger body for a hierarchical EdOrg (>= 1 parent FK).
    /// Step 1: DELETE invalidated ancestor tuples (with EXCEPT for remaining ancestors).
    /// Step 2: INSERT new ancestor tuples (MERGE for MSSQL, ON CONFLICT for PG).
    /// </summary>
    private void EmitHierarchicalUpdateBody(SqlWriter writer, AuthEdOrgEntity entity)
    {
        EmitUpdateDeleteStep(writer, entity);
        writer.AppendLine();
        EmitUpdateInsertStep(writer, entity);
    }

    /// <summary>
    /// Emits the DELETE step of the UPDATE trigger: removes invalidated ancestor tuples
    /// when parent FK values are cleared or changed. Uses EXCEPT to preserve ancestors
    /// that remain via other parent FK paths.
    /// </summary>
    private void EmitUpdateDeleteStep(SqlWriter writer, AuthEdOrgEntity entity)
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);
        bool isPgsql = _dialect.Rules.Dialect == SqlDialect.Pgsql;

        if (isPgsql)
        {
            writer.AppendLine($"DELETE FROM {authTable}");
            writer.AppendLine($"WHERE ({source}, {target}) IN (");
            using (writer.Indent())
            {
                writer.AppendLine($"SELECT sources.{source}, targets.{target}");
                writer.AppendLine("FROM (");
                using (writer.Indent())
                {
                    EmitUpdateDeleteSources(writer, entity);
                }
                writer.AppendLine(") AS sources");
                writer.AppendLine("CROSS JOIN");
                writer.AppendLine("(");
                using (writer.Indent())
                {
                    EmitDescendantsSubquery(writer, entity, "inserted", "new", "NEW");
                }
                writer.AppendLine(") AS targets");
            }
            writer.AppendLine(");");
        }
        else
        {
            writer.AppendLine("DELETE tbd");
            writer.AppendLine($"FROM {authTable} AS tbd");
            using (writer.Indent())
            {
                writer.AppendLine("INNER JOIN (");
                using (writer.Indent())
                {
                    writer.AppendLine($"SELECT d1.{source}, d2.{target}");
                    writer.AppendLine("FROM (");
                    using (writer.Indent())
                    {
                        EmitUpdateDeleteSources(writer, entity);
                    }
                    writer.AppendLine(") AS d1");
                    writer.AppendLine("CROSS JOIN");
                    writer.AppendLine("(");
                    using (writer.Indent())
                    {
                        EmitDescendantsSubquery(writer, entity, "inserted", "new", "NEW");
                    }
                    writer.AppendLine(") AS d2");
                    writer.AppendLine($"WHERE d1.{idCol} = d2.{idCol}");
                }
                writer.AppendLine(") AS cj");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON tbd.{source} = cj.{source}");
                    writer.AppendLine($"AND tbd.{target} = cj.{target};");
                }
            }
        }
    }

    /// <summary>
    /// Emits the combined sources block for the DELETE step: UNION of "old ancestors
    /// to remove" for each FK, followed by EXCEPT of "ancestors to keep" for each FK.
    /// </summary>
    private void EmitUpdateDeleteSources(SqlWriter writer, AuthEdOrgEntity entity)
    {
        for (int i = 0; i < entity.ParentEdOrgFks.Count; i++)
        {
            if (i > 0)
            {
                writer.AppendLine();
                writer.AppendLine("UNION");
                writer.AppendLine();
            }
            EmitUpdateOldAncestorsForFk(writer, entity, entity.ParentEdOrgFks[i]);
        }

        foreach (var fk in entity.ParentEdOrgFks)
        {
            writer.AppendLine();
            writer.AppendLine("EXCEPT");
            writer.AppendLine();
            EmitUpdateKeepAncestorsForFk(writer, entity, fk);
        }
    }

    /// <summary>
    /// Emits one parent FK's "old ancestors to remove" SELECT for the DELETE step.
    /// Resolves the OLD parent FK via DocumentId join, with change detection guards.
    /// </summary>
    private void EmitUpdateOldAncestorsForFk(SqlWriter writer, AuthEdOrgEntity entity, AuthParentEdOrgFk fk)
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);
        var fkCol = Quote(fk.FkColumn);
        var parentTable = Quote(fk.ParentTable);
        var parentIdCol = Quote(fk.ParentIdentityColumn);
        var docIdCol = Quote(_documentIdCol);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"SELECT tuples.{source}");
            writer.AppendLine($"FROM {parentTable} AS parent");
            using (writer.Indent())
            {
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{parentIdCol} = tuples.{target}");
                }
            }
            writer.AppendLine($"WHERE parent.{docIdCol} = OLD.{fkCol}");
            using (writer.Indent())
            {
                writer.AppendLine($"AND OLD.{fkCol} IS NOT NULL");
                writer.AppendLine($"AND (NEW.{fkCol} IS NULL OR OLD.{fkCol} <> NEW.{fkCol})");
            }
        }
        else
        {
            writer.AppendLine($"SELECT tuples.{source}, new.{idCol}");
            writer.AppendLine("FROM inserted new");
            using (writer.Indent())
            {
                writer.AppendLine("INNER JOIN deleted old");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON old.{idCol} = new.{idCol}");
                }
                writer.AppendLine($"INNER JOIN {parentTable} AS parent");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{docIdCol} = old.{fkCol}");
                }
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{parentIdCol} = tuples.{target}");
                }
            }
            writer.AppendLine($"WHERE old.{fkCol} IS NOT NULL");
            using (writer.Indent())
            {
                writer.AppendLine($"AND (new.{fkCol} IS NULL OR old.{fkCol} <> new.{fkCol})");
            }
        }
    }

    /// <summary>
    /// Emits one parent FK's "ancestors to keep" SELECT for the DELETE step EXCEPT clause.
    /// Resolves the NEW parent FK via DocumentId join, no change detection needed.
    /// </summary>
    private void EmitUpdateKeepAncestorsForFk(SqlWriter writer, AuthEdOrgEntity entity, AuthParentEdOrgFk fk)
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);
        var fkCol = Quote(fk.FkColumn);
        var parentTable = Quote(fk.ParentTable);
        var parentIdCol = Quote(fk.ParentIdentityColumn);
        var docIdCol = Quote(_documentIdCol);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"SELECT tuples.{source}");
            writer.AppendLine($"FROM {parentTable} AS parent");
            using (writer.Indent())
            {
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{parentIdCol} = tuples.{target}");
                }
            }
            writer.AppendLine($"WHERE parent.{docIdCol} = NEW.{fkCol}");
        }
        else
        {
            writer.AppendLine($"SELECT tuples.{source}, new.{idCol}");
            writer.AppendLine("FROM inserted new");
            using (writer.Indent())
            {
                writer.AppendLine($"INNER JOIN {parentTable} AS parent");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{docIdCol} = new.{fkCol}");
                }
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{parentIdCol} = tuples.{target}");
                }
            }
        }
    }

    /// <summary>
    /// Emits the INSERT step of the UPDATE trigger: adds new ancestor tuples
    /// when parent FK values are initialized or changed. Uses MERGE for MSSQL
    /// and INSERT ON CONFLICT for PgSQL to handle idempotent upserts.
    /// </summary>
    private void EmitUpdateInsertStep(SqlWriter writer, AuthEdOrgEntity entity)
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);
        bool isPgsql = _dialect.Rules.Dialect == SqlDialect.Pgsql;

        if (isPgsql)
        {
            writer.AppendLine($"INSERT INTO {authTable} ({source}, {target})");
            writer.AppendLine($"SELECT sources.{source}, targets.{target}");
            writer.AppendLine("FROM (");
            using (writer.Indent())
            {
                EmitUpdateInsertSources(writer, entity);
            }
            writer.AppendLine(") AS sources");
            writer.AppendLine("CROSS JOIN");
            writer.AppendLine("(");
            using (writer.Indent())
            {
                EmitDescendantsSubquery(writer, entity, "inserted", "new", "NEW");
            }
            writer.AppendLine(") AS targets");
            writer.AppendLine($"ON CONFLICT ({source}, {target}) DO NOTHING;");
        }
        else
        {
            writer.AppendLine($"MERGE INTO {authTable} target");
            writer.AppendLine("USING (");
            using (writer.Indent())
            {
                writer.AppendLine($"SELECT sources.{source}, targets.{target}");
                writer.AppendLine("FROM (");
                using (writer.Indent())
                {
                    EmitUpdateInsertSources(writer, entity);
                }
                writer.AppendLine(") AS sources");
                writer.AppendLine("CROSS JOIN");
                writer.AppendLine("(");
                using (writer.Indent())
                {
                    EmitDescendantsSubquery(writer, entity, "inserted", "new", "NEW");
                }
                writer.AppendLine(") AS targets");
                writer.AppendLine($"WHERE sources.{idCol} = targets.{idCol}");
            }
            writer.AppendLine(") AS source");
            using (writer.Indent())
            {
                writer.AppendLine($"ON target.{source} = source.{source}");
                writer.AppendLine($"AND target.{target} = source.{target}");
            }
            writer.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
            using (writer.Indent())
            {
                writer.AppendLine($"INSERT ({source}, {target})");
                writer.AppendLine($"VALUES (source.{source}, source.{target});");
            }
        }
    }

    /// <summary>
    /// Emits the combined sources block for the INSERT step: UNION of "new ancestors
    /// to add" for each FK, with change detection guards.
    /// </summary>
    private void EmitUpdateInsertSources(SqlWriter writer, AuthEdOrgEntity entity)
    {
        for (int i = 0; i < entity.ParentEdOrgFks.Count; i++)
        {
            if (i > 0)
            {
                writer.AppendLine();
                writer.AppendLine("UNION");
                writer.AppendLine();
            }
            EmitUpdateNewAncestorsForFk(writer, entity, entity.ParentEdOrgFks[i]);
        }
    }

    /// <summary>
    /// Emits one parent FK's "new ancestors to add" SELECT for the INSERT step.
    /// Resolves the NEW parent FK via DocumentId join, with change detection guards.
    /// </summary>
    private void EmitUpdateNewAncestorsForFk(SqlWriter writer, AuthEdOrgEntity entity, AuthParentEdOrgFk fk)
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);
        var fkCol = Quote(fk.FkColumn);
        var parentTable = Quote(fk.ParentTable);
        var parentIdCol = Quote(fk.ParentIdentityColumn);
        var docIdCol = Quote(_documentIdCol);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"SELECT tuples.{source}");
            writer.AppendLine($"FROM {parentTable} AS parent");
            using (writer.Indent())
            {
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{parentIdCol} = tuples.{target}");
                }
            }
            writer.AppendLine($"WHERE parent.{docIdCol} = NEW.{fkCol}");
            using (writer.Indent())
            {
                writer.AppendLine(
                    $"AND ((OLD.{fkCol} IS NULL AND NEW.{fkCol} IS NOT NULL) OR OLD.{fkCol} <> NEW.{fkCol})"
                );
            }
        }
        else
        {
            writer.AppendLine($"SELECT tuples.{source}, new.{idCol}");
            writer.AppendLine("FROM inserted new");
            using (writer.Indent())
            {
                writer.AppendLine("INNER JOIN deleted old");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON new.{idCol} = old.{idCol}");
                }
                writer.AppendLine($"INNER JOIN {parentTable} AS parent");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{docIdCol} = new.{fkCol}");
                }
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{parentIdCol} = tuples.{target}");
                }
            }
            writer.AppendLine($"WHERE (old.{fkCol} IS NULL AND new.{fkCol} IS NOT NULL)");
            using (writer.Indent())
            {
                writer.AppendLine($"OR old.{fkCol} <> new.{fkCol}");
            }
        }
    }

    // ── Hierarchical DELETE trigger body ────────────────────────────────

    /// <summary>
    /// Emits the DELETE trigger body for a hierarchical EdOrg (>= 1 parent FK).
    /// Step 1: Remove ancestor tuples via CROSS JOIN of old ancestors (resolved
    /// through parent DocumentId joins) and old descendants. Multi-parent entities
    /// combine source blocks with UNION. Step 2: Remove self-referencing tuple.
    /// Ancestor removal precedes self-tuple removal so the descendants subquery
    /// still finds the entity itself.
    /// </summary>
    private void EmitHierarchicalDeleteBody(SqlWriter writer, AuthEdOrgEntity entity)
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);
        bool isPgsql = _dialect.Rules.Dialect == SqlDialect.Pgsql;

        // Step 1: Remove ancestor tuples (CROSS JOIN old ancestors x old descendants)
        if (isPgsql)
        {
            writer.AppendLine($"DELETE FROM {authTable}");
            writer.AppendLine($"WHERE ({source}, {target}) IN (");
            using (writer.Indent())
            {
                writer.AppendLine($"SELECT sources.{source}, targets.{target}");
                writer.AppendLine("FROM (");
                using (writer.Indent())
                {
                    for (int i = 0; i < entity.ParentEdOrgFks.Count; i++)
                    {
                        if (i > 0)
                        {
                            writer.AppendLine();
                            writer.AppendLine("UNION");
                            writer.AppendLine();
                        }
                        EmitSourceAncestorsForFk(
                            writer,
                            entity,
                            entity.ParentEdOrgFks[i],
                            "deleted",
                            "old",
                            "OLD"
                        );
                    }
                }
                writer.AppendLine(") AS sources");
                writer.AppendLine("CROSS JOIN");
                writer.AppendLine("(");
                using (writer.Indent())
                {
                    EmitDescendantsSubquery(writer, entity, "deleted", "old", "OLD");
                }
                writer.AppendLine(") AS targets");
            }
            writer.AppendLine(");");
        }
        else
        {
            writer.AppendLine("DELETE tbd");
            writer.AppendLine($"FROM {authTable} AS tbd");
            using (writer.Indent())
            {
                writer.AppendLine("INNER JOIN (");
                using (writer.Indent())
                {
                    writer.AppendLine($"SELECT d1.{source}, d2.{target}");
                    writer.AppendLine("FROM (");
                    using (writer.Indent())
                    {
                        for (int i = 0; i < entity.ParentEdOrgFks.Count; i++)
                        {
                            if (i > 0)
                            {
                                writer.AppendLine();
                                writer.AppendLine("UNION");
                                writer.AppendLine();
                            }
                            EmitSourceAncestorsForFk(
                                writer,
                                entity,
                                entity.ParentEdOrgFks[i],
                                "deleted",
                                "old",
                                "OLD"
                            );
                        }
                    }
                    writer.AppendLine(") AS d1");
                    writer.AppendLine("CROSS JOIN");
                    writer.AppendLine("(");
                    using (writer.Indent())
                    {
                        EmitDescendantsSubquery(writer, entity, "deleted", "old", "OLD");
                    }
                    writer.AppendLine(") AS d2");
                    writer.AppendLine($"WHERE d1.{idCol} = d2.{idCol}");
                }
                writer.AppendLine(") AS cj");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON tbd.{source} = cj.{source}");
                    writer.AppendLine($"AND tbd.{target} = cj.{target};");
                }
            }
        }
        writer.AppendLine();

        // Step 2: Remove self-referencing tuple
        if (isPgsql)
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

    // ── Reusable trigger subquery helpers ────────────────────────────────

    /// <summary>
    /// Emits a source ancestor SELECT for a single parent FK, resolving the
    /// parent's EducationOrganizationId via a DocumentId join to the parent table.
    /// For multi-parent entities, the caller combines multiple blocks with UNION.
    /// </summary>
    /// <remarks>
    /// Used by INSERT and DELETE trigger bodies (UPDATE triggers use a different
    /// pattern with additional WHERE conditions for change detection).
    /// </remarks>
    private void EmitSourceAncestorsForFk(
        SqlWriter writer,
        AuthEdOrgEntity entity,
        AuthParentEdOrgFk fk,
        string mssqlPseudoTable,
        string mssqlAlias,
        string pgsqlRecord
    )
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);
        var fkCol = Quote(fk.FkColumn);
        var parentTable = Quote(fk.ParentTable);
        var parentIdCol = Quote(fk.ParentIdentityColumn);
        var docIdCol = Quote(_documentIdCol);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"SELECT tuples.{source}");
            writer.AppendLine($"FROM {parentTable} AS parent");
            using (writer.Indent())
            {
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{parentIdCol} = tuples.{target}");
                }
            }
            writer.AppendLine($"WHERE parent.{docIdCol} = {pgsqlRecord}.{fkCol}");
            using (writer.Indent())
            {
                writer.AppendLine($"AND {pgsqlRecord}.{fkCol} IS NOT NULL");
            }
        }
        else
        {
            writer.AppendLine($"SELECT tuples.{source}, {mssqlAlias}.{idCol}");
            writer.AppendLine($"FROM {mssqlPseudoTable} {mssqlAlias}");
            using (writer.Indent())
            {
                writer.AppendLine($"INNER JOIN {parentTable} AS parent");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{docIdCol} = {mssqlAlias}.{fkCol}");
                }
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{parentIdCol} = tuples.{target}");
                }
            }
            writer.AppendLine($"WHERE {mssqlAlias}.{fkCol} IS NOT NULL");
        }
    }

    /// <summary>
    /// Emits a descendants SELECT subquery that finds all existing descendants
    /// of the current entity by looking up tuples where the entity's
    /// EducationOrganizationId is the source.
    /// </summary>
    /// <remarks>
    /// Used by INSERT and DELETE trigger bodies for the CROSS JOIN targets side.
    /// </remarks>
    private void EmitDescendantsSubquery(
        SqlWriter writer,
        AuthEdOrgEntity entity,
        string mssqlPseudoTable,
        string mssqlAlias,
        string pgsqlRecord
    )
    {
        var authTable = Quote(_authTable);
        var source = Quote(_sourceCol);
        var target = Quote(_targetCol);
        var idCol = Quote(entity.IdentityColumn);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"SELECT tuples.{target}");
            writer.AppendLine($"FROM {authTable} AS tuples");
            writer.AppendLine($"WHERE tuples.{source} = {pgsqlRecord}.{idCol}");
        }
        else
        {
            writer.AppendLine($"SELECT {mssqlAlias}.{idCol}, tuples.{target}");
            writer.AppendLine($"FROM {mssqlPseudoTable} {mssqlAlias}");
            using (writer.Indent())
            {
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON {mssqlAlias}.{idCol} = tuples.{source}");
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
