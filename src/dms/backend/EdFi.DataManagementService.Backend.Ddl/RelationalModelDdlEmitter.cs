// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits dialect-specific DDL (schemas, tables, indexes, views, and triggers) from a derived relational model set.
/// </summary>
public sealed class RelationalModelDdlEmitter(ISqlDialect dialect)
{
    private readonly ISqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

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

        // Phase 1: Schemas
        EmitSchemas(writer, modelSet.ProjectSchemasInEndpointOrder);

        // Phase 2: Tables (PK/UK/CHECK only, no cross-table FKs)
        EmitTables(writer, modelSet.ConcreteResourcesInNameOrder);

        // Phase 3: Abstract Identity Tables (must precede FKs that reference them)
        EmitAbstractIdentityTables(writer, modelSet.AbstractIdentityTablesInNameOrder);

        // Phase 4: Foreign Keys (separate ALTER TABLE statements)
        EmitForeignKeys(
            writer,
            modelSet.ConcreteResourcesInNameOrder,
            modelSet.AbstractIdentityTablesInNameOrder
        );

        // Phase 5: Indexes
        EmitIndexes(writer, modelSet.IndexesInCreateOrder);

        // Phase 6: Abstract Union Views (must precede Triggers per design)
        EmitAbstractUnionViews(writer, modelSet.AbstractUnionViewsInNameOrder);

        // Phase 7: Triggers
        EmitTriggers(writer, modelSet.TriggersInCreateOrder);

        return writer.ToString();
    }

    /// <summary>
    /// Emits <c>CREATE SCHEMA IF NOT EXISTS</c> statements for each project schema.
    /// </summary>
    private void EmitSchemas(SqlWriter writer, IReadOnlyList<ProjectSchemaInfo> schemas)
    {
        foreach (var schema in schemas)
        {
            writer.AppendLine(_dialect.CreateSchemaIfNotExists(schema.PhysicalSchema));
        }

        if (schemas.Count > 0)
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
                continue;

            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                EmitCreateTable(writer, table);
            }
        }
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
            definitions.Add(RenderColumnDefinition(column));
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
    private string RenderColumnDefinition(DbColumnModel column)
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

        var nullability = column.IsNullable ? "NULL" : "NOT NULL";
        return $"{Quote(column.ColumnName)} {type} {nullability}";
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
                continue;

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
        foreach (var fk in table.Constraints.OfType<TableConstraint.ForeignKey>())
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
                    index.IsUnique
                )
            );
            writer.AppendLine();
        }
    }

    /// <summary>
    /// Emits <c>CREATE TRIGGER</c> statements for each trigger in create-order.
    /// </summary>
    private void EmitTriggers(SqlWriter writer, IReadOnlyList<DbTriggerInfo> triggers)
    {
        foreach (var trigger in triggers)
        {
            // Dispatch by dialect enum rather than pattern abstraction for trigger generation.
            // Adding a new dialect requires updating this site and: EmitDocumentStampingBody,
            // EmitReferentialIdentityBody, EmitAbstractIdentityBody, FormatNullOrTrueCheck,
            // EmitStringLiteralWithCast.
            if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
            {
                EmitPgsqlTrigger(writer, trigger);
            }
            else
            {
                EmitMssqlTrigger(writer, trigger);
            }
        }
    }

    /// <summary>
    /// Emits a PostgreSQL trigger (function + trigger).
    /// Uses DROP TRIGGER IF EXISTS + CREATE TRIGGER per design (not CREATE OR REPLACE TRIGGER).
    /// </summary>
    private void EmitPgsqlTrigger(SqlWriter writer, DbTriggerInfo trigger)
    {
        var funcName = _dialect.Rules.ShortenIdentifier($"TF_{trigger.Name.Value}");
        var schema = trigger.Table.Schema;

        // Function: CREATE OR REPLACE is supported and idempotent
        writer.Append("CREATE OR REPLACE FUNCTION ");
        writer.Append(Quote(schema));
        writer.Append(".");
        writer.Append(Quote(funcName));
        writer.AppendLine("()");
        writer.AppendLine("RETURNS TRIGGER AS $$");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            EmitTriggerBody(writer, trigger);
            writer.AppendLine("RETURN NEW;");
        }
        writer.AppendLine("END;");
        writer.AppendLine("$$ LANGUAGE plpgsql;");
        writer.AppendLine();

        // Trigger: Use DROP + CREATE pattern per design (ddl-generation.md:260-262)
        // PostgreSQL's CREATE OR REPLACE TRIGGER is not available in all versions,
        // so we use the idempotent DROP IF EXISTS + CREATE pattern.
        writer.AppendLine(_dialect.DropTriggerIfExists(trigger.Table, trigger.Name.Value));
        writer.Append("CREATE TRIGGER ");
        writer.AppendLine(Quote(trigger.Name));
        writer.Append("BEFORE INSERT OR UPDATE ON ");
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
    private void EmitMssqlTrigger(SqlWriter writer, DbTriggerInfo trigger)
    {
        // CREATE OR ALTER TRIGGER must be the first statement in a T-SQL batch.
        writer.AppendLine("GO");
        writer.Append("CREATE OR ALTER TRIGGER ");
        writer.Append(Quote(trigger.Table.Schema));
        writer.Append(".");
        writer.AppendLine(Quote(trigger.Name));
        writer.Append("ON ");
        writer.AppendLine(Quote(trigger.Table));
        writer.AppendLine(
            trigger.Parameters is TriggerKindParameters.IdentityPropagationFallback
                ? "AFTER UPDATE"
                : "AFTER INSERT, UPDATE"
        );
        writer.AppendLine("AS");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("SET NOCOUNT ON;");
            EmitTriggerBody(writer, trigger);
        }
        writer.AppendLine("END;");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the trigger body logic based on trigger kind.
    /// </summary>
    private void EmitTriggerBody(SqlWriter writer, DbTriggerInfo trigger)
    {
        switch (trigger.Parameters)
        {
            case TriggerKindParameters.DocumentStamping:
                EmitDocumentStampingBody(writer, trigger);
                break;
            case TriggerKindParameters.ReferentialIdentityMaintenance refId:
                EmitReferentialIdentityBody(writer, refId);
                break;
            case TriggerKindParameters.AbstractIdentityMaintenance abstractId:
                EmitAbstractIdentityBody(writer, abstractId);
                break;
            case TriggerKindParameters.IdentityPropagationFallback propagation:
                EmitIdentityPropagationBody(writer, trigger, propagation);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger.Parameters));
        }
    }

    /// <summary>
    /// Emits document stamping trigger body that stamps <c>dms.Document.ContentVersion</c>
    /// and (for root tables with identity projection columns) <c>IdentityVersion</c> on writes.
    /// </summary>
    private void EmitDocumentStampingBody(SqlWriter writer, DbTriggerInfo trigger)
    {
        if (trigger.KeyColumns.Count != 1)
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{trigger.Name.Value}' requires exactly one key column, but has {trigger.KeyColumns.Count}."
            );

        var documentTable = Quote(DmsTableNames.Document);
        var sequenceName = FormatSequenceName();
        var keyColumn = trigger.KeyColumns[0];

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlDocumentStampingBody(writer, trigger, documentTable, sequenceName, keyColumn);
        }
        else
        {
            EmitMssqlDocumentStampingBody(writer, trigger, documentTable, sequenceName, keyColumn);
        }
    }

    private void EmitPgsqlDocumentStampingBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn
    )
    {
        // ContentVersion stamp
        writer.Append("UPDATE ");
        writer.AppendLine(documentTable);
        writer.Append("SET ");
        writer.Append(Quote(new DbColumnName("ContentVersion")));
        writer.Append(" = nextval('");
        writer.Append(sequenceName);
        writer.Append("'), ");
        writer.Append(Quote(new DbColumnName("ContentLastModifiedAt")));
        writer.AppendLine(" = now()");
        writer.Append("WHERE ");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(" = NEW.");
        writer.Append(Quote(keyColumn));
        writer.AppendLine(";");

        // IdentityVersion stamp for root tables with identity projection columns
        if (trigger.IdentityProjectionColumns.Count > 0)
        {
            // PostgreSQL: IS DISTINCT FROM provides null-safe inequality comparison.
            // (NULL IS DISTINCT FROM NULL) → false, (NULL IS DISTINCT FROM value) → true.
            // Equivalent to MSSQL's EmitMssqlNullSafeNotEqual pattern which expands to:
            // (a <> b OR (a IS NULL AND b IS NOT NULL) OR (a IS NOT NULL AND b IS NULL))
            writer.Append("IF TG_OP = 'UPDATE' AND (");
            for (int i = 0; i < trigger.IdentityProjectionColumns.Count; i++)
            {
                if (i > 0)
                    writer.Append(" OR ");
                var col = Quote(trigger.IdentityProjectionColumns[i]);
                writer.Append("OLD.");
                writer.Append(col);
                writer.Append(" IS DISTINCT FROM NEW.");
                writer.Append(col);
            }
            writer.AppendLine(") THEN");

            using (writer.Indent())
            {
                writer.Append("UPDATE ");
                writer.AppendLine(documentTable);
                writer.Append("SET ");
                writer.Append(Quote(new DbColumnName("IdentityVersion")));
                writer.Append(" = nextval('");
                writer.Append(sequenceName);
                writer.Append("'), ");
                writer.Append(Quote(new DbColumnName("IdentityLastModifiedAt")));
                writer.AppendLine(" = now()");
                writer.Append("WHERE ");
                writer.Append(Quote(new DbColumnName("DocumentId")));
                writer.Append(" = NEW.");
                writer.Append(Quote(keyColumn));
                writer.AppendLine(";");
            }

            writer.AppendLine("END IF;");
        }
    }

    private void EmitMssqlDocumentStampingBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn
    )
    {
        // ContentVersion stamp
        writer.AppendLine("UPDATE d");
        writer.Append("SET d.");
        writer.Append(Quote(new DbColumnName("ContentVersion")));
        writer.Append(" = NEXT VALUE FOR ");
        writer.Append(sequenceName);
        writer.Append(", d.");
        writer.Append(Quote(new DbColumnName("ContentLastModifiedAt")));
        writer.AppendLine(" = sysutcdatetime()");
        writer.Append("FROM ");
        writer.Append(documentTable);
        writer.AppendLine(" d");
        writer.Append("INNER JOIN inserted i ON d.");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(" = i.");
        writer.Append(Quote(keyColumn));
        writer.AppendLine(";");

        // IdentityVersion stamp for root tables with identity projection columns
        if (trigger.IdentityProjectionColumns.Count > 0)
        {
            // Performance pre-filter: UPDATE(col) returns true if the column appeared in the SET clause,
            // regardless of whether the value actually changed. The WHERE clause below (using null-safe
            // inequality) is the authoritative value-change check that filters to only actually changed rows.
            writer.Append("IF EXISTS (SELECT 1 FROM deleted) AND (");
            for (int i = 0; i < trigger.IdentityProjectionColumns.Count; i++)
            {
                if (i > 0)
                    writer.Append(" OR ");
                writer.Append("UPDATE(");
                writer.Append(Quote(trigger.IdentityProjectionColumns[i]));
                writer.Append(")");
            }
            writer.AppendLine(")");

            writer.AppendLine("BEGIN");

            using (writer.Indent())
            {
                writer.AppendLine("UPDATE d");
                writer.Append("SET d.");
                writer.Append(Quote(new DbColumnName("IdentityVersion")));
                writer.Append(" = NEXT VALUE FOR ");
                writer.Append(sequenceName);
                writer.Append(", d.");
                writer.Append(Quote(new DbColumnName("IdentityLastModifiedAt")));
                writer.AppendLine(" = sysutcdatetime()");
                writer.Append("FROM ");
                writer.Append(documentTable);
                writer.AppendLine(" d");
                writer.Append("INNER JOIN inserted i ON d.");
                writer.Append(Quote(new DbColumnName("DocumentId")));
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
        TriggerKindParameters.ReferentialIdentityMaintenance refId
    )
    {
        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlReferentialIdentityBody(writer, refId);
        }
        else
        {
            EmitMssqlReferentialIdentityBody(writer, refId);
        }
    }

    private void EmitPgsqlReferentialIdentityBody(
        SqlWriter writer,
        TriggerKindParameters.ReferentialIdentityMaintenance refId
    )
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

    private void EmitPgsqlReferentialIdentityBlock(
        SqlWriter writer,
        string refIdTable,
        short resourceKeyId,
        string projectName,
        string resourceName,
        IReadOnlyList<IdentityElementMapping> identityElements
    )
    {
        // Defensive assertion: model derivation in DeriveTriggerInventoryPass guarantees non-empty identity elements.
        // If this fires, there's a bug in the model derivation phase.
        Debug.Assert(
            identityElements.Count > 0,
            $"ReferentialIdentityMaintenance trigger requires at least one identity element for resource '{resourceName}'."
        );

        var uuidv5Func = FormatUuidv5FunctionName();

        // DELETE existing row
        writer.Append("DELETE FROM ");
        writer.AppendLine(refIdTable);
        writer.Append("WHERE ");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(" = NEW.");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(" AND ");
        writer.Append(Quote(new DbColumnName("ResourceKeyId")));
        writer.Append(" = ");
        writer.Append(resourceKeyId.ToString());
        writer.AppendLine(";");

        // INSERT new row with UUIDv5
        writer.Append("INSERT INTO ");
        writer.Append(refIdTable);
        writer.Append(" (");
        writer.Append(Quote(new DbColumnName("ReferentialId")));
        writer.Append(", ");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(", ");
        writer.Append(Quote(new DbColumnName("ResourceKeyId")));
        writer.AppendLine(")");
        writer.Append("VALUES (");
        writer.Append(uuidv5Func);
        writer.Append("('");
        writer.Append(Uuidv5Namespace);
        // Format intentionally matches ReferentialIdCalculator.ResourceInfoString: {ProjectName}{ResourceName}
        // with no separator — do not add one without updating the calculator.
        writer.Append("'::uuid, '");
        writer.Append(EscapeSqlLiteral(projectName));
        writer.Append(EscapeSqlLiteral(resourceName));
        writer.Append("' || ");
        EmitPgsqlIdentityHashExpression(writer, identityElements);
        writer.Append("), NEW.");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(", ");
        writer.Append(resourceKeyId.ToString());
        writer.AppendLine(");");
    }

    private void EmitPgsqlIdentityHashExpression(
        SqlWriter writer,
        IReadOnlyList<IdentityElementMapping> elements
    )
    {
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0)
                writer.Append(" || '#' || ");
            writer.Append("'$");
            writer.Append(EscapeSqlLiteral(elements[i].IdentityJsonPath));
            writer.Append("=' || NEW.");
            writer.Append(Quote(elements[i].Column));
            writer.Append("::text");
        }
    }

    private void EmitMssqlReferentialIdentityBody(
        SqlWriter writer,
        TriggerKindParameters.ReferentialIdentityMaintenance refId
    )
    {
        var refIdTable = Quote(DmsTableNames.ReferentialIdentity);

        // Primary referential identity
        EmitMssqlReferentialIdentityBlock(
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
            EmitMssqlReferentialIdentityBlock(
                writer,
                refIdTable,
                alias.ResourceKeyId,
                alias.ProjectName,
                alias.ResourceName,
                alias.IdentityElements
            );
        }
    }

    private void EmitMssqlReferentialIdentityBlock(
        SqlWriter writer,
        string refIdTable,
        short resourceKeyId,
        string projectName,
        string resourceName,
        IReadOnlyList<IdentityElementMapping> identityElements
    )
    {
        // Defensive assertion: model derivation in DeriveTriggerInventoryPass guarantees non-empty identity elements.
        // If this fires, there's a bug in the model derivation phase.
        Debug.Assert(
            identityElements.Count > 0,
            $"ReferentialIdentityMaintenance trigger requires at least one identity element for resource '{resourceName}'."
        );

        var uuidv5Func = FormatUuidv5FunctionName();

        // DELETE existing row
        writer.Append("DELETE FROM ");
        writer.AppendLine(refIdTable);
        writer.Append("WHERE ");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(" IN (SELECT ");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(" FROM inserted) AND ");
        writer.Append(Quote(new DbColumnName("ResourceKeyId")));
        writer.Append(" = ");
        writer.Append(resourceKeyId.ToString());
        writer.AppendLine(";");

        // INSERT new rows from inserted table with UUIDv5
        writer.Append("INSERT INTO ");
        writer.Append(refIdTable);
        writer.Append(" (");
        writer.Append(Quote(new DbColumnName("ReferentialId")));
        writer.Append(", ");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(", ");
        writer.Append(Quote(new DbColumnName("ResourceKeyId")));
        writer.AppendLine(")");
        writer.Append("SELECT ");
        writer.Append(uuidv5Func);
        writer.Append("('");
        writer.Append(Uuidv5Namespace);
        // Format intentionally matches ReferentialIdCalculator.ResourceInfoString: {ProjectName}{ResourceName}
        // with no separator — do not add one without updating the calculator.
        writer.Append("', N'");
        writer.Append(EscapeSqlLiteral(projectName));
        writer.Append(EscapeSqlLiteral(resourceName));
        writer.Append("' + ");
        EmitMssqlIdentityHashExpression(writer, identityElements);
        writer.Append("), i.");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(", ");
        writer.AppendLine(resourceKeyId.ToString());
        writer.AppendLine("FROM inserted i;");
    }

    private void EmitMssqlIdentityHashExpression(
        SqlWriter writer,
        IReadOnlyList<IdentityElementMapping> elements
    )
    {
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0)
                writer.Append(" + N'#' + ");
            writer.Append("N'$");
            writer.Append(EscapeSqlLiteral(elements[i].IdentityJsonPath));
            writer.Append("=' + CAST(i.");
            writer.Append(Quote(elements[i].Column));
            writer.Append(" AS nvarchar(max))");
        }
    }

    /// <summary>
    /// Emits abstract identity maintenance trigger body that maintains abstract identity
    /// tables from concrete resource root tables.
    /// </summary>
    private void EmitAbstractIdentityBody(
        SqlWriter writer,
        TriggerKindParameters.AbstractIdentityMaintenance abstractId
    )
    {
        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlAbstractIdentityBody(
                writer,
                abstractId.TargetTable,
                abstractId.TargetColumnMappings,
                abstractId.DiscriminatorValue
            );
        }
        else
        {
            EmitMssqlAbstractIdentityBody(
                writer,
                abstractId.TargetTable,
                abstractId.TargetColumnMappings,
                abstractId.DiscriminatorValue
            );
        }
    }

    private void EmitPgsqlAbstractIdentityBody(
        SqlWriter writer,
        DbTableName targetTableName,
        IReadOnlyList<TriggerColumnMapping> mappings,
        string discriminatorValue
    )
    {
        var targetTable = Quote(targetTableName);

        // INSERT ... ON CONFLICT DO UPDATE
        writer.Append("INSERT INTO ");
        writer.Append(targetTable);
        writer.Append(" (");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        foreach (var mapping in mappings)
        {
            writer.Append(", ");
            writer.Append(Quote(mapping.TargetColumn));
        }
        writer.Append(", ");
        writer.Append(Quote(new DbColumnName("Discriminator")));
        writer.AppendLine(")");

        writer.Append("VALUES (NEW.");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        foreach (var mapping in mappings)
        {
            writer.Append(", NEW.");
            writer.Append(Quote(mapping.SourceColumn));
        }
        writer.Append(", '");
        writer.Append(EscapeSqlLiteral(discriminatorValue));
        writer.AppendLine("')");

        writer.Append("ON CONFLICT (");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.AppendLine(")");

        writer.Append("DO UPDATE SET ");
        for (int i = 0; i < mappings.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");
            writer.Append(Quote(mappings[i].TargetColumn));
            writer.Append(" = EXCLUDED.");
            writer.Append(Quote(mappings[i].TargetColumn));
        }
        writer.AppendLine(";");
    }

    private void EmitMssqlAbstractIdentityBody(
        SqlWriter writer,
        DbTableName targetTableName,
        IReadOnlyList<TriggerColumnMapping> mappings,
        string discriminatorValue
    )
    {
        var targetTable = Quote(targetTableName);

        // MERGE statement
        writer.Append("MERGE ");
        writer.Append(targetTable);
        writer.AppendLine(" AS t");
        writer.Append("USING inserted AS s ON t.");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        writer.Append(" = s.");
        writer.AppendLine(Quote(new DbColumnName("DocumentId")));

        // WHEN MATCHED THEN UPDATE
        writer.Append("WHEN MATCHED THEN UPDATE SET ");
        for (int i = 0; i < mappings.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");
            writer.Append("t.");
            writer.Append(Quote(mappings[i].TargetColumn));
            writer.Append(" = s.");
            writer.Append(Quote(mappings[i].SourceColumn));
        }
        writer.AppendLine();

        // WHEN NOT MATCHED THEN INSERT
        writer.Append("WHEN NOT MATCHED THEN INSERT (");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        foreach (var mapping in mappings)
        {
            writer.Append(", ");
            writer.Append(Quote(mapping.TargetColumn));
        }
        writer.Append(", ");
        writer.Append(Quote(new DbColumnName("Discriminator")));
        writer.AppendLine(")");

        writer.Append("VALUES (s.");
        writer.Append(Quote(new DbColumnName("DocumentId")));
        foreach (var mapping in mappings)
        {
            writer.Append(", s.");
            writer.Append(Quote(mapping.SourceColumn));
        }
        writer.Append(", N'");
        writer.Append(EscapeSqlLiteral(discriminatorValue));
        writer.AppendLine("');");
    }

    /// <summary>
    /// Emits identity propagation fallback trigger body (MSSQL only) that cascades
    /// identity column updates to referrer tables when <c>ON UPDATE CASCADE</c> is not available.
    /// The trigger is placed on the referenced entity and propagates to all referrers.
    /// </summary>
    private void EmitIdentityPropagationBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
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
            return;
        }

        var documentIdCol = Quote(new DbColumnName("DocumentId"));

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
                    writer.Append(", ");
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
                    writer.Append(" OR ");
                var col = Quote(referrer.ColumnMappings[i].SourceColumn);
                EmitMssqlNullSafeNotEqual(writer, "i", col, "d", col);
            }
            writer.AppendLine(";");
            writer.AppendLine();
        }
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
        // MSSQL: CREATE OR ALTER VIEW must be the first statement in a T-SQL batch.
        if (_dialect.Rules.Dialect == SqlDialect.Mssql)
        {
            writer.AppendLine("GO");
        }

        // Determine view creation pattern based on dialect
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
        writer.Append(Quote(viewInfo.ViewName));
        writer.AppendLine(" AS");

        // Emit UNION ALL arms
        // Defensive assertion: model derivation guarantees at least one union arm for abstract views.
        // If this fires, there's a bug in the model derivation phase.
        Debug.Assert(
            viewInfo.UnionArmsInOrder.Count > 0,
            $"Abstract union view '{viewInfo.ViewName.Schema.Value}.{viewInfo.ViewName.Name}' has no union arms."
        );

        for (int i = 0; i < viewInfo.UnionArmsInOrder.Count; i++)
        {
            var arm = viewInfo.UnionArmsInOrder[i];

            if (arm.ProjectionExpressionsInSelectOrder.Count != viewInfo.OutputColumnsInSelectOrder.Count)
                throw new InvalidOperationException(
                    $"Union arm from table '{arm.FromTable.Schema.Value}.{arm.FromTable.Name}' has "
                        + $"{arm.ProjectionExpressionsInSelectOrder.Count} projection expressions but the view expects "
                        + $"{viewInfo.OutputColumnsInSelectOrder.Count} output columns."
                );

            if (i > 0)
            {
                writer.AppendLine("UNION ALL");
            }

            writer.Append("SELECT ");

            // Emit projection expressions
            for (int j = 0; j < arm.ProjectionExpressionsInSelectOrder.Count; j++)
            {
                if (j > 0)
                    writer.Append(", ");

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
                writer.Append(Quote(sourceCol.ColumnName));
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
    /// Emits a string literal with dialect-specific CAST expression.
    /// </summary>
    private void EmitStringLiteralWithCast(SqlWriter writer, string value, RelationalScalarType targetType)
    {
        var sqlType = _dialect.RenderColumnType(targetType);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            // PostgreSQL: 'literal'::type
            writer.Append("'");
            writer.Append(EscapeSqlLiteral(value));
            writer.Append("'::");
            writer.Append(sqlType);
        }
        else
        {
            // SQL Server: CAST(N'literal' AS type)
            writer.Append("CAST(N'");
            writer.Append(EscapeSqlLiteral(value));
            writer.Append("' AS ");
            writer.Append(sqlType);
            writer.Append(")");
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

    /// <summary>
    /// Escapes single quotes in a value for safe embedding in a SQL string literal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inputs are schema-derived and pre-validated (project names, resource names, JSON paths)
    /// from the API schema loader. These are not user-supplied runtime values.
    /// </para>
    /// <para>
    /// Assumes <c>standard_conforming_strings = on</c> (PostgreSQL default since 9.1),
    /// so backslashes are treated as literal characters and do not need escaping.
    /// For MSSQL, <c>SET QUOTED_IDENTIFIER ON</c> (the default) provides similar semantics.
    /// </para>
    /// </remarks>
    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");
}
