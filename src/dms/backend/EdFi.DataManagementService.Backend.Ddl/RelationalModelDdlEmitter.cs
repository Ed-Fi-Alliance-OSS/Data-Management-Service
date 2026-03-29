// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits dialect-specific DDL (schemas, tables, indexes, views, and triggers) from a derived relational model set.
/// </summary>
public sealed class RelationalModelDdlEmitter(ISqlDialect dialect)
{
    private readonly ISqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

    // Frequently-used column names, allocated once to avoid repetitive allocations.
    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");
    private static readonly DbColumnName ContentVersionColumn = new("ContentVersion");
    private static readonly DbColumnName ContentLastModifiedAtColumn = new("ContentLastModifiedAt");
    private static readonly DbColumnName IdentityVersionColumn = new("IdentityVersion");
    private static readonly DbColumnName IdentityLastModifiedAtColumn = new("IdentityLastModifiedAt");
    private static readonly DbColumnName ReferentialIdColumn = new("ReferentialId");
    private static readonly DbColumnName ResourceKeyIdColumn = new("ResourceKeyId");
    private static readonly DbColumnName DiscriminatorColumn = new("Discriminator");

    /// <summary>
    /// Builds a SQL script that creates all schemas, tables, indexes, views, and triggers in the model set.
    /// </summary>
    /// <param name="modelSet">The derived relational model set to emit.</param>
    /// <returns>The emitted DDL script.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the model set dialect does not match the emitter dialect rules.
    /// </exception>
    /// <remarks>
    /// For SQL Server (MSSQL), the output contains <c>GO</c> batch separators required
    /// for <c>CREATE OR ALTER</c> statements. These are processed by sqlcmd/SSMS but
    /// are not valid T-SQL. ADO.NET consumers must split on <c>GO</c> lines and execute
    /// each batch separately.
    /// </remarks>
    public string Emit(DerivedRelationalModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        if (modelSet.Dialect != _dialect.Rules.Dialect)
        {
            throw new InvalidOperationException(
                $"Dialect mismatch: model={modelSet.Dialect}, rules={_dialect.Rules.Dialect}."
            );
        }

        var writer = new SqlWriter(_dialect);

        // Apply canonical ordering within each phase so output is byte-for-byte stable
        // regardless of the order in which elements appear in the model set.
        // All comparisons use StringComparer.Ordinal (culture-invariant, case-sensitive).
        // NOTE: These sort keys intentionally duplicate the ordering applied in
        // RelationalModelSetBuilderContext.BuildResult() as a defense-in-depth measure.
        // If sort keys diverge between layers, consider centralizing sort-key definitions.
        var schemas = modelSet
            .ProjectSchemasInEndpointOrder.OrderBy(s => s.PhysicalSchema.Value, StringComparer.Ordinal)
            .ThenBy(s => s.ProjectEndpointName, StringComparer.Ordinal)
            .ToList();

        var concreteResources = modelSet
            .ConcreteResourcesInNameOrder.OrderBy(
                r => r.ResourceKey.Resource.ProjectName,
                StringComparer.Ordinal
            )
            .ThenBy(r => r.ResourceKey.Resource.ResourceName, StringComparer.Ordinal)
            .ToList();

        var abstractIdentityTables = modelSet
            .AbstractIdentityTablesInNameOrder.OrderBy(
                t => t.AbstractResourceKey.Resource.ProjectName,
                StringComparer.Ordinal
            )
            .ThenBy(t => t.AbstractResourceKey.Resource.ResourceName, StringComparer.Ordinal)
            .ToList();

        var abstractUnionViews = modelSet
            .AbstractUnionViewsInNameOrder.OrderBy(v => v.ViewName.Schema.Value, StringComparer.Ordinal)
            .ThenBy(v => v.ViewName.Name, StringComparer.Ordinal)
            .ToList();

        var indexes = modelSet
            .IndexesInCreateOrder.OrderBy(i => i.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(i => i.Table.Name, StringComparer.Ordinal)
            .ThenBy(i => i.Name.Value, StringComparer.Ordinal)
            .ToList();

        var triggers = modelSet
            .TriggersInCreateOrder.OrderBy(t => t.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(t => t.Table.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Name.Value, StringComparer.Ordinal)
            .ToList();

        var authHierarchy = modelSet.AuthEdOrgHierarchy;

        // Phase 1: Schemas (includes auth schema when hierarchy is present)
        var additionalSchemas = authHierarchy is { EntitiesInNameOrder.Count: > 0 }
            ? [AuthNames.AuthSchema]
            : Array.Empty<DbSchemaName>();
        EmitSchemas(writer, schemas, additionalSchemas);

        // Phase 2: Tables (PK/UK/CHECK only, no cross-table FKs; includes auth table)
        EmitTables(writer, concreteResources);
        EmitAuthTable(writer, authHierarchy);

        // Phase 3: Abstract Identity Tables (must precede FKs that reference them)
        EmitAbstractIdentityTables(writer, abstractIdentityTables);

        // Phase 4: Foreign Keys (separate ALTER TABLE statements)
        EmitForeignKeys(writer, concreteResources, abstractIdentityTables);

        // Phase 5: Indexes
        EmitIndexes(writer, indexes);

        // Phase 6: Views (must precede Triggers per design)
        EmitAbstractUnionViews(writer, abstractUnionViews);
        EmitPeopleAuthViews(writer, authHierarchy);

        var tableModelsByTableName = BuildTableModelLookup(concreteResources, abstractIdentityTables);

        // Phase 7: Triggers (includes auth hierarchy triggers)
        EmitTriggers(writer, triggers, tableModelsByTableName);

        return writer.ToString();
    }

    /// <summary>
    /// Emits <c>CREATE SCHEMA IF NOT EXISTS</c> statements for each project schema
    /// and any additional schemas (e.g., <c>auth</c>).
    /// </summary>
    private void EmitSchemas(
        SqlWriter writer,
        IReadOnlyList<ProjectSchemaInfo> schemas,
        IReadOnlyList<DbSchemaName> additionalSchemas
    )
    {
        foreach (var schema in schemas)
        {
            writer.AppendLine(_dialect.CreateSchemaIfNotExists(schema.PhysicalSchema));
        }

        foreach (var schema in additionalSchemas)
        {
            writer.AppendLine(_dialect.CreateSchemaIfNotExists(schema));
        }

        if (schemas.Count > 0 || additionalSchemas.Count > 0)
        {
            writer.AppendLine();
        }
    }

    /// <summary>
    /// Emits <c>CREATE TABLE IF NOT EXISTS</c> statements for each table in each concrete resource model.
    /// </summary>
    private void EmitTables(SqlWriter writer, IReadOnlyList<ConcreteResourceModel> resources)
    {
        foreach (var resource in resources)
        {
            // Descriptor resources use the shared dms.Descriptor table (emitted by core DDL).
            if (resource.StorageKind == ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                EmitCreateTable(writer, table);
            }
        }
    }

    /// <summary>
    /// Emits the <c>auth.EducationOrganizationIdToEducationOrganizationId</c> table when the auth hierarchy is present.
    /// </summary>
    private void EmitAuthTable(SqlWriter writer, AuthEdOrgHierarchy? authHierarchy)
    {
        if (authHierarchy is not { EntitiesInNameOrder.Count: > 0 })
        {
            return;
        }

        var authTable = AuthNames.EdOrgIdToEdOrgId;
        var sourceCol = AuthNames.SourceEdOrgId;
        var targetCol = AuthNames.TargetEdOrgId;

        writer.AppendLine(_dialect.CreateTableHeader(authTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine($"{_dialect.RenderColumnDefinition(sourceCol, "bigint", false)},");
            writer.AppendLine($"{_dialect.RenderColumnDefinition(targetCol, "bigint", false)},");
            writer.AppendLine(
                _dialect.RenderNamedPrimaryKeyClause(
                    "PK_EducationOrganizationIdToEducationOrganizationId",
                    [sourceCol, targetCol]
                )
            );
        }
        writer.AppendLine(");");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits a <c>CREATE TABLE IF NOT EXISTS</c> statement including columns, key, and table constraints.
    /// </summary>
    private void EmitCreateTable(SqlWriter writer, DbTableModel table)
    {
        writer.AppendLine(_dialect.CreateTableHeader(table.Table));
        writer.AppendLine("(");

        var definitions = new List<string>();

        foreach (var column in table.Columns)
        {
            definitions.Add(RenderColumnDefinition(table, column));
        }

        if (table.Key.Columns.Count > 0)
        {
            definitions.Add(
                $"CONSTRAINT {Quote(ResolvePrimaryKeyConstraintName(table))} PRIMARY KEY ({FormatColumnList(table.Key.Columns)})"
            );
        }

        foreach (var constraint in table.Constraints)
        {
            var formatted = FormatConstraint(constraint);
            if (formatted is not null) // Skip null (FK) constraints
            {
                definitions.Add(formatted);
            }
        }

        using (writer.Indent())
        {
            for (var i = 0; i < definitions.Count; i++)
            {
                writer.Append(definitions[i]);

                if (i < definitions.Count - 1)
                {
                    writer.AppendLine(",");
                }
                else
                {
                    writer.AppendLine();
                }
            }
        }

        writer.AppendLine(");");
        writer.AppendLine();
    }

    /// <summary>
    /// Renders a column definition based on its storage type.
    /// Stored columns emit a normal column definition; UnifiedAlias columns emit a computed column.
    /// </summary>
    private string RenderColumnDefinition(DbTableModel table, DbColumnModel column)
    {
        var type = ResolveColumnType(column);

        if (column.Storage is ColumnStorage.UnifiedAlias alias)
        {
            return _dialect.RenderComputedColumnDefinition(
                column.ColumnName,
                type,
                alias.CanonicalColumn,
                alias.PresenceColumn
            );
        }

        var defaultExpression = ResolveDefaultExpression(table, column);

        return _dialect.RenderColumnDefinition(column.ColumnName, type, column.IsNullable, defaultExpression);
    }

    private string? ResolveDefaultExpression(DbTableModel table, DbColumnModel column)
    {
        return UsesCollectionItemSequenceDefault(table, column)
            ? _dialect.RenderSequenceDefaultExpression(
                DmsTableNames.DmsSchema,
                DmsTableNames.CollectionItemIdSequence
            )
            : null;
    }

    private static bool UsesCollectionItemSequenceDefault(DbTableModel table, DbColumnModel column)
    {
        return column.ColumnName.Equals(RelationalNameConventions.CollectionItemIdColumnName)
            && table.IdentityMetadata.TableKind is DbTableKind.Collection or DbTableKind.ExtensionCollection;
    }

    /// <summary>
    /// Emits idempotent <c>ALTER TABLE ADD CONSTRAINT</c> statements for all foreign keys.
    /// </summary>
    private void EmitForeignKeys(
        SqlWriter writer,
        IReadOnlyList<ConcreteResourceModel> resources,
        IReadOnlyList<AbstractIdentityTableInfo> abstractIdentityTables
    )
    {
        // Emit FKs for concrete resource tables (skip descriptors — they use shared dms.Descriptor)
        foreach (var resource in resources)
        {
            if (resource.StorageKind == ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                EmitTableForeignKeys(writer, table);
            }
        }

        // Emit FKs for abstract identity tables (e.g., DocumentId -> dms.Document)
        foreach (var tableInfo in abstractIdentityTables)
        {
            EmitTableForeignKeys(writer, tableInfo.TableModel);
        }
    }

    /// <summary>
    /// Emits foreign key constraints for a single table.
    /// </summary>
    private void EmitTableForeignKeys(SqlWriter writer, DbTableModel table)
    {
        // OrderBy is redundant with RelationalModelOrdering.CanonicalizeTable() constraint
        // ordering but kept as defense-in-depth for the emitter's byte-for-byte guarantee.
        foreach (
            var fk in table
                .Constraints.OfType<TableConstraint.ForeignKey>()
                .OrderBy(fk => fk.Name, StringComparer.Ordinal)
        )
        {
            writer.AppendLine(
                _dialect.AddForeignKeyConstraint(
                    table.Table,
                    fk.Name,
                    fk.Columns,
                    fk.TargetTable,
                    fk.TargetColumns,
                    fk.OnDelete,
                    fk.OnUpdate
                )
            );
            writer.AppendLine();
        }
    }

    /// <summary>
    /// Emits <c>CREATE TABLE IF NOT EXISTS</c> statements for abstract identity tables.
    /// </summary>
    private void EmitAbstractIdentityTables(SqlWriter writer, IReadOnlyList<AbstractIdentityTableInfo> tables)
    {
        foreach (var tableInfo in tables)
        {
            // Reuse existing EmitCreateTable - it already handles all table types
            EmitCreateTable(writer, tableInfo.TableModel);
        }
    }

    /// <summary>
    /// Emits <c>CREATE INDEX IF NOT EXISTS</c> statements for each index in create-order.
    /// Only FK-support and explicit indexes are emitted, since PK and UK indexes are
    /// already created by their respective constraint definitions.
    /// </summary>
    private void EmitIndexes(SqlWriter writer, IReadOnlyList<DbIndexInfo> indexes)
    {
        foreach (var index in indexes)
        {
            // Skip PK and UK indexes - they are already created by constraint definitions
            if (index.Kind is DbIndexKind.PrimaryKey or DbIndexKind.UniqueConstraint)
            {
                continue;
            }

            writer.AppendLine(
                _dialect.CreateIndexIfNotExists(
                    index.Table,
                    index.Name.Value,
                    index.KeyColumns,
                    index.IsUnique,
                    index.IncludeColumns
                )
            );
            writer.AppendLine();
        }
    }

    /// <summary>
    /// Emits <c>CREATE TRIGGER</c> statements for each trigger in create-order.
    /// </summary>
    private void EmitTriggers(
        SqlWriter writer,
        IReadOnlyList<DbTriggerInfo> triggers,
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByTableName
    )
    {
        // MSSQL requires a batch boundary before the first CREATE OR ALTER TRIGGER.
        // Each trigger emits its own trailing GO, so only the leading GO is needed here.
        if (_dialect.Rules.Dialect == SqlDialect.Mssql && triggers.Count > 0)
        {
            writer.AppendLine("GO");
        }

        foreach (var trigger in triggers)
        {
            // Auth hierarchy triggers use AFTER timing with different scaffolding
            // (RETURN NULL for PG, single-event per trigger).
            if (trigger.Parameters is TriggerKindParameters.AuthHierarchyMaintenance auth)
            {
                if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
                {
                    EmitPgsqlAuthTrigger(writer, trigger, auth);
                }
                else
                {
                    EmitMssqlAuthTrigger(writer, trigger, auth);
                }
                continue;
            }

            // Dispatch by dialect enum rather than pattern abstraction for trigger generation.
            // Adding a new dialect requires updating this site and: EmitDocumentStampingBody,
            // EmitReferentialIdentityBody, EmitAbstractIdentityBody, FormatNullOrTrueCheck,
            // EmitStringLiteralWithCast.
            if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
            {
                EmitPgsqlTrigger(writer, trigger, tableModelsByTableName);
            }
            else
            {
                EmitMssqlTrigger(writer, trigger, tableModelsByTableName);
            }
        }
    }

    /// <summary>
    /// Emits a PostgreSQL trigger (function + trigger).
    /// Uses DROP TRIGGER IF EXISTS + CREATE TRIGGER per design (not CREATE OR REPLACE TRIGGER).
    /// </summary>
    private void EmitPgsqlTrigger(
        SqlWriter writer,
        DbTriggerInfo trigger,
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByTableName
    )
    {
        var funcName = _dialect.Rules.ShortenIdentifier($"TF_{trigger.Name.Value}");
        var schema = trigger.Table.Schema;

        // Function: CREATE OR REPLACE is supported and idempotent
        writer.Append("CREATE OR REPLACE FUNCTION ");
        writer.Append(Quote(schema));
        writer.Append(".");
        writer.Append(Quote(funcName));
        writer.AppendLine("()");
        writer.AppendLine("RETURNS TRIGGER AS $func$");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            // DMS-1002 accepts INSERT/UPDATE representation stamping as the main behavior.
            // We still emit DELETE handling here as future-facing prevision for later
            // delete-tracking work. On DELETE there is no NEW row, so we bump
            // ContentVersion via OLD, return OLD, and skip the normal body.
            if (trigger.Parameters is TriggerKindParameters.DocumentStamping)
            {
                if (trigger.KeyColumns.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"DocumentStamping trigger '{trigger.Name.Value}' requires exactly one key column in the PgSQL path, but has {trigger.KeyColumns.Count}."
                    );
                }

                var deleteKeyColumn = trigger.KeyColumns[0];
                writer.AppendLine("IF TG_OP = 'DELETE' THEN");
                using (writer.Indent())
                {
                    writer.Append("UPDATE ");
                    writer.AppendLine(Quote(DmsTableNames.Document));
                    writer.Append("SET ");
                    writer.Append(Quote(ContentVersionColumn));
                    writer.Append(" = nextval('");
                    writer.Append(FormatSequenceName());
                    writer.Append("'), ");
                    writer.Append(Quote(ContentLastModifiedAtColumn));
                    writer.AppendLine(" = now()");
                    writer.Append("WHERE ");
                    writer.Append(Quote(DocumentIdColumn));
                    writer.Append(" = OLD.");
                    writer.Append(Quote(deleteKeyColumn));
                    writer.AppendLine(";");
                    writer.AppendLine("RETURN OLD;");
                }
                writer.AppendLine("END IF;");
            }

            EmitTriggerBody(writer, trigger, tableModelsByTableName);
            writer.AppendLine("RETURN NEW;");
        }
        writer.AppendLine("END;");
        writer.AppendLine("$func$ LANGUAGE plpgsql;");
        writer.AppendLine();

        // Trigger: Use DROP + CREATE pattern per design (ddl-generation.md:260-262)
        // PostgreSQL's CREATE OR REPLACE TRIGGER is not available in all versions,
        // so we use the idempotent DROP IF EXISTS + CREATE pattern.
        writer.AppendLine(_dialect.DropTriggerIfExists(trigger.Table, trigger.Name.Value));
        writer.Append("CREATE TRIGGER ");
        writer.AppendLine(Quote(trigger.Name));

        // Keep DELETE in the emitted trigger shape as future-facing prevision for later
        // delete-tracking work, even though DMS-1002 acceptance is centered on
        // INSERT/UPDATE representation stamping.
        var pgsqlTriggerEvent =
            trigger.Parameters is TriggerKindParameters.DocumentStamping
                ? "BEFORE INSERT OR UPDATE OR DELETE ON "
                : "BEFORE INSERT OR UPDATE ON ";
        writer.Append(pgsqlTriggerEvent);
        writer.AppendLine(Quote(trigger.Table));
        writer.AppendLine("FOR EACH ROW");
        writer.Append("EXECUTE FUNCTION ");
        writer.Append(Quote(schema));
        writer.Append(".");
        writer.Append(Quote(funcName));
        writer.AppendLine("();");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits a SQL Server trigger.
    /// </summary>
    private void EmitMssqlTrigger(
        SqlWriter writer,
        DbTriggerInfo trigger,
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByTableName
    )
    {
        writer.Append("CREATE OR ALTER TRIGGER ");
        writer.Append(Quote(trigger.Table.Schema));
        writer.Append(".");
        writer.AppendLine(Quote(trigger.Name));
        writer.Append("ON ");
        writer.AppendLine(Quote(trigger.Table));
        var mssqlTriggerEvent = trigger.Parameters switch
        {
            TriggerKindParameters.IdentityPropagationFallback => "INSTEAD OF UPDATE",
            TriggerKindParameters.DocumentStamping => "AFTER INSERT, UPDATE, DELETE",
            _ => "AFTER INSERT, UPDATE",
        };
        writer.AppendLine(mssqlTriggerEvent);
        writer.AppendLine("AS");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("SET NOCOUNT ON;");
            EmitTriggerBody(writer, trigger, tableModelsByTableName);
        }
        writer.AppendLine("END;");
        // Close the batch so that the next trigger (or any subsequent DDL/DML
        // concatenated after the relational model DDL) starts in a fresh batch.
        writer.AppendLine("GO");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits a PostgreSQL auth hierarchy trigger (AFTER, row-level, RETURN NULL).
    /// </summary>
    private void EmitPgsqlAuthTrigger(
        SqlWriter writer,
        DbTriggerInfo trigger,
        TriggerKindParameters.AuthHierarchyMaintenance auth
    )
    {
        var schema = trigger.Table.Schema;
        var funcName = _dialect.Rules.ShortenIdentifier($"TF_{trigger.Name.Value}");
        var triggerEvent = auth.TriggerEvent switch
        {
            AuthHierarchyTriggerEvent.Insert => "INSERT",
            AuthHierarchyTriggerEvent.Update => "UPDATE",
            AuthHierarchyTriggerEvent.Delete => "DELETE",
            _ => throw new ArgumentOutOfRangeException(nameof(auth.TriggerEvent)),
        };

        // Trigger function
        writer.Append("CREATE OR REPLACE FUNCTION ");
        writer.Append(Quote(schema));
        writer.Append(".");
        writer.Append(Quote(funcName));
        writer.AppendLine("()");
        writer.AppendLine("RETURNS TRIGGER AS $$");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            AuthTriggerBodyEmitter.EmitBody(writer, _dialect, auth.Entity, auth.TriggerEvent);
            writer.AppendLine("RETURN NULL;");
        }
        writer.AppendLine("END;");
        writer.AppendLine("$$ LANGUAGE plpgsql;");
        writer.AppendLine();

        // Drop + Create trigger
        writer.AppendLine(_dialect.DropTriggerIfExists(trigger.Table, trigger.Name.Value));
        writer.Append("CREATE TRIGGER ");
        writer.AppendLine(Quote(trigger.Name));
        using (writer.Indent())
        {
            writer.Append($"AFTER {triggerEvent} ON ");
            writer.AppendLine(Quote(trigger.Table));
            writer.AppendLine("FOR EACH ROW");
            writer.Append("EXECUTE FUNCTION ");
            writer.Append(Quote(schema));
            writer.Append(".");
            writer.Append(Quote(funcName));
            writer.AppendLine("();");
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits a SQL Server auth hierarchy trigger (AFTER, single-event).
    /// </summary>
    private void EmitMssqlAuthTrigger(
        SqlWriter writer,
        DbTriggerInfo trigger,
        TriggerKindParameters.AuthHierarchyMaintenance auth
    )
    {
        var schema = trigger.Table.Schema;
        var triggerEvent = auth.TriggerEvent switch
        {
            AuthHierarchyTriggerEvent.Insert => "INSERT",
            AuthHierarchyTriggerEvent.Update => "UPDATE",
            AuthHierarchyTriggerEvent.Delete => "DELETE",
            _ => throw new ArgumentOutOfRangeException(nameof(auth.TriggerEvent)),
        };

        writer.Append("CREATE OR ALTER TRIGGER ");
        writer.Append(Quote(schema));
        writer.Append(".");
        writer.AppendLine(Quote(trigger.Name));
        writer.Append("ON ");
        writer.AppendLine(Quote(trigger.Table));
        writer.AppendLine($"AFTER {triggerEvent}");
        writer.AppendLine("AS");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("SET NOCOUNT ON;");
            AuthTriggerBodyEmitter.EmitBody(writer, _dialect, auth.Entity, auth.TriggerEvent);
        }
        writer.AppendLine("END;");
        // Close the batch so that the next trigger (or any subsequent DDL/DML
        // concatenated after the relational model DDL) starts in a fresh batch.
        writer.AppendLine("GO");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the trigger body logic based on trigger kind.
    /// </summary>
    private void EmitTriggerBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByTableName
    )
    {
        switch (trigger.Parameters)
        {
            case TriggerKindParameters.DocumentStamping:
                if (!tableModelsByTableName.TryGetValue(trigger.Table, out var tableModel))
                {
                    throw new InvalidOperationException(
                        $"DocumentStamping trigger '{trigger.Name.Value}' requires a table model for '{trigger.Table.Schema.Value}.{trigger.Table.Name}', but none was found."
                    );
                }

                EmitDocumentStampingBody(writer, trigger, tableModel);
                break;
            case TriggerKindParameters.ReferentialIdentityMaintenance refId:
                EmitReferentialIdentityBody(writer, trigger, refId);
                break;
            case TriggerKindParameters.AbstractIdentityMaintenance abstractId:
                EmitAbstractIdentityBody(writer, trigger, abstractId);
                break;
            case TriggerKindParameters.IdentityPropagationFallback propagation:
                if (!tableModelsByTableName.TryGetValue(trigger.Table, out var propagationTableModel))
                {
                    throw new InvalidOperationException(
                        $"IdentityPropagationFallback trigger '{trigger.Name.Value}' requires a table model for '{trigger.Table.Schema.Value}.{trigger.Table.Name}', but none was found."
                    );
                }

                EmitIdentityPropagationBody(writer, trigger, propagationTableModel, propagation);
                break;
            case TriggerKindParameters.AuthHierarchyMaintenance:
                // Auth triggers are handled by dedicated scaffolding methods
                // (EmitPgsqlAuthTrigger / EmitMssqlAuthTrigger), not this switch.
                throw new InvalidOperationException(
                    $"Auth hierarchy trigger '{trigger.Name.Value}' should not reach EmitTriggerBody."
                );
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger.Parameters));
        }
    }

    /// <summary>
    /// Emits document stamping trigger body for DMS-1002 INSERT/UPDATE representation
    /// stamping, with retained DELETE prevision left in place for later delete-tracking work.
    /// Root tables with identity projection columns also stamp <c>IdentityVersion</c>.
    /// </summary>
    private void EmitDocumentStampingBody(SqlWriter writer, DbTriggerInfo trigger, DbTableModel tableModel)
    {
        if (trigger.KeyColumns.Count != 1)
        {
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{trigger.Name.Value}' requires exactly one key column, but has {trigger.KeyColumns.Count}."
            );
        }

        var documentTable = Quote(DmsTableNames.Document);
        var sequenceName = FormatSequenceName();
        var keyColumn = trigger.KeyColumns[0];

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlDocumentStampingBody(
                writer,
                trigger,
                tableModel,
                documentTable,
                sequenceName,
                keyColumn
            );
        }
        else
        {
            EmitMssqlDocumentStampingBody(
                writer,
                trigger,
                tableModel,
                documentTable,
                sequenceName,
                keyColumn
            );
        }
    }

    private void EmitPgsqlDocumentStampingBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn
    )
    {
        var storedColumns = GetStoredColumnsForDocumentStamping(tableModel, trigger.Name.Value);
        var isRootDocumentStampingTrigger = tableModel.IdentityMetadata.TableKind == DbTableKind.Root;

        // Skip successful no-op UPDATEs that do not change any stored row values.
        writer.Append("IF TG_OP = 'UPDATE' AND NOT (");
        EmitPgsqlValueDiffDisjunction(writer, storedColumns);
        writer.AppendLine(") THEN");
        using (writer.Indent())
        {
            writer.AppendLine("RETURN NEW;");
        }
        writer.AppendLine("END IF;");

        // Root-document INSERTs reuse dms.Document defaults so we do not burn an
        // extra sequence value and journal row before the resource row exists.
        if (isRootDocumentStampingTrigger)
        {
            writer.AppendLine("IF TG_OP = 'UPDATE' THEN");
            using (writer.Indent())
            {
                EmitPgsqlDocumentContentStampUpdate(writer, documentTable, sequenceName, keyColumn);
            }
            writer.AppendLine("END IF;");
        }
        else
        {
            EmitPgsqlDocumentContentStampUpdate(writer, documentTable, sequenceName, keyColumn);
        }

        // IdentityVersion stamp for root tables with identity projection columns
        if (trigger.IdentityProjectionColumns.Count > 0)
        {
            // PostgreSQL: IS DISTINCT FROM provides null-safe inequality comparison.
            // (NULL IS DISTINCT FROM NULL) → false, (NULL IS DISTINCT FROM value) → true.
            // Equivalent to MSSQL's EmitMssqlNullSafeNotEqual pattern which expands to:
            // (a <> b OR (a IS NULL AND b IS NOT NULL) OR (a IS NOT NULL AND b IS NULL))
            writer.Append("IF TG_OP = 'UPDATE' AND (");
            EmitPgsqlValueDiffDisjunction(writer, trigger.IdentityProjectionColumns);
            writer.AppendLine(") THEN");

            using (writer.Indent())
            {
                writer.Append("UPDATE ");
                writer.AppendLine(documentTable);
                writer.Append("SET ");
                writer.Append(Quote(IdentityVersionColumn));
                writer.Append(" = nextval('");
                writer.Append(sequenceName);
                writer.Append("'), ");
                writer.Append(Quote(IdentityLastModifiedAtColumn));
                writer.AppendLine(" = now()");
                writer.Append("WHERE ");
                writer.Append(Quote(DocumentIdColumn));
                writer.Append(" = NEW.");
                writer.Append(Quote(keyColumn));
                writer.AppendLine(";");
            }

            writer.AppendLine("END IF;");
        }
    }

    private void EmitPgsqlDocumentContentStampUpdate(
        SqlWriter writer,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn
    )
    {
        writer.Append("UPDATE ");
        writer.AppendLine(documentTable);
        writer.Append("SET ");
        writer.Append(Quote(ContentVersionColumn));
        writer.Append(" = nextval('");
        writer.Append(sequenceName);
        writer.Append("'), ");
        writer.Append(Quote(ContentLastModifiedAtColumn));
        writer.AppendLine(" = now()");
        writer.Append("WHERE ");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(" = NEW.");
        writer.Append(Quote(keyColumn));
        writer.AppendLine(";");
    }

    private void EmitMssqlDocumentStampingBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn
    )
    {
        var tableKeyColumns = GetKeyColumnsForDocumentStamping(tableModel, trigger.Name.Value);
        var storedColumns = GetStoredColumnsForDocumentStamping(tableModel, trigger.Name.Value);
        var quotedKeyColumn = Quote(keyColumn);
        var quotedProbeKeyColumn = Quote(tableKeyColumns[0]);

        // ContentVersion stamp - compute the set of affected documents from inserted/deleted
        // rows that are inserts, deletes, or actual value changes. No-op UPDATEs are excluded.
        writer.AppendLine(";WITH affectedDocs AS (");
        using (writer.Indent())
        {
            writer.Append("SELECT i.");
            writer.AppendLine(quotedKeyColumn);
            writer.AppendLine("FROM inserted i");
            writer.Append("LEFT JOIN deleted del ON ");
            EmitMssqlJoinConjunction(writer, "del", "i", tableKeyColumns);
            writer.AppendLine();
            writer.Append("WHERE del.");
            writer.Append(quotedProbeKeyColumn);
            writer.Append(" IS NULL OR ");
            EmitMssqlColumnValueDiffDisjunction(writer, "i", "del", storedColumns);
            writer.AppendLine();
            writer.AppendLine("UNION");
            writer.Append("SELECT del.");
            writer.AppendLine(quotedKeyColumn);
            writer.AppendLine("FROM deleted del");
            writer.Append("LEFT JOIN inserted i ON ");
            EmitMssqlJoinConjunction(writer, "i", "del", tableKeyColumns);
            writer.AppendLine();
            writer.Append("WHERE i.");
            writer.Append(quotedProbeKeyColumn);
            writer.Append(" IS NULL OR ");
            EmitMssqlColumnValueDiffDisjunction(writer, "i", "del", storedColumns);
            writer.AppendLine();
        }
        writer.AppendLine(")");

        writer.AppendLine("UPDATE d");
        writer.Append("SET d.");
        writer.Append(Quote(ContentVersionColumn));
        writer.Append(" = NEXT VALUE FOR ");
        writer.Append(sequenceName);
        writer.Append(", d.");
        writer.Append(Quote(ContentLastModifiedAtColumn));
        writer.AppendLine(" = sysutcdatetime()");
        writer.Append("FROM ");
        writer.Append(documentTable);
        writer.AppendLine(" d");
        writer.Append("INNER JOIN affectedDocs a ON d.");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(" = a.");
        writer.Append(quotedKeyColumn);
        writer.AppendLine(";");

        // IdentityVersion stamp for root tables with identity projection columns
        if (trigger.IdentityProjectionColumns.Count > 0)
        {
            // Performance pre-filter: UPDATE(col) returns true if the column appeared in the SET clause,
            // regardless of whether the value actually changed. The WHERE clause below (using null-safe
            // inequality) is the authoritative value-change check that filters to only actually changed rows.
            writer.Append("IF EXISTS (SELECT 1 FROM deleted) AND (");
            EmitMssqlUpdateColumnDisjunction(writer, trigger.IdentityProjectionColumns);
            writer.AppendLine(")");

            writer.AppendLine("BEGIN");

            using (writer.Indent())
            {
                writer.AppendLine("UPDATE d");
                writer.Append("SET d.");
                writer.Append(Quote(IdentityVersionColumn));
                writer.Append(" = NEXT VALUE FOR ");
                writer.Append(sequenceName);
                writer.Append(", d.");
                writer.Append(Quote(IdentityLastModifiedAtColumn));
                writer.AppendLine(" = sysutcdatetime()");
                writer.Append("FROM ");
                writer.Append(documentTable);
                writer.AppendLine(" d");
                writer.Append("INNER JOIN inserted i ON d.");
                writer.Append(Quote(DocumentIdColumn));
                writer.Append(" = i.");
                writer.AppendLine(Quote(keyColumn));
                writer.Append("INNER JOIN deleted del ON del.");
                writer.Append(Quote(keyColumn));
                writer.Append(" = i.");
                writer.AppendLine(Quote(keyColumn));
                writer.Append("WHERE ");
                for (int i = 0; i < trigger.IdentityProjectionColumns.Count; i++)
                {
                    if (i > 0)
                    {
                        writer.Append(" OR ");
                    }
                    var col = Quote(trigger.IdentityProjectionColumns[i]);
                    EmitMssqlNullSafeNotEqual(writer, "i", col, "del", col);
                }
                writer.AppendLine(";");
            }

            writer.AppendLine("END");
        }
    }

    /// <summary>
    /// Emits referential identity maintenance trigger body that maintains
    /// <c>dms.ReferentialIdentity</c> rows via UUIDv5 computation.
    /// </summary>
    private void EmitReferentialIdentityBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        TriggerKindParameters.ReferentialIdentityMaintenance refId
    )
    {
        // Consolidated guard: both dialect paths require at least one identity element.
        if (refId.IdentityElements.Count == 0)
        {
            throw new InvalidOperationException(
                $"ReferentialIdentityMaintenance trigger requires at least one identity element "
                    + $"for resource '{refId.ResourceName}'. This indicates a bug in the model derivation phase."
            );
        }

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlReferentialIdentityBody(writer, trigger.IdentityProjectionColumns, refId);
        }
        else
        {
            EmitMssqlReferentialIdentityBody(writer, trigger.IdentityProjectionColumns, refId);
        }
    }

    private void EmitPgsqlReferentialIdentityBody(
        SqlWriter writer,
        IReadOnlyList<DbColumnName> identityProjectionColumns,
        TriggerKindParameters.ReferentialIdentityMaintenance refId
    )
    {
        // Guard: skip recomputation on no-op UPDATEs where identity columns didn't change
        writer.Append("IF TG_OP = 'INSERT' OR (");
        EmitPgsqlValueDiffDisjunction(writer, identityProjectionColumns);
        writer.AppendLine(") THEN");

        using (writer.Indent())
        {
            var refIdTable = Quote(DmsTableNames.ReferentialIdentity);

            // Primary referential identity
            EmitPgsqlReferentialIdentityBlock(
                writer,
                refIdTable,
                refId.ResourceKeyId,
                refId.ProjectName,
                refId.ResourceName,
                refId.IdentityElements
            );

            // Superclass alias
            if (refId.SuperclassAlias is { } alias)
            {
                EmitPgsqlReferentialIdentityBlock(
                    writer,
                    refIdTable,
                    alias.ResourceKeyId,
                    alias.ProjectName,
                    alias.ResourceName,
                    alias.IdentityElements
                );
            }
        }

        writer.AppendLine("END IF;");
    }

    private void EmitPgsqlReferentialIdentityBlock(
        SqlWriter writer,
        string refIdTable,
        short resourceKeyId,
        string projectName,
        string resourceName,
        IReadOnlyList<IdentityElementMapping> identityElements
    )
    {
        var uuidv5Func = FormatUuidv5FunctionName();

        // DELETE existing row
        writer.Append("DELETE FROM ");
        writer.AppendLine(refIdTable);
        writer.Append("WHERE ");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(" = NEW.");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(" AND ");
        writer.Append(Quote(ResourceKeyIdColumn));
        writer.Append(" = ");
        writer.Append(resourceKeyId.ToString(CultureInfo.InvariantCulture));
        writer.AppendLine(";");

        // INSERT new row with UUIDv5
        writer.Append("INSERT INTO ");
        writer.Append(refIdTable);
        writer.Append(" (");
        writer.Append(Quote(ReferentialIdColumn));
        writer.Append(", ");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(", ");
        writer.Append(Quote(ResourceKeyIdColumn));
        writer.AppendLine(")");
        writer.Append("VALUES (");
        writer.Append(uuidv5Func);
        writer.Append("('");
        writer.Append(Uuidv5Namespace);
        // Format intentionally matches ReferentialIdCalculator.ResourceInfoString: {ProjectName}{ResourceName}
        // with no separator — do not add one without updating the calculator.
        writer.Append("'::uuid, '");
        writer.Append(SqlDialectBase.EscapeSingleQuote(projectName));
        writer.Append(SqlDialectBase.EscapeSingleQuote(resourceName));
        writer.Append("' || ");
        EmitPgsqlIdentityHashExpression(writer, identityElements);
        writer.Append("), NEW.");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(", ");
        writer.Append(resourceKeyId.ToString(CultureInfo.InvariantCulture));
        writer.AppendLine(");");
    }

    /// <summary>
    /// Emits the PostgreSQL identity hash expression for UUIDv5 computation.
    /// </summary>
    /// <remarks>
    /// Identity columns are guaranteed NOT NULL because they are resource key parts
    /// (the identity of the resource). The column model derivation ensures these columns
    /// are created with <c>NOT NULL</c> constraints, so COALESCE is not needed here.
    /// </remarks>
    private void EmitPgsqlIdentityHashExpression(
        SqlWriter writer,
        IReadOnlyList<IdentityElementMapping> elements
    )
    {
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" || '#' || ");
            }
            writer.Append("'$");
            writer.Append(SqlDialectBase.EscapeSingleQuote(elements[i].IdentityJsonPath));
            writer.Append("=' || ");
            EmitPgsqlColumnToText(writer, elements[i].Column, elements[i].ScalarType);
        }
    }

    /// <summary>
    /// Emits a type-aware text conversion for an identity column value in PostgreSQL.
    /// Uses <c>::text</c> for most types (already ISO-stable) but explicit <c>to_char()</c>
    /// for <see cref="ScalarKind.DateTime"/> where <c>::text</c> omits the ISO 8601 T separator.
    /// </summary>
    private void EmitPgsqlColumnToText(SqlWriter writer, DbColumnName column, RelationalScalarType scalarType)
    {
        var quoted = Quote(column);
        switch (scalarType.Kind)
        {
            case ScalarKind.DateTime:
                // PG timestamp::text gives 'YYYY-MM-DD HH:MM:SS' (space, no T).
                // Use to_char for ISO 8601 with T separator.
                //
                // No AT TIME ZONE 'UTC' conversion: the PG column type is timestamptz, which
                // stores UTC internally but displays in the session timezone. The trigger fires
                // in the same session as the INSERT/UPDATE, so to_char always reproduces the
                // original literal that was inserted — matching what Core's ReferentialIdCalculator
                // hashes from the raw JSON string. Adding AT TIME ZONE 'UTC' here would break
                // parity because the C# path does not normalize to UTC before hashing.
                // The DMS application must use a consistent session timezone (UTC recommended).
                // See also EmitMssqlColumnToNvarchar (datetime2 is timezone-naive, no issue).
                writer.Append("to_char(NEW.");
                writer.Append(quoted);
                writer.Append(", 'YYYY-MM-DD\"T\"HH24:MI:SS')");
                break;

            default:
                // String, Int32, Int64, Date, Time, Decimal, Boolean — ::text is deterministic.
                writer.Append("NEW.");
                writer.Append(quoted);
                writer.Append("::text");
                break;
        }
    }

    private void EmitMssqlReferentialIdentityBody(
        SqlWriter writer,
        IReadOnlyList<DbColumnName> identityProjectionColumns,
        TriggerKindParameters.ReferentialIdentityMaintenance refId
    )
    {
        var refIdTable = Quote(DmsTableNames.ReferentialIdentity);

        EmitMssqlInsertUpdateDispatch(
            writer,
            identityProjectionColumns,
            isInsert => EmitMssqlReferentialIdentityBlock(writer, refIdTable, refId, isInsert)
        );
    }

    /// <summary>
    /// Emits the DELETE + INSERT block for one referential identity resource key.
    /// When <paramref name="isInsert"/> is true, scopes to all <c>inserted</c> rows;
    /// otherwise scopes to the <c>@changedDocs</c> value-diff workset.
    /// </summary>
    private void EmitMssqlReferentialIdentityBlock(
        SqlWriter writer,
        string refIdTable,
        TriggerKindParameters.ReferentialIdentityMaintenance refId,
        bool isInsert
    )
    {
        // Primary referential identity
        EmitMssqlReferentialIdentityUpsert(
            writer,
            refIdTable,
            refId.ResourceKeyId,
            refId.ProjectName,
            refId.ResourceName,
            refId.IdentityElements,
            isInsert
        );

        // Superclass alias
        if (refId.SuperclassAlias is { } alias)
        {
            EmitMssqlReferentialIdentityUpsert(
                writer,
                refIdTable,
                alias.ResourceKeyId,
                alias.ProjectName,
                alias.ResourceName,
                alias.IdentityElements,
                isInsert
            );
        }
    }

    /// <summary>
    /// Emits a DELETE + INSERT pair for a single resource key's referential identity rows.
    /// When <paramref name="isInsert"/> is true, scopes to all <c>inserted</c> rows;
    /// otherwise scopes to the <c>@changedDocs</c> value-diff workset.
    /// </summary>
    private void EmitMssqlReferentialIdentityUpsert(
        SqlWriter writer,
        string refIdTable,
        short resourceKeyId,
        string projectName,
        string resourceName,
        IReadOnlyList<IdentityElementMapping> identityElements,
        bool isInsert
    )
    {
        var sourceAlias = isInsert ? "inserted" : "@changedDocs";
        var uuidv5Func = FormatUuidv5FunctionName();
        var documentIdCol = Quote(DocumentIdColumn);

        // DELETE existing rows scoped to the source (all inserted or only changed)
        writer.Append("DELETE FROM ");
        writer.AppendLine(refIdTable);
        writer.Append("WHERE ");
        writer.Append(documentIdCol);
        writer.Append(" IN (SELECT ");
        writer.Append(documentIdCol);
        writer.Append(" FROM ");
        writer.Append(sourceAlias);
        writer.Append(") AND ");
        writer.Append(Quote(ResourceKeyIdColumn));
        writer.Append(" = ");
        writer.Append(resourceKeyId.ToString(CultureInfo.InvariantCulture));
        writer.AppendLine(";");

        // INSERT new rows with UUIDv5 — always join back to 'inserted' for column values
        // (changedDocs only carries DocumentId; inserted has the full row).
        writer.Append("INSERT INTO ");
        writer.Append(refIdTable);
        writer.Append(" (");
        writer.Append(Quote(ReferentialIdColumn));
        writer.Append(", ");
        writer.Append(documentIdCol);
        writer.Append(", ");
        writer.Append(Quote(ResourceKeyIdColumn));
        writer.AppendLine(")");
        writer.Append("SELECT ");
        writer.Append(uuidv5Func);
        writer.Append("('");
        writer.Append(Uuidv5Namespace);
        // Format intentionally matches ReferentialIdCalculator.ResourceInfoString: {ProjectName}{ResourceName}
        // with no separator — do not add one without updating the calculator.
        writer.Append("', CAST(N'");
        writer.Append(SqlDialectBase.EscapeSingleQuote(projectName));
        writer.Append(SqlDialectBase.EscapeSingleQuote(resourceName));
        writer.Append("' AS nvarchar(max)) + ");
        EmitMssqlIdentityHashExpression(writer, identityElements);
        writer.Append("), i.");
        writer.Append(documentIdCol);
        writer.Append(", ");
        writer.AppendLine(resourceKeyId.ToString(CultureInfo.InvariantCulture));
        writer.Append("FROM inserted i");
        if (!isInsert)
        {
            writer.Append(" INNER JOIN ");
            writer.Append(sourceAlias);
            writer.Append(" cd ON cd.");
            writer.Append(documentIdCol);
            writer.Append(" = i.");
            writer.Append(documentIdCol);
        }
        writer.AppendLine(";");
    }

    private void EmitMssqlIdentityHashExpression(
        SqlWriter writer,
        IReadOnlyList<IdentityElementMapping> elements
    )
    {
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" + N'#' + ");
            }
            writer.Append("N'$");
            writer.Append(SqlDialectBase.EscapeSingleQuote(elements[i].IdentityJsonPath));
            writer.Append("=' + ");
            EmitMssqlColumnToNvarchar(writer, elements[i].Column, elements[i].ScalarType);
        }
    }

    /// <summary>
    /// Emits a type-aware nvarchar conversion for an identity column value in MSSQL.
    /// Uses deterministic CONVERT styles for temporal types to ensure cross-engine parity
    /// with PostgreSQL and Core's <c>JsonValue.ToString()</c>.
    /// </summary>
    private void EmitMssqlColumnToNvarchar(
        SqlWriter writer,
        DbColumnName column,
        RelationalScalarType scalarType
    )
    {
        var quoted = Quote(column);
        switch (scalarType.Kind)
        {
            case ScalarKind.String:
                // Already nvarchar — no cast needed.
                writer.Append("i.");
                writer.Append(quoted);
                break;

            case ScalarKind.Date:
                // ISO 8601: YYYY-MM-DD (CONVERT style 23).
                writer.Append("CONVERT(nvarchar(10), i.");
                writer.Append(quoted);
                writer.Append(", 23)");
                break;

            case ScalarKind.DateTime:
                // ISO 8601: YYYY-MM-DDTHH:mm:ss (CONVERT style 126, truncated to 19 chars).
                // Truncation to whole seconds matches PG to_char() which also omits fractional
                // seconds, ensuring cross-engine identity hash parity.
                writer.Append("CONVERT(nvarchar(19), i.");
                writer.Append(quoted);
                writer.Append(", 126)");
                break;

            case ScalarKind.Time:
                // HH:mm:ss (CONVERT style 108).
                writer.Append("CONVERT(nvarchar(8), i.");
                writer.Append(quoted);
                writer.Append(", 108)");
                break;

            case ScalarKind.Boolean:
                // CAST(bit AS nvarchar) produces '1'/'0', but Core's JsonValue.ToString()
                // and PG bool::text both produce 'true'/'false'. Use CASE to match.
                writer.Append("CASE WHEN i.");
                writer.Append(quoted);
                writer.Append(" = 1 THEN N'true' ELSE N'false' END");
                break;

            default:
                // Int32, Int64, Decimal — CAST is deterministic and matches Core/PG formatting.
                writer.Append("CAST(i.");
                writer.Append(quoted);
                writer.Append(" AS nvarchar(max))");
                break;
        }
    }

    /// <summary>
    /// Emits abstract identity maintenance trigger body that maintains abstract identity
    /// tables from concrete resource root tables.
    /// </summary>
    private void EmitAbstractIdentityBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        TriggerKindParameters.AbstractIdentityMaintenance abstractId
    )
    {
        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlAbstractIdentityBody(
                writer,
                trigger.IdentityProjectionColumns,
                abstractId.TargetTable,
                abstractId.TargetColumnMappings,
                abstractId.DiscriminatorValue
            );
        }
        else
        {
            EmitMssqlAbstractIdentityBody(
                writer,
                trigger.IdentityProjectionColumns,
                abstractId.TargetTable,
                abstractId.TargetColumnMappings,
                abstractId.DiscriminatorValue
            );
        }
    }

    private void EmitPgsqlAbstractIdentityBody(
        SqlWriter writer,
        IReadOnlyList<DbColumnName> identityProjectionColumns,
        DbTableName targetTableName,
        IReadOnlyList<TriggerColumnMapping> mappings,
        string discriminatorValue
    )
    {
        // Guard: skip recomputation on no-op UPDATEs where identity columns didn't change
        writer.Append("IF TG_OP = 'INSERT' OR (");
        EmitPgsqlValueDiffDisjunction(writer, identityProjectionColumns);
        writer.AppendLine(") THEN");

        using (writer.Indent())
        {
            var targetTable = Quote(targetTableName);

            // INSERT ... ON CONFLICT DO UPDATE
            writer.Append("INSERT INTO ");
            writer.Append(targetTable);
            writer.Append(" (");
            writer.Append(Quote(DocumentIdColumn));
            foreach (var mapping in mappings)
            {
                writer.Append(", ");
                writer.Append(Quote(mapping.TargetColumn));
            }
            writer.Append(", ");
            writer.Append(Quote(DiscriminatorColumn));
            writer.AppendLine(")");

            writer.Append("VALUES (NEW.");
            writer.Append(Quote(DocumentIdColumn));
            foreach (var mapping in mappings)
            {
                writer.Append(", NEW.");
                writer.Append(Quote(mapping.SourceColumn));
            }
            writer.Append(", '");
            writer.Append(SqlDialectBase.EscapeSingleQuote(discriminatorValue));
            writer.AppendLine("')");

            writer.Append("ON CONFLICT (");
            writer.Append(Quote(DocumentIdColumn));
            writer.AppendLine(")");

            writer.Append("DO UPDATE SET ");
            for (int i = 0; i < mappings.Count; i++)
            {
                if (i > 0)
                {
                    writer.Append(", ");
                }
                writer.Append(Quote(mappings[i].TargetColumn));
                writer.Append(" = EXCLUDED.");
                writer.Append(Quote(mappings[i].TargetColumn));
            }
            writer.AppendLine(";");
        }

        writer.AppendLine("END IF;");
    }

    private void EmitMssqlAbstractIdentityBody(
        SqlWriter writer,
        IReadOnlyList<DbColumnName> identityProjectionColumns,
        DbTableName targetTableName,
        IReadOnlyList<TriggerColumnMapping> mappings,
        string discriminatorValue
    )
    {
        EmitMssqlInsertUpdateDispatch(
            writer,
            identityProjectionColumns,
            isInsert =>
                EmitMssqlAbstractIdentityUpsert(
                    writer,
                    targetTableName,
                    mappings,
                    discriminatorValue,
                    isInsert
                )
        );
    }

    /// <summary>
    /// Shared MSSQL INSERT/UPDATE dispatch skeleton. Emits the IF NOT EXISTS (deleted) / ELSE IF UPDATE()
    /// branching structure, delegating the block-specific logic to <paramref name="emitBlock"/>.
    /// </summary>
    private void EmitMssqlInsertUpdateDispatch(
        SqlWriter writer,
        IReadOnlyList<DbColumnName> identityProjectionColumns,
        Action<bool> emitBlock
    )
    {
        // INSERT case: no deleted rows, so process all inserted rows.
        writer.AppendLine("IF NOT EXISTS (SELECT 1 FROM deleted)");
        writer.AppendLine("BEGIN");

        using (writer.Indent())
        {
            emitBlock(true);
        }

        writer.AppendLine("END");

        // UPDATE case: use UPDATE(col) as a performance pre-filter only, then compute a value-diff
        // workset to find rows whose identity projection values actually changed (null-safe).
        // This is critical for key-unification correctness: UPDATE(aliasColumn) returns false when
        // a CASCADE updates the canonical column, so UPDATE() alone would miss those changes.
        writer.Append("ELSE IF (");
        EmitMssqlUpdateColumnDisjunction(writer, identityProjectionColumns);
        writer.AppendLine(")");
        writer.AppendLine("BEGIN");

        using (writer.Indent())
        {
            EmitMssqlValueDiffWorkset(writer, identityProjectionColumns);
            emitBlock(false);
        }

        writer.AppendLine("END");
    }

    /// <summary>
    /// Emits an UPDATE + INSERT upsert for abstract identity maintenance.
    /// SQL Server rejects MERGE when the target table has an INSTEAD OF UPDATE
    /// trigger, which can happen once identity propagation fallback triggers are
    /// derived for abstract identity tables.
    /// When <paramref name="isInsert"/> is true, scopes to all <c>inserted</c> rows;
    /// otherwise scopes to the <c>@changedDocs</c> value-diff workset.
    /// </summary>
    private void EmitMssqlAbstractIdentityUpsert(
        SqlWriter writer,
        DbTableName targetTableName,
        IReadOnlyList<TriggerColumnMapping> mappings,
        string discriminatorValue,
        bool isInsert
    )
    {
        var targetTable = Quote(targetTableName);
        var documentIdCol = Quote(DocumentIdColumn);

        // UPDATE existing rows first.
        writer.AppendLine("UPDATE t");
        writer.Append("SET ");
        for (int i = 0; i < mappings.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(", ");
            }
            writer.Append("t.");
            writer.Append(Quote(mappings[i].TargetColumn));
            writer.Append(" = s.");
            writer.Append(Quote(mappings[i].SourceColumn));
        }
        writer.AppendLine();
        writer.Append("FROM ");
        writer.Append(targetTable);
        writer.AppendLine(" t");

        if (isInsert)
        {
            writer.Append("INNER JOIN inserted s ON t.");
        }
        else
        {
            writer.Append("INNER JOIN (SELECT i.* FROM inserted i INNER JOIN ");
            writer.Append("@changedDocs");
            writer.Append(" cd ON cd.");
            writer.Append(documentIdCol);
            writer.Append(" = i.");
            writer.Append(documentIdCol);
            writer.Append(") AS s ON t.");
        }
        writer.Append(documentIdCol);
        writer.Append(" = s.");
        writer.Append(documentIdCol);
        writer.AppendLine(";");

        // INSERT only the rows that do not already exist in the target table.
        writer.Append("INSERT INTO ");
        writer.Append(targetTable);
        writer.Append(" (");
        writer.Append(documentIdCol);
        foreach (var mapping in mappings)
        {
            writer.Append(", ");
            writer.Append(Quote(mapping.TargetColumn));
        }
        writer.Append(", ");
        writer.Append(Quote(DiscriminatorColumn));
        writer.AppendLine(")");
        writer.Append("SELECT s.");
        writer.Append(documentIdCol);
        foreach (var mapping in mappings)
        {
            writer.Append(", s.");
            writer.Append(Quote(mapping.SourceColumn));
        }
        writer.Append(", N'");
        writer.Append(SqlDialectBase.EscapeSingleQuote(discriminatorValue));
        writer.AppendLine("'");

        if (isInsert)
        {
            writer.AppendLine("FROM inserted s");
        }
        else
        {
            writer.Append("FROM (SELECT i.* FROM inserted i INNER JOIN ");
            writer.Append("@changedDocs");
            writer.Append(" cd ON cd.");
            writer.Append(documentIdCol);
            writer.Append(" = i.");
            writer.Append(documentIdCol);
            writer.AppendLine(") AS s");
        }

        writer.Append("LEFT JOIN ");
        writer.Append(targetTable);
        writer.Append(" existing ON existing.");
        writer.Append(documentIdCol);
        writer.Append(" = s.");
        writer.Append(documentIdCol);
        writer.AppendLine();
        writer.Append("WHERE existing.");
        writer.Append(documentIdCol);
        writer.AppendLine(" IS NULL;");
    }

    /// <summary>
    /// Emits identity propagation fallback trigger body (MSSQL only) that cascades
    /// identity column updates to referrer tables when <c>ON UPDATE CASCADE</c> is not available.
    /// The trigger is placed on the referenced entity and propagates to all referrers.
    /// </summary>
    private void EmitIdentityPropagationBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        TriggerKindParameters.IdentityPropagationFallback propagation
    )
    {
        if (_dialect.Rules.Dialect != SqlDialect.Mssql)
        {
            throw new InvalidOperationException(
                $"Identity propagation fallback triggers are only supported for MSSQL, but dialect is {_dialect.Rules.Dialect}."
            );
        }

        if (propagation.ReferrerUpdates.Count == 0)
        {
            throw new InvalidOperationException(
                "IdentityPropagationFallback trigger was created with zero referrer updates. "
                    + "This indicates a bug in DeriveTriggerInventoryPass — triggers with no "
                    + "referrers should be skipped."
            );
        }

        var documentIdCol = Quote(DocumentIdColumn);
        var tableKeyColumns = GetKeyColumnsForDocumentStamping(tableModel, trigger.Name.Value);
        var updatableColumns = GetWritableStoredNonKeyColumns(tableModel, trigger.Name.Value);

        // Identity propagation must happen before the owning row update so the subsequent
        // base-table UPDATE can satisfy ON UPDATE NO ACTION reference FKs without transient
        // violations.
        writer.Append("IF (");
        EmitMssqlUpdateColumnDisjunction(writer, trigger.IdentityProjectionColumns);
        writer.AppendLine(")");
        writer.AppendLine("BEGIN");

        using (writer.Indent())
        {
            // Emit an UPDATE statement for each referrer table.
            foreach (var referrer in propagation.ReferrerUpdates)
            {
                var referrerTable = Quote(referrer.ReferrerTable);
                var fkColumn = Quote(referrer.ReferrerFkColumn);

                writer.AppendLine("UPDATE r");
                writer.Append("SET ");
                for (int i = 0; i < referrer.ColumnMappings.Count; i++)
                {
                    if (i > 0)
                    {
                        writer.Append(", ");
                    }
                    // TargetColumn = referrer's stored identity column (e.g., School_SchoolId)
                    // SourceColumn = trigger table's identity column (e.g., SchoolId)
                    writer.Append("r.");
                    writer.Append(Quote(referrer.ColumnMappings[i].TargetColumn));
                    writer.Append(" = i.");
                    writer.Append(Quote(referrer.ColumnMappings[i].SourceColumn));
                }
                writer.AppendLine();

                writer.Append("FROM ");
                writer.Append(referrerTable);
                writer.AppendLine(" r");

                // Join referrer to deleted via FK column pointing to DocumentId.
                writer.Append("INNER JOIN deleted d ON r.");
                writer.Append(fkColumn);
                writer.Append(" = d.");
                writer.AppendLine(documentIdCol);

                // Correlate old/new rows by DocumentId (the universal PK of the trigger's owning table).
                writer.Append("INNER JOIN inserted i ON i.");
                writer.Append(documentIdCol);
                writer.Append(" = d.");
                writer.AppendLine(documentIdCol);

                // Only update if identity columns actually changed.
                writer.Append("WHERE ");
                for (int i = 0; i < referrer.ColumnMappings.Count; i++)
                {
                    if (i > 0)
                    {
                        writer.Append(" OR ");
                    }
                    var col = Quote(referrer.ColumnMappings[i].SourceColumn);
                    EmitMssqlNullSafeNotEqual(writer, "i", col, "d", col);
                }
                writer.AppendLine();
                writer.Append("AND ");
                EmitMssqlPropagationOldValueConjunction(writer, referrer.ColumnMappings);
                writer.AppendLine(";");
                writer.AppendLine();
            }
        }

        writer.AppendLine("END");
        writer.AppendLine();

        writer.AppendLine("UPDATE t");
        writer.Append("SET ");
        for (int i = 0; i < updatableColumns.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(", ");
            }

            var quotedColumn = Quote(updatableColumns[i]);
            writer.Append("t.");
            writer.Append(quotedColumn);
            writer.Append(" = i.");
            writer.Append(quotedColumn);
        }
        writer.AppendLine();
        writer.Append("FROM ");
        writer.Append(Quote(trigger.Table));
        writer.AppendLine(" t");
        writer.Append("INNER JOIN inserted i ON ");
        EmitMssqlJoinConjunction(writer, "t", "i", tableKeyColumns);
        writer.AppendLine(";");
    }

    /// <summary>
    /// Emits a PostgreSQL <c>OLD.col IS DISTINCT FROM NEW.col</c> disjunction for identity
    /// projection columns, used as a value-diff guard in trigger bodies.
    /// </summary>
    private void EmitPgsqlValueDiffDisjunction(
        SqlWriter writer,
        IReadOnlyList<DbColumnName> identityProjectionColumns
    )
    {
        for (int i = 0; i < identityProjectionColumns.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" OR ");
            }
            var col = Quote(identityProjectionColumns[i]);
            writer.Append("OLD.");
            writer.Append(col);
            writer.Append(" IS DISTINCT FROM NEW.");
            writer.Append(col);
        }
    }

    /// <summary>
    /// Emits a MSSQL <c>UPDATE(col)</c> disjunction for identity projection columns, used
    /// as a <b>performance pre-filter only</b> (not a correctness gate). <c>UPDATE(col)</c>
    /// returns true if the column appeared in the SET clause regardless of whether the value
    /// actually changed, and returns false for computed alias columns updated via CASCADE.
    /// </summary>
    private void EmitMssqlUpdateColumnDisjunction(
        SqlWriter writer,
        IReadOnlyList<DbColumnName> identityProjectionColumns
    )
    {
        for (int i = 0; i < identityProjectionColumns.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" OR ");
            }
            writer.Append("UPDATE(");
            writer.Append(Quote(identityProjectionColumns[i]));
            writer.Append(")");
        }
    }

    /// <summary>
    /// Emits a MSSQL table variable <c>@changedDocs</c> populated with the set of
    /// <c>DocumentId</c> values whose identity projection columns actually changed
    /// (null-safe value diff between <c>inserted</c> and <c>deleted</c>).
    /// </summary>
    /// <remarks>
    /// A table variable is used instead of a CTE because T-SQL CTEs scope to the single
    /// immediately-following DML statement, while triggers need the workset across multiple
    /// statements (DELETE + INSERT or MERGE). The table variable persists for the entire
    /// BEGIN...END block. This is the authoritative workset for UPDATE triggers and is
    /// correct under key unification where <c>UPDATE(aliasColumn)</c> returns false for
    /// CASCADE-driven canonical column changes.
    /// </remarks>
    private void EmitMssqlValueDiffWorkset(
        SqlWriter writer,
        IReadOnlyList<DbColumnName> identityProjectionColumns
    )
    {
        var documentIdCol = Quote(DocumentIdColumn);
        writer.Append("DECLARE @changedDocs TABLE (");
        writer.Append(documentIdCol);
        writer.AppendLine(" bigint NOT NULL);");
        writer.Append("INSERT INTO @changedDocs (");
        writer.Append(documentIdCol);
        writer.AppendLine(")");
        writer.Append("SELECT i.");
        writer.AppendLine(documentIdCol);
        writer.Append("FROM inserted i INNER JOIN deleted d ON d.");
        writer.Append(documentIdCol);
        writer.Append(" = i.");
        writer.AppendLine(documentIdCol);
        writer.Append("WHERE ");
        for (int i = 0; i < identityProjectionColumns.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" OR ");
            }
            var col = Quote(identityProjectionColumns[i]);
            EmitMssqlNullSafeNotEqual(writer, "i", col, "d", col);
        }
        writer.AppendLine(";");
    }

    /// <summary>
    /// Emits a NULL-safe inequality comparison for MSSQL, equivalent to PostgreSQL's
    /// <c>IS DISTINCT FROM</c>. Emits <c>(left.col &lt;&gt; right.col OR (left.col IS NULL AND right.col IS NOT NULL) OR (left.col IS NOT NULL AND right.col IS NULL))</c>.
    /// </summary>
    /// <remarks>
    /// PostgreSQL's <c>IS DISTINCT FROM</c> is a null-safe inequality operator:
    /// <c>NULL IS DISTINCT FROM NULL</c> returns <c>false</c> (nulls are considered equal),
    /// and <c>NULL IS DISTINCT FROM value</c> returns <c>true</c>.
    /// SQL Server lacks this operator, so we expand to the three-part OR expression.
    /// </remarks>
    private static void EmitMssqlNullSafeNotEqual(
        SqlWriter writer,
        string leftAlias,
        string quotedColumn,
        string rightAlias,
        string rightQuotedColumn
    )
    {
        writer.Append("(");
        writer.Append(leftAlias);
        writer.Append(".");
        writer.Append(quotedColumn);
        writer.Append(" <> ");
        writer.Append(rightAlias);
        writer.Append(".");
        writer.Append(rightQuotedColumn);
        writer.Append(" OR (");
        writer.Append(leftAlias);
        writer.Append(".");
        writer.Append(quotedColumn);
        writer.Append(" IS NULL AND ");
        writer.Append(rightAlias);
        writer.Append(".");
        writer.Append(rightQuotedColumn);
        writer.Append(" IS NOT NULL) OR (");
        writer.Append(leftAlias);
        writer.Append(".");
        writer.Append(quotedColumn);
        writer.Append(" IS NOT NULL AND ");
        writer.Append(rightAlias);
        writer.Append(".");
        writer.Append(rightQuotedColumn);
        writer.Append(" IS NULL))");
    }

    private static void EmitMssqlNullSafeEqual(
        SqlWriter writer,
        string leftAlias,
        string quotedColumn,
        string rightAlias,
        string rightQuotedColumn
    )
    {
        writer.Append("((");
        writer.Append(leftAlias);
        writer.Append(".");
        writer.Append(quotedColumn);
        writer.Append(" = ");
        writer.Append(rightAlias);
        writer.Append(".");
        writer.Append(rightQuotedColumn);
        writer.Append(") OR (");
        writer.Append(leftAlias);
        writer.Append(".");
        writer.Append(quotedColumn);
        writer.Append(" IS NULL AND ");
        writer.Append(rightAlias);
        writer.Append(".");
        writer.Append(rightQuotedColumn);
        writer.Append(" IS NULL))");
    }

    /// <summary>
    /// Resolves the SQL type for a column using explicit scalar type metadata or dialect defaults.
    /// For columns with an explicit <see cref="RelationalScalarType"/>, delegates to
    /// <see cref="ISqlDialect.RenderColumnType"/>. For implicit key columns (Ordinal, FK, etc.),
    /// falls back to dialect-specific integer defaults.
    /// </summary>
    private string ResolveColumnType(DbColumnModel column)
    {
        var scalarType = column.ScalarType;

        if (scalarType is null)
        {
            // ColumnKind.Scalar always has an explicit ScalarType from schema projection.
            // The cases below are implicit system columns with no ScalarType.
            return column.Kind switch
            {
                ColumnKind.Ordinal => _dialect.OrdinalColumnType,
                ColumnKind.DocumentFk or ColumnKind.DescriptorFk or ColumnKind.ParentKeyPart =>
                    _dialect.DocumentIdColumnType,
                _ => throw new InvalidOperationException(
                    $"Column '{column.ColumnName.Value}' of kind {column.Kind} has no ScalarType."
                ),
            };
        }

        return _dialect.RenderColumnType(scalarType);
    }

    /// <summary>
    /// Formats a table constraint for inclusion within a <c>CREATE TABLE</c> statement.
    /// Returns null for FK constraints which are emitted separately in Phase 4.
    /// </summary>
    private string? FormatConstraint(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique =>
                $"CONSTRAINT {Quote(unique.Name)} UNIQUE ({FormatColumnList(unique.Columns)})",
            TableConstraint.ForeignKey => null, // Skip FKs, emit in Phase 4
            TableConstraint.AllOrNoneNullability allOrNone =>
                $"CONSTRAINT {Quote(allOrNone.Name)} CHECK ({FormatAllOrNoneCheck(allOrNone)})",
            TableConstraint.NullOrTrue nullOrTrue =>
                $"CONSTRAINT {Quote(nullOrTrue.Name)} CHECK ({FormatNullOrTrueCheck(nullOrTrue)})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(constraint),
                constraint,
                "Unsupported table constraint."
            ),
        };
    }

    /// <summary>
    /// Formats the expression for an all-or-none nullability check constraint.
    /// Enforces bidirectional all-or-none semantics: either all columns (FK + dependents)
    /// are NULL, or all columns are NOT NULL. This prevents partial composite FK values.
    /// </summary>
    private string FormatAllOrNoneCheck(TableConstraint.AllOrNoneNullability constraint)
    {
        var fkCol = Quote(constraint.FkColumn);

        // All columns NULL case
        var allNullClause = string.Join(
            " AND ",
            constraint
                .DependentColumns.Select(column => $"{Quote(column)} IS NULL")
                .Prepend($"{fkCol} IS NULL")
        );

        // All columns NOT NULL case
        var allNotNullClause = string.Join(
            " AND ",
            constraint
                .DependentColumns.Select(column => $"{Quote(column)} IS NOT NULL")
                .Prepend($"{fkCol} IS NOT NULL")
        );

        return $"({allNullClause}) OR ({allNotNullClause})";
    }

    /// <summary>
    /// Formats the expression for a null-or-true check constraint.
    /// </summary>
    private string FormatNullOrTrueCheck(TableConstraint.NullOrTrue constraint)
    {
        var trueLiteral = _dialect.RenderBooleanLiteral(true);
        return $"{Quote(constraint.Column)} IS NULL OR {Quote(constraint.Column)} = {trueLiteral}";
    }

    /// <summary>
    /// Formats a comma-separated list of quoted column names.
    /// </summary>
    private string FormatColumnList(IReadOnlyList<DbColumnName> columns)
    {
        return string.Join(", ", columns.Select(Quote));
    }

    /// <summary>
    /// Formats a comma-separated list of quoted key column names.
    /// </summary>
    private string FormatColumnList(IReadOnlyList<DbKeyColumn> columns)
    {
        return string.Join(", ", columns.Select(column => Quote(column.ColumnName)));
    }

    /// <summary>
    /// Emits <c>CREATE VIEW</c> statements for abstract union views.
    /// </summary>
    private void EmitAbstractUnionViews(SqlWriter writer, IReadOnlyList<AbstractUnionViewInfo> views)
    {
        foreach (var viewInfo in views)
        {
            EmitCreateView(writer, viewInfo);
        }
    }

    /// <summary>
    /// Emits a <c>CREATE VIEW</c> statement for a single abstract union view.
    /// </summary>
    private void EmitCreateView(SqlWriter writer, AbstractUnionViewInfo viewInfo)
    {
        EmitViewHeader(writer, viewInfo.ViewName);

        // Emit UNION ALL arms
        if (viewInfo.UnionArmsInOrder.Count == 0)
        {
            throw new InvalidOperationException(
                $"Abstract union view '{viewInfo.ViewName.Schema.Value}.{viewInfo.ViewName.Name}' "
                    + "has no union arms. This indicates a bug in the model derivation phase."
            );
        }

        for (int i = 0; i < viewInfo.UnionArmsInOrder.Count; i++)
        {
            var arm = viewInfo.UnionArmsInOrder[i];

            if (arm.ProjectionExpressionsInSelectOrder.Count != viewInfo.OutputColumnsInSelectOrder.Count)
            {
                throw new InvalidOperationException(
                    $"Union arm from table '{arm.FromTable.Schema.Value}.{arm.FromTable.Name}' has "
                        + $"{arm.ProjectionExpressionsInSelectOrder.Count} projection expressions but the view expects "
                        + $"{viewInfo.OutputColumnsInSelectOrder.Count} output columns."
                );
            }

            if (i > 0)
            {
                writer.AppendLine("UNION ALL");
            }

            writer.Append("SELECT ");

            // Emit projection expressions
            for (int j = 0; j < arm.ProjectionExpressionsInSelectOrder.Count; j++)
            {
                if (j > 0)
                {
                    writer.Append(", ");
                }

                var expr = arm.ProjectionExpressionsInSelectOrder[j];
                var outputColumn = viewInfo.OutputColumnsInSelectOrder[j];

                EmitProjectionExpression(writer, expr, outputColumn.ScalarType);
                writer.Append(" AS ");
                writer.Append(Quote(outputColumn.ColumnName));
            }

            writer.AppendLine();
            writer.Append("FROM ");
            writer.AppendLine(Quote(arm.FromTable));
        }

        writer.AppendLine(";");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits a projection expression for an abstract union view select list.
    /// </summary>
    private void EmitProjectionExpression(
        SqlWriter writer,
        AbstractUnionViewProjectionExpression expr,
        RelationalScalarType targetType
    )
    {
        switch (expr)
        {
            case AbstractUnionViewProjectionExpression.SourceColumn sourceCol:
                if (sourceCol.SourceType is not null && sourceCol.SourceType != targetType)
                {
                    EmitColumnWithCast(writer, sourceCol.ColumnName, targetType);
                }
                else
                {
                    writer.Append(Quote(sourceCol.ColumnName));
                }
                break;

            case AbstractUnionViewProjectionExpression.StringLiteral literal:
                EmitStringLiteralWithCast(writer, literal.Value, targetType);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(expr),
                    expr,
                    "Unsupported projection expression"
                );
        }
    }

    /// <summary>
    /// Emits hard-coded <c>CREATE VIEW</c> statements for the four people authorization views
    /// in the <c>auth</c> schema. These views map <c>SourceEducationOrganizationId</c> to person
    /// <c>DocumentId</c> values through their respective association tables and the EdOrg hierarchy.
    /// </summary>
    /// <remarks>
    /// Views are emitted in alphabetical order by name:
    /// <list type="number">
    ///   <item><c>auth.EducationOrganizationIdToContactDocumentId</c></item>
    ///   <item><c>auth.EducationOrganizationIdToStaffDocumentId</c></item>
    ///   <item><c>auth.EducationOrganizationIdToStudentDocumentId</c></item>
    ///   <item><c>auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility</c></item>
    /// </list>
    /// The definitions are hard-coded because people types are rarely added/modified and their
    /// join structures are not easily generalizable (e.g., Staff joins against two association
    /// tables; Contact goes through Student). See <c>auth.md</c> ("People auth views") for design.
    /// </remarks>
    private void EmitPeopleAuthViews(SqlWriter writer, AuthEdOrgHierarchy? authHierarchy)
    {
        // These views assume all five association tables (StudentSchoolAssociation,
        // StudentContactAssociation, StaffEducationOrganizationAssignmentAssociation,
        // StaffEducationOrganizationEmploymentAssociation, and
        // StudentEducationOrganizationResponsibilityAssociation) exist in the target database.
        // This is guaranteed for any full DS 5.2 deployment that has an auth hierarchy.
        if (authHierarchy is not { EntitiesInNameOrder.Count: > 0 })
        {
            return;
        }

        var authSchema = AuthNames.AuthSchema;
        string edOrgTable = Quote(AuthNames.EdOrgIdToEdOrgId);
        string srcEdOrgId = Quote(AuthNames.SourceEdOrgId);
        string tgtEdOrgId = Quote(AuthNames.TargetEdOrgId);
        string schoolId = Quote(AuthNames.SchoolIdUnified);
        string studentDocId = Quote(AuthNames.StudentDocumentId);
        string contactDocId = Quote(AuthNames.ContactDocumentId);
        string staffDocId = Quote(AuthNames.StaffDocumentId);
        string edOrgEdOrgId = Quote(AuthNames.EdOrgEdOrgId);

        var edfi = new DbSchemaName("edfi");

        // Alphabetical order: Contact, Staff, Student, StudentThroughResponsibility

        // 1. auth.EducationOrganizationIdToContactDocumentId
        //    EdOrg hierarchy -> StudentSchoolAssociation -> StudentContactAssociation
        EmitViewHeader(writer, new DbTableName(authSchema, "EducationOrganizationIdToContactDocumentId"));
        writer.AppendLine($"SELECT DISTINCT");
        using (writer.Indent())
        {
            writer.AppendLine($"edOrg.{srcEdOrgId},");
            writer.AppendLine($"sca.{contactDocId}");
        }
        writer.AppendLine($"FROM {edOrgTable} edOrg");
        writer.AppendLine(
            $"INNER JOIN {Quote(new DbTableName(edfi, "StudentSchoolAssociation"))} ssa"
                + $" ON edOrg.{tgtEdOrgId} = ssa.{schoolId}"
        );
        writer.AppendLine(
            $"INNER JOIN {Quote(new DbTableName(edfi, "StudentContactAssociation"))} sca"
                + $" ON ssa.{studentDocId} = sca.{studentDocId}"
        );
        writer.AppendLine(";");
        writer.AppendLine();

        // 2. auth.EducationOrganizationIdToStaffDocumentId
        //    UNION of two arms: StaffEducationOrganizationAssignmentAssociation
        //    and StaffEducationOrganizationEmploymentAssociation
        //    UNION already deduplicates, so per-arm DISTINCT is not needed.
        EmitViewHeader(writer, new DbTableName(authSchema, "EducationOrganizationIdToStaffDocumentId"));
        writer.AppendLine($"SELECT");
        using (writer.Indent())
        {
            writer.AppendLine($"edOrg.{srcEdOrgId},");
            writer.AppendLine($"seoaa.{staffDocId}");
        }
        writer.AppendLine($"FROM {edOrgTable} edOrg");
        writer.AppendLine(
            $"INNER JOIN {Quote(new DbTableName(edfi, "StaffEducationOrganizationAssignmentAssociation"))} seoaa"
                + $" ON edOrg.{tgtEdOrgId} = seoaa.{edOrgEdOrgId}"
        );
        writer.AppendLine("UNION");
        writer.AppendLine($"SELECT");
        using (writer.Indent())
        {
            writer.AppendLine($"edOrg.{srcEdOrgId},");
            writer.AppendLine($"seoea.{staffDocId}");
        }
        writer.AppendLine($"FROM {edOrgTable} edOrg");
        writer.AppendLine(
            $"INNER JOIN {Quote(new DbTableName(edfi, "StaffEducationOrganizationEmploymentAssociation"))} seoea"
                + $" ON edOrg.{tgtEdOrgId} = seoea.{edOrgEdOrgId}"
        );
        writer.AppendLine(";");
        writer.AppendLine();

        // 3. auth.EducationOrganizationIdToStudentDocumentId
        //    EdOrg hierarchy -> StudentSchoolAssociation
        EmitViewHeader(writer, new DbTableName(authSchema, "EducationOrganizationIdToStudentDocumentId"));
        writer.AppendLine($"SELECT DISTINCT");
        using (writer.Indent())
        {
            writer.AppendLine($"edOrg.{srcEdOrgId},");
            writer.AppendLine($"ssa.{studentDocId}");
        }
        writer.AppendLine($"FROM {edOrgTable} edOrg");
        writer.AppendLine(
            $"INNER JOIN {Quote(new DbTableName(edfi, "StudentSchoolAssociation"))} ssa"
                + $" ON edOrg.{tgtEdOrgId} = ssa.{schoolId}"
        );
        writer.AppendLine(";");
        writer.AppendLine();

        // 4. auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility
        //    EdOrg hierarchy -> StudentEducationOrganizationResponsibilityAssociation
        EmitViewHeader(
            writer,
            new DbTableName(authSchema, "EducationOrganizationIdToStudentDocumentIdThroughResponsibility")
        );
        writer.AppendLine($"SELECT DISTINCT");
        using (writer.Indent())
        {
            writer.AppendLine($"edOrg.{srcEdOrgId},");
            writer.AppendLine($"seora.{studentDocId}");
        }
        writer.AppendLine($"FROM {edOrgTable} edOrg");
        writer.AppendLine(
            $"INNER JOIN {Quote(new DbTableName(edfi, "StudentEducationOrganizationResponsibilityAssociation"))} seora"
                + $" ON edOrg.{tgtEdOrgId} = seora.{edOrgEdOrgId}"
        );
        writer.AppendLine(";");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the dialect-specific <c>CREATE VIEW</c> header: an optional <c>GO</c> batch separator
    /// (MSSQL) followed by <c>CREATE OR REPLACE VIEW</c> / <c>CREATE OR ALTER VIEW</c> and <c> AS</c>.
    /// </summary>
    /// <param name="writer">The SQL writer.</param>
    /// <param name="viewName">The schema-qualified view name (quoted internally).</param>
    private void EmitViewHeader(SqlWriter writer, DbTableName viewName)
    {
        if (_dialect.ViewCreationPattern == DdlPattern.CreateOrAlter)
        {
            writer.AppendLine("GO");
        }

        var createKeyword = _dialect.ViewCreationPattern switch
        {
            DdlPattern.CreateOrReplace => "CREATE OR REPLACE VIEW",
            DdlPattern.CreateOrAlter => "CREATE OR ALTER VIEW",
            _ => throw new ArgumentOutOfRangeException(
                nameof(_dialect.ViewCreationPattern),
                _dialect.ViewCreationPattern,
                "Unsupported view creation pattern."
            ),
        };

        writer.Append(createKeyword);
        writer.Append(" ");
        writer.Append(Quote(viewName));
        writer.AppendLine(" AS");
    }

    /// <summary>
    /// Emits a string literal with dialect-specific CAST expression.
    /// </summary>
    private void EmitStringLiteralWithCast(SqlWriter writer, string value, RelationalScalarType targetType)
    {
        var sqlType = _dialect.RenderColumnType(targetType);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            // PostgreSQL: 'literal'::type
            writer.Append("'");
            writer.Append(SqlDialectBase.EscapeSingleQuote(value));
            writer.Append("'::");
            writer.Append(sqlType);
        }
        else
        {
            // SQL Server: CAST(N'literal' AS type)
            writer.Append("CAST(N'");
            writer.Append(SqlDialectBase.EscapeSingleQuote(value));
            writer.Append("' AS ");
            writer.Append(sqlType);
            writer.Append(")");
        }
    }

    /// <summary>
    /// Emits a column reference with a dialect-specific CAST to the target type.
    /// Used when a source column's type differs from the view's canonical output type.
    /// </summary>
    private void EmitColumnWithCast(
        SqlWriter writer,
        DbColumnName columnName,
        RelationalScalarType targetType
    )
    {
        var sqlType = _dialect.RenderColumnType(targetType);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            // PostgreSQL: "ColumnName"::type
            writer.Append(Quote(columnName));
            writer.Append("::");
            writer.Append(sqlType);
        }
        else
        {
            // SQL Server: CAST([ColumnName] AS type)
            writer.Append("CAST(");
            writer.Append(Quote(columnName));
            writer.Append(" AS ");
            writer.Append(sqlType);
            writer.Append(")");
        }
    }

    private static IReadOnlyDictionary<DbTableName, DbTableModel> BuildTableModelLookup(
        IReadOnlyList<ConcreteResourceModel> concreteResources,
        IReadOnlyList<AbstractIdentityTableInfo> abstractIdentityTables
    )
    {
        Dictionary<DbTableName, DbTableModel> tableModelsByTableName = [];

        foreach (var resource in concreteResources)
        {
            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                tableModelsByTableName[table.Table] = table;
            }
        }

        foreach (var abstractIdentityTable in abstractIdentityTables)
        {
            tableModelsByTableName[abstractIdentityTable.TableModel.Table] = abstractIdentityTable.TableModel;
        }

        return tableModelsByTableName;
    }

    private static IReadOnlyList<DbColumnName> GetStoredColumnsForDocumentStamping(
        DbTableModel tableModel,
        string triggerName
    )
    {
        var storedColumns = tableModel
            .Columns.Where(column => column.Storage is ColumnStorage.Stored)
            .Select(column => column.ColumnName)
            .ToArray();

        if (storedColumns.Length == 0)
        {
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{triggerName}' requires at least one stored column on table '{tableModel.Table.Schema.Value}.{tableModel.Table.Name}'."
            );
        }

        return storedColumns;
    }

    private static IReadOnlyList<DbColumnName> GetKeyColumnsForDocumentStamping(
        DbTableModel tableModel,
        string triggerName
    )
    {
        var keyColumns = tableModel.Key.Columns.Select(column => column.ColumnName).ToArray();

        if (keyColumns.Length == 0)
        {
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{triggerName}' requires at least one key column on table '{tableModel.Table.Schema.Value}.{tableModel.Table.Name}'."
            );
        }

        return keyColumns;
    }

    private static IReadOnlyList<DbColumnName> GetWritableStoredNonKeyColumns(
        DbTableModel tableModel,
        string triggerName
    )
    {
        var keyColumns = tableModel.Key.Columns.Select(column => column.ColumnName).ToHashSet();
        var updatableColumns = tableModel
            .Columns.Where(column =>
                column.Storage is ColumnStorage.Stored
                && column.IsWritable
                && !keyColumns.Contains(column.ColumnName)
            )
            .Select(column => column.ColumnName)
            .ToArray();

        if (updatableColumns.Length == 0)
        {
            throw new InvalidOperationException(
                $"IdentityPropagationFallback trigger '{triggerName}' requires at least one writable stored non-key column on table '{tableModel.Table.Schema.Value}.{tableModel.Table.Name}'."
            );
        }

        return updatableColumns;
    }

    private void EmitMssqlJoinConjunction(
        SqlWriter writer,
        string leftAlias,
        string rightAlias,
        IReadOnlyList<DbColumnName> keyColumns
    )
    {
        for (int i = 0; i < keyColumns.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" AND ");
            }

            var quotedColumn = Quote(keyColumns[i]);
            writer.Append(leftAlias);
            writer.Append(".");
            writer.Append(quotedColumn);
            writer.Append(" = ");
            writer.Append(rightAlias);
            writer.Append(".");
            writer.Append(quotedColumn);
        }
    }

    private void EmitMssqlColumnValueDiffDisjunction(
        SqlWriter writer,
        string leftAlias,
        string rightAlias,
        IReadOnlyList<DbColumnName> columns
    )
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" OR ");
            }

            var quotedColumn = Quote(columns[i]);
            EmitMssqlNullSafeNotEqual(writer, leftAlias, quotedColumn, rightAlias, quotedColumn);
        }
    }

    private void EmitMssqlPropagationOldValueConjunction(
        SqlWriter writer,
        IReadOnlyList<TriggerColumnMapping> columnMappings
    )
    {
        for (int i = 0; i < columnMappings.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" AND ");
            }

            var targetColumn = Quote(columnMappings[i].TargetColumn);
            var sourceColumn = Quote(columnMappings[i].SourceColumn);
            EmitMssqlNullSafeEqual(writer, "r", targetColumn, "d", sourceColumn);
        }
    }

    /// <summary>
    /// Resolves the primary key constraint name, falling back to a conventional default when unset.
    /// </summary>
    private string ResolvePrimaryKeyConstraintName(DbTableModel table)
    {
        return string.IsNullOrWhiteSpace(table.Key.ConstraintName)
            ? _dialect.Rules.ShortenIdentifier($"PK_{table.Table.Schema.Value}_{table.Table.Name}")
            : table.Key.ConstraintName;
    }

    /// <summary>
    /// Quotes a raw identifier using the configured dialect.
    /// </summary>
    private string Quote(string identifier) => _dialect.QuoteIdentifier(identifier);

    /// <summary>
    /// Quotes a schema name using the configured dialect.
    /// </summary>
    private string Quote(DbSchemaName schema) => _dialect.QuoteIdentifier(schema.Value);

    /// <summary>
    /// Quotes a fully-qualified table name using the configured dialect.
    /// </summary>
    private string Quote(DbTableName table) => _dialect.QualifyTable(table);

    /// <summary>
    /// Quotes a column name using the configured dialect.
    /// </summary>
    private string Quote(DbColumnName column) => _dialect.QuoteIdentifier(column.Value);

    /// <summary>
    /// Quotes a trigger name using the configured dialect.
    /// </summary>
    private string Quote(DbTriggerName trigger) => _dialect.QuoteIdentifier(trigger.Value);

    /// <summary>
    /// The UUIDv5 namespace used for referential identity computation.
    /// Must match <c>ReferentialIdCalculator.EdFiUuidv5Namespace</c> in
    /// <c>EdFi.DataManagementService.Core</c>. A guard test in the unit test project
    /// asserts this value to prevent silent divergence.
    /// </summary>
    internal const string Uuidv5Namespace = "edf1edf1-3df1-3df1-3df1-3df1edf1edf1";

    /// <summary>
    /// Formats the qualified <c>dms.ChangeVersionSequence</c> name for the current dialect.
    /// </summary>
    private string FormatSequenceName()
    {
        return $"{Quote(DmsTableNames.Document.Schema)}.{Quote(DmsTableNames.ChangeVersionSequence)}";
    }

    /// <summary>
    /// Formats the qualified <c>dms.uuidv5</c> function name for the current dialect.
    /// </summary>
    private string FormatUuidv5FunctionName()
    {
        return $"{Quote(DmsTableNames.Document.Schema)}.{Quote("uuidv5")}";
    }
}
