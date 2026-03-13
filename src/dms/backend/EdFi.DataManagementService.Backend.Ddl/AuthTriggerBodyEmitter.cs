// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits trigger body SQL for auth hierarchy maintenance triggers.
/// Extracted from <c>AuthDdlEmitter</c> to be consumed by <see cref="RelationalModelDdlEmitter"/>.
/// </summary>
internal static class AuthTriggerBodyEmitter
{
    private static readonly DbTableName _authTable = AuthTableNames.EdOrgIdToEdOrgId;
    private static readonly DbColumnName _sourceCol = AuthTableNames.SourceEdOrgId;
    private static readonly DbColumnName _targetCol = AuthTableNames.TargetEdOrgId;
    private static readonly DbColumnName _documentIdCol = new("DocumentId");

    /// <summary>
    /// Groups dialect-specific pseudo-table parameters for trigger body emission.
    /// </summary>
    private readonly record struct TriggerContext(
        string MssqlPseudoTable,
        string MssqlAlias,
        string PgsqlRecord
    );

    private readonly record struct QuotedNames(string AuthTable, string Source, string Target, string IdCol);

    private static readonly TriggerContext _insertedContext = new("inserted", "new", "NEW");
    private static readonly TriggerContext _deletedContext = new("deleted", "old", "OLD");

    /// <summary>
    /// Emits the trigger body for the given auth hierarchy trigger kind.
    /// </summary>
    public static void EmitBody(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity,
        AuthHierarchyTriggerEvent triggerEvent
    )
    {
        bool isLeaf = entity.ParentEdOrgFks.Count == 0;

        switch (triggerEvent)
        {
            case AuthHierarchyTriggerEvent.Insert:
                if (isLeaf)
                    EmitLeafInsertBody(writer, dialect, entity);
                else
                    EmitHierarchicalInsertBody(writer, dialect, entity);
                break;
            case AuthHierarchyTriggerEvent.Delete:
                if (isLeaf)
                    EmitLeafDeleteBody(writer, dialect, entity);
                else
                    EmitHierarchicalDeleteBody(writer, dialect, entity);
                break;
            case AuthHierarchyTriggerEvent.Update:
                EmitHierarchicalUpdateBody(writer, dialect, entity);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(triggerEvent));
        }
    }

    // ── Leaf trigger bodies ─────────────────────────────────────────────

    private static void EmitLeafInsertBody(SqlWriter writer, ISqlDialect dialect, AuthEdOrgEntity entity)
    {
        EmitSelfTupleInsert(writer, dialect, entity);
    }

    private static void EmitLeafDeleteBody(SqlWriter writer, ISqlDialect dialect, AuthEdOrgEntity entity)
    {
        EmitSelfTupleDelete(writer, dialect, entity);
    }

    // ── Hierarchical INSERT trigger body ────────────────────────────────

    private static void EmitHierarchicalInsertBody(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity
    )
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);
        bool isPgsql = dialect.Rules.Dialect == SqlDialect.Pgsql;

        // Step 1: Self-referencing tuple
        EmitSelfTupleInsert(writer, dialect, entity);
        writer.AppendLine();

        // Step 2: Ancestor tuples via CROSS JOIN (sources x descendants)
        writer.AppendLine($"INSERT INTO {authTable} ({source}, {target})");
        writer.AppendLine($"SELECT sources.{source}, targets.{target}");
        writer.AppendLine("FROM (");

        using (writer.Indent())
        {
            EmitUnionedSourceAncestors(writer, dialect, entity, _insertedContext);
        }
        writer.AppendLine(") AS sources");

        writer.AppendLine("CROSS JOIN");
        writer.AppendLine("(");

        using (writer.Indent())
        {
            EmitDescendantsSubquery(writer, dialect, entity, _insertedContext);
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

    private static void EmitHierarchicalUpdateBody(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity
    )
    {
        EmitUpdateDeleteStep(writer, dialect, entity);
        writer.AppendLine();
        EmitUpdateInsertStep(writer, dialect, entity);
    }

    private static void EmitUpdateDeleteStep(SqlWriter writer, ISqlDialect dialect, AuthEdOrgEntity entity)
    {
        EmitCrossJoinDelete(
            writer,
            dialect,
            entity,
            w => EmitUpdateDeleteSources(w, dialect, entity),
            w => EmitDescendantsSubquery(w, dialect, entity, _insertedContext)
        );
    }

    private static void EmitUpdateDeleteSources(SqlWriter writer, ISqlDialect dialect, AuthEdOrgEntity entity)
    {
        for (int i = 0; i < entity.ParentEdOrgFks.Count; i++)
        {
            if (i > 0)
            {
                writer.AppendLine();
                writer.AppendLine("UNION");
                writer.AppendLine();
            }
            EmitUpdateOldAncestorsForFk(writer, dialect, entity, entity.ParentEdOrgFks[i]);
        }

        foreach (var fk in entity.ParentEdOrgFks)
        {
            writer.AppendLine();
            writer.AppendLine("EXCEPT");
            writer.AppendLine();
            EmitUpdateKeepAncestorsForFk(writer, dialect, entity, fk);
        }
    }

    private static void EmitUpdateOldAncestorsForFk(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity,
        AuthParentEdOrgFk fk
    )
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);
        var fkCol = Quote(dialect, fk.FkColumn);
        var parentTable = Quote(dialect, fk.ParentTable);
        var parentIdCol = Quote(dialect, fk.ParentIdentityColumn);
        var docIdCol = Quote(dialect, _documentIdCol);

        if (dialect.Rules.Dialect == SqlDialect.Pgsql)
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

    private static void EmitUpdateKeepAncestorsForFk(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity,
        AuthParentEdOrgFk fk
    )
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);
        var fkCol = Quote(dialect, fk.FkColumn);
        var parentTable = Quote(dialect, fk.ParentTable);
        var parentIdCol = Quote(dialect, fk.ParentIdentityColumn);
        var docIdCol = Quote(dialect, _documentIdCol);

        if (dialect.Rules.Dialect == SqlDialect.Pgsql)
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

    private static void EmitUpdateInsertStep(SqlWriter writer, ISqlDialect dialect, AuthEdOrgEntity entity)
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);
        bool isPgsql = dialect.Rules.Dialect == SqlDialect.Pgsql;

        if (isPgsql)
        {
            writer.AppendLine($"INSERT INTO {authTable} ({source}, {target})");
            writer.AppendLine($"SELECT sources.{source}, targets.{target}");
            writer.AppendLine("FROM (");
            using (writer.Indent())
            {
                EmitUpdateInsertSources(writer, dialect, entity);
            }
            writer.AppendLine(") AS sources");
            writer.AppendLine("CROSS JOIN");
            writer.AppendLine("(");
            using (writer.Indent())
            {
                EmitDescendantsSubquery(writer, dialect, entity, _insertedContext);
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
                    EmitUpdateInsertSources(writer, dialect, entity);
                }
                writer.AppendLine(") AS sources");
                writer.AppendLine("CROSS JOIN");
                writer.AppendLine("(");
                using (writer.Indent())
                {
                    EmitDescendantsSubquery(writer, dialect, entity, _insertedContext);
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

    private static void EmitUpdateInsertSources(SqlWriter writer, ISqlDialect dialect, AuthEdOrgEntity entity)
    {
        for (int i = 0; i < entity.ParentEdOrgFks.Count; i++)
        {
            if (i > 0)
            {
                writer.AppendLine();
                writer.AppendLine("UNION");
                writer.AppendLine();
            }
            EmitUpdateNewAncestorsForFk(writer, dialect, entity, entity.ParentEdOrgFks[i]);
        }
    }

    private static void EmitUpdateNewAncestorsForFk(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity,
        AuthParentEdOrgFk fk
    )
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);
        var fkCol = Quote(dialect, fk.FkColumn);
        var parentTable = Quote(dialect, fk.ParentTable);
        var parentIdCol = Quote(dialect, fk.ParentIdentityColumn);
        var docIdCol = Quote(dialect, _documentIdCol);

        if (dialect.Rules.Dialect == SqlDialect.Pgsql)
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

    private static void EmitHierarchicalDeleteBody(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity
    )
    {
        // Step 1: Remove ancestor tuples
        EmitCrossJoinDelete(
            writer,
            dialect,
            entity,
            w => EmitUnionedSourceAncestors(w, dialect, entity, _deletedContext),
            w => EmitDescendantsSubquery(w, dialect, entity, _deletedContext)
        );
        writer.AppendLine();

        // Step 2: Remove self-referencing tuple
        EmitSelfTupleDelete(writer, dialect, entity);
    }

    // ── Reusable trigger helpers ────────────────────────────────────────

    private static void EmitSelfTupleInsert(SqlWriter writer, ISqlDialect dialect, AuthEdOrgEntity entity)
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);

        if (dialect.Rules.Dialect == SqlDialect.Pgsql)
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

    private static void EmitSelfTupleDelete(SqlWriter writer, ISqlDialect dialect, AuthEdOrgEntity entity)
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);

        if (dialect.Rules.Dialect == SqlDialect.Pgsql)
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

    private static void EmitCrossJoinDelete(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity,
        Action<SqlWriter> emitSources,
        Action<SqlWriter> emitTargets
    )
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);

        if (dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"DELETE FROM {authTable}");
            writer.AppendLine($"WHERE ({source}, {target}) IN (");
            using (writer.Indent())
            {
                writer.AppendLine($"SELECT sources.{source}, targets.{target}");
                writer.AppendLine("FROM (");
                using (writer.Indent())
                {
                    emitSources(writer);
                }
                writer.AppendLine(") AS sources");
                writer.AppendLine("CROSS JOIN");
                writer.AppendLine("(");
                using (writer.Indent())
                {
                    emitTargets(writer);
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
                        emitSources(writer);
                    }
                    writer.AppendLine(") AS d1");
                    writer.AppendLine("CROSS JOIN");
                    writer.AppendLine("(");
                    using (writer.Indent())
                    {
                        emitTargets(writer);
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

    private static void EmitUnionedSourceAncestors(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity,
        TriggerContext ctx
    )
    {
        for (int i = 0; i < entity.ParentEdOrgFks.Count; i++)
        {
            if (i > 0)
            {
                writer.AppendLine();
                writer.AppendLine("UNION");
                writer.AppendLine();
            }

            EmitSourceAncestorsForFk(writer, dialect, entity, entity.ParentEdOrgFks[i], ctx);
        }
    }

    private static void EmitSourceAncestorsForFk(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity,
        AuthParentEdOrgFk fk,
        TriggerContext ctx
    )
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);
        var fkCol = Quote(dialect, fk.FkColumn);
        var parentTable = Quote(dialect, fk.ParentTable);
        var parentIdCol = Quote(dialect, fk.ParentIdentityColumn);
        var docIdCol = Quote(dialect, _documentIdCol);

        if (dialect.Rules.Dialect == SqlDialect.Pgsql)
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
            writer.AppendLine($"WHERE parent.{docIdCol} = {ctx.PgsqlRecord}.{fkCol}");
            using (writer.Indent())
            {
                writer.AppendLine($"AND {ctx.PgsqlRecord}.{fkCol} IS NOT NULL");
            }
        }
        else
        {
            writer.AppendLine($"SELECT tuples.{source}, {ctx.MssqlAlias}.{idCol}");
            writer.AppendLine($"FROM {ctx.MssqlPseudoTable} {ctx.MssqlAlias}");
            using (writer.Indent())
            {
                writer.AppendLine($"INNER JOIN {parentTable} AS parent");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{docIdCol} = {ctx.MssqlAlias}.{fkCol}");
                }
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON parent.{parentIdCol} = tuples.{target}");
                }
            }
            writer.AppendLine($"WHERE {ctx.MssqlAlias}.{fkCol} IS NOT NULL");
        }
    }

    private static void EmitDescendantsSubquery(
        SqlWriter writer,
        ISqlDialect dialect,
        AuthEdOrgEntity entity,
        TriggerContext ctx
    )
    {
        var (authTable, source, target, idCol) = GetQuotedNames(dialect, entity);

        if (dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"SELECT tuples.{target}");
            writer.AppendLine($"FROM {authTable} AS tuples");
            writer.AppendLine($"WHERE tuples.{source} = {ctx.PgsqlRecord}.{idCol}");
        }
        else
        {
            writer.AppendLine($"SELECT {ctx.MssqlAlias}.{idCol}, tuples.{target}");
            writer.AppendLine($"FROM {ctx.MssqlPseudoTable} {ctx.MssqlAlias}");
            using (writer.Indent())
            {
                writer.AppendLine($"INNER JOIN {authTable} AS tuples");
                using (writer.Indent())
                {
                    writer.AppendLine($"ON {ctx.MssqlAlias}.{idCol} = tuples.{source}");
                }
            }
        }
    }

    // ── Quoting helpers ─────────────────────────────────────────────────

    private static QuotedNames GetQuotedNames(ISqlDialect dialect, AuthEdOrgEntity entity) =>
        new(
            Quote(dialect, _authTable),
            Quote(dialect, _sourceCol),
            Quote(dialect, _targetCol),
            Quote(dialect, entity.IdentityColumn)
        );

    private static string Quote(ISqlDialect dialect, DbTableName table) => dialect.QualifyTable(table);

    private static string Quote(ISqlDialect dialect, DbColumnName column) =>
        dialect.QuoteIdentifier(column.Value);
}
