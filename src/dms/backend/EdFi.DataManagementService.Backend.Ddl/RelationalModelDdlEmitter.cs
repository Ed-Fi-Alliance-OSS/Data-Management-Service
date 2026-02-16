// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits dialect-specific DDL (schemas, tables, indexes, and triggers) from a derived relational model set.
/// </summary>
public sealed class RelationalModelDdlEmitter(ISqlDialectRules dialectRules)
{
    private readonly ISqlDialectRules _dialectRules =
        dialectRules ?? throw new ArgumentNullException(nameof(dialectRules));

    /// <summary>
    /// Builds a SQL script that creates all schemas, tables, indexes, and triggers in the model set.
    /// </summary>
    /// <param name="modelSet">The derived relational model set to emit.</param>
    /// <returns>The emitted DDL script.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the model set dialect does not match the emitter dialect rules.
    /// </exception>
    public string Emit(DerivedRelationalModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        if (modelSet.Dialect != _dialectRules.Dialect)
        {
            throw new InvalidOperationException(
                $"Dialect mismatch: model={modelSet.Dialect}, rules={_dialectRules.Dialect}."
            );
        }

        var builder = new StringBuilder();

        // Phase 1: Schemas
        AppendSchemas(builder, modelSet.ProjectSchemasInEndpointOrder);

        // Phase 2: Tables (PK/UK/CHECK only, no cross-table FKs)
        AppendTables(builder, modelSet.ConcreteResourcesInNameOrder);

        // Phase 3: Abstract Identity Tables (must precede FKs that reference them)
        AppendAbstractIdentityTables(builder, modelSet.AbstractIdentityTablesInNameOrder);

        // Phase 4: Foreign Keys (separate ALTER TABLE statements)
        AppendForeignKeys(builder, modelSet.ConcreteResourcesInNameOrder);

        // Phase 5: Indexes
        AppendIndexes(builder, modelSet.IndexesInCreateOrder);

        // Phase 6: Triggers
        AppendTriggers(builder, modelSet.TriggersInCreateOrder);

        // Phase 7: Abstract Union Views
        AppendAbstractUnionViews(builder, modelSet.AbstractUnionViewsInNameOrder);

        return builder.ToString();
    }

    /// <summary>
    /// Appends <c>CREATE SCHEMA</c> statements for each project schema.
    /// </summary>
    private void AppendSchemas(StringBuilder builder, IReadOnlyList<ProjectSchemaInfo> schemas)
    {
        foreach (var schema in schemas)
        {
            builder.Append("CREATE SCHEMA ");
            builder.Append(Quote(schema.PhysicalSchema));
            builder.AppendLine(";");
        }

        if (schemas.Count > 0)
        {
            builder.AppendLine();
        }
    }

    /// <summary>
    /// Appends <c>CREATE TABLE</c> statements for each table in each concrete resource model.
    /// </summary>
    private void AppendTables(StringBuilder builder, IReadOnlyList<ConcreteResourceModel> resources)
    {
        foreach (var resource in resources)
        {
            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                AppendCreateTable(builder, table);
            }
        }
    }

    /// <summary>
    /// Appends a <c>CREATE TABLE</c> statement including columns, key, and table constraints.
    /// </summary>
    private void AppendCreateTable(StringBuilder builder, DbTableModel table)
    {
        builder.Append("CREATE TABLE ");
        builder.Append(Quote(table.Table));
        builder.AppendLine(" (");

        var definitions = new List<string>();

        foreach (var column in table.Columns)
        {
            var type = ResolveColumnType(column);
            var nullability = column.IsNullable ? "NULL" : "NOT NULL";
            definitions.Add($"{Quote(column.ColumnName)} {type} {nullability}");
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

        for (var i = 0; i < definitions.Count; i++)
        {
            builder.Append("    ");
            builder.Append(definitions[i]);

            if (i < definitions.Count - 1)
            {
                builder.Append(',');
            }

            builder.AppendLine();
        }

        builder.AppendLine(");");
        builder.AppendLine();
    }

    /// <summary>
    /// Appends <c>ALTER TABLE ADD CONSTRAINT</c> statements for all foreign keys.
    /// </summary>
    private void AppendForeignKeys(StringBuilder builder, IReadOnlyList<ConcreteResourceModel> resources)
    {
        foreach (var resource in resources)
        {
            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                foreach (var fk in table.Constraints.OfType<TableConstraint.ForeignKey>())
                {
                    builder.Append("ALTER TABLE ");
                    builder.Append(Quote(table.Table));
                    builder.Append(" ADD CONSTRAINT ");
                    builder.Append(Quote(fk.Name));
                    builder.Append(" FOREIGN KEY (");
                    builder.Append(FormatColumnList(fk.Columns));
                    builder.Append(") REFERENCES ");
                    builder.Append(Quote(fk.TargetTable));
                    builder.Append(" (");
                    builder.Append(FormatColumnList(fk.TargetColumns));
                    builder.Append(")");
                    builder.Append(FormatReferentialActions(fk));
                    builder.AppendLine(";");
                    builder.AppendLine();
                }
            }
        }
    }

    /// <summary>
    /// Appends <c>CREATE TABLE</c> statements for abstract identity tables.
    /// </summary>
    private void AppendAbstractIdentityTables(
        StringBuilder builder,
        IReadOnlyList<AbstractIdentityTableInfo> tables
    )
    {
        foreach (var tableInfo in tables)
        {
            // Reuse existing AppendCreateTable - it already handles all table types
            AppendCreateTable(builder, tableInfo.TableModel);
        }
    }

    /// <summary>
    /// Appends <c>CREATE INDEX</c> statements for each index in create-order.
    /// </summary>
    private void AppendIndexes(StringBuilder builder, IReadOnlyList<DbIndexInfo> indexes)
    {
        foreach (var index in indexes)
        {
            var unique = index.IsUnique ? "UNIQUE " : string.Empty;
            builder.Append("CREATE ");
            builder.Append(unique);
            builder.Append("INDEX ");
            builder.Append(Quote(index.Name));
            builder.Append(" ON ");
            builder.Append(Quote(index.Table));
            builder.Append(" (");
            builder.Append(FormatColumnList(index.KeyColumns));
            builder.AppendLine(");");
            builder.AppendLine();
        }
    }

    /// <summary>
    /// Appends <c>CREATE TRIGGER</c> statements for each trigger in create-order.
    /// </summary>
    private void AppendTriggers(StringBuilder builder, IReadOnlyList<DbTriggerInfo> triggers)
    {
        foreach (var trigger in triggers)
        {
            if (_dialectRules.Dialect == SqlDialect.Pgsql)
            {
                AppendPgsqlTrigger(builder, trigger);
            }
            else
            {
                AppendMssqlTrigger(builder, trigger);
            }
        }
    }

    /// <summary>
    /// Appends a PostgreSQL trigger (function + trigger).
    /// </summary>
    private void AppendPgsqlTrigger(StringBuilder builder, DbTriggerInfo trigger)
    {
        var funcName = $"TF_{trigger.Name.Value}";
        var schema = trigger.Table.Schema;

        // Function
        builder.Append("CREATE OR REPLACE FUNCTION ");
        builder.Append(Quote(schema));
        builder.Append('.');
        builder.Append(Quote(funcName));
        builder.AppendLine("()");
        builder.AppendLine("RETURNS TRIGGER AS $$");
        builder.AppendLine("BEGIN");
        AppendTriggerBody(builder, trigger, "    ");
        builder.AppendLine("    RETURN NEW;");
        builder.AppendLine("END;");
        builder.AppendLine("$$ LANGUAGE plpgsql;");
        builder.AppendLine();

        // Trigger
        builder.Append("CREATE OR REPLACE TRIGGER ");
        builder.Append(Quote(trigger.Name));
        builder.AppendLine();
        builder.Append("BEFORE INSERT OR UPDATE ON ");
        builder.Append(Quote(trigger.Table));
        builder.AppendLine();
        builder.AppendLine("FOR EACH ROW");
        builder.Append("EXECUTE FUNCTION ");
        builder.Append(Quote(schema));
        builder.Append('.');
        builder.Append(Quote(funcName));
        builder.AppendLine("();");
        builder.AppendLine();
    }

    /// <summary>
    /// Appends a SQL Server trigger.
    /// </summary>
    private void AppendMssqlTrigger(StringBuilder builder, DbTriggerInfo trigger)
    {
        builder.Append("CREATE OR ALTER TRIGGER ");
        builder.Append(Quote(trigger.Table.Schema));
        builder.Append('.');
        builder.Append(Quote(trigger.Name));
        builder.AppendLine();
        builder.Append("ON ");
        builder.Append(Quote(trigger.Table));
        builder.AppendLine();
        builder.AppendLine("AFTER INSERT, UPDATE");
        builder.AppendLine("AS");
        builder.AppendLine("BEGIN");
        builder.AppendLine("    SET NOCOUNT ON;");
        AppendTriggerBody(builder, trigger, "    ");
        builder.AppendLine("END;");
        builder.AppendLine();
    }

    /// <summary>
    /// Appends the trigger body logic based on trigger kind.
    /// </summary>
    private void AppendTriggerBody(StringBuilder builder, DbTriggerInfo trigger, string indent)
    {
        switch (trigger.Kind)
        {
            case DbTriggerKind.DocumentStamping:
                AppendDocumentStampingBody(builder, trigger, indent);
                break;
            case DbTriggerKind.ReferentialIdentityMaintenance:
                AppendReferentialIdentityBody(builder, trigger, indent);
                break;
            case DbTriggerKind.AbstractIdentityMaintenance:
                AppendAbstractIdentityBody(builder, trigger, indent);
                break;
            case DbTriggerKind.IdentityPropagationFallback:
                AppendIdentityPropagationBody(builder, trigger, indent);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger.Kind));
        }
    }

    /// <summary>
    /// Appends document stamping trigger body that stamps <c>dms.Document.ContentVersion</c>
    /// and (for root tables with identity projection columns) <c>IdentityVersion</c> on writes.
    /// </summary>
    private void AppendDocumentStampingBody(StringBuilder builder, DbTriggerInfo trigger, string indent)
    {
        var documentTable = Quote(DmsTableNames.Document);
        var sequenceName = FormatSequenceName();
        var keyColumn = trigger.KeyColumns[0];

        if (_dialectRules.Dialect == SqlDialect.Pgsql)
        {
            AppendPgsqlDocumentStampingBody(builder, trigger, indent, documentTable, sequenceName, keyColumn);
        }
        else
        {
            AppendMssqlDocumentStampingBody(builder, trigger, indent, documentTable, sequenceName, keyColumn);
        }
    }

    private void AppendPgsqlDocumentStampingBody(
        StringBuilder builder,
        DbTriggerInfo trigger,
        string indent,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn
    )
    {
        // ContentVersion stamp
        builder.Append(indent);
        builder.Append("UPDATE ");
        builder.AppendLine(documentTable);
        builder.Append(indent);
        builder.Append("SET ");
        builder.Append(Quote(new DbColumnName("ContentVersion")));
        builder.Append(" = nextval('");
        builder.Append(sequenceName);
        builder.Append("'), ");
        builder.Append(Quote(new DbColumnName("ContentLastModifiedAt")));
        builder.AppendLine(" = now()");
        builder.Append(indent);
        builder.Append("WHERE ");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(" = NEW.");
        builder.Append(Quote(keyColumn));
        builder.AppendLine(";");

        // IdentityVersion stamp for root tables with identity projection columns
        if (trigger.IdentityProjectionColumns.Count > 0)
        {
            builder.Append(indent);
            builder.Append("IF TG_OP = 'UPDATE' AND (");
            for (int i = 0; i < trigger.IdentityProjectionColumns.Count; i++)
            {
                if (i > 0)
                    builder.Append(" OR ");
                var col = Quote(trigger.IdentityProjectionColumns[i]);
                builder.Append("OLD.");
                builder.Append(col);
                builder.Append(" IS DISTINCT FROM NEW.");
                builder.Append(col);
            }
            builder.AppendLine(") THEN");

            var innerIndent = indent + "    ";
            builder.Append(innerIndent);
            builder.Append("UPDATE ");
            builder.AppendLine(documentTable);
            builder.Append(innerIndent);
            builder.Append("SET ");
            builder.Append(Quote(new DbColumnName("IdentityVersion")));
            builder.Append(" = nextval('");
            builder.Append(sequenceName);
            builder.Append("'), ");
            builder.Append(Quote(new DbColumnName("IdentityLastModifiedAt")));
            builder.AppendLine(" = now()");
            builder.Append(innerIndent);
            builder.Append("WHERE ");
            builder.Append(Quote(new DbColumnName("DocumentId")));
            builder.Append(" = NEW.");
            builder.Append(Quote(keyColumn));
            builder.AppendLine(";");

            builder.Append(indent);
            builder.AppendLine("END IF;");
        }
    }

    private void AppendMssqlDocumentStampingBody(
        StringBuilder builder,
        DbTriggerInfo trigger,
        string indent,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn
    )
    {
        // ContentVersion stamp
        builder.Append(indent);
        builder.AppendLine("UPDATE d");
        builder.Append(indent);
        builder.Append("SET d.");
        builder.Append(Quote(new DbColumnName("ContentVersion")));
        builder.Append(" = NEXT VALUE FOR ");
        builder.Append(sequenceName);
        builder.Append(", d.");
        builder.Append(Quote(new DbColumnName("ContentLastModifiedAt")));
        builder.AppendLine(" = sysutcdatetime()");
        builder.Append(indent);
        builder.Append("FROM ");
        builder.Append(documentTable);
        builder.AppendLine(" d");
        builder.Append(indent);
        builder.Append("INNER JOIN inserted i ON d.");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(" = i.");
        builder.Append(Quote(keyColumn));
        builder.AppendLine(";");

        // IdentityVersion stamp for root tables with identity projection columns
        if (trigger.IdentityProjectionColumns.Count > 0)
        {
            builder.Append(indent);
            builder.Append("IF EXISTS (SELECT 1 FROM deleted) AND (");
            for (int i = 0; i < trigger.IdentityProjectionColumns.Count; i++)
            {
                if (i > 0)
                    builder.Append(" OR ");
                builder.Append("UPDATE(");
                builder.Append(Quote(trigger.IdentityProjectionColumns[i]));
                builder.Append(')');
            }
            builder.AppendLine(")");

            builder.Append(indent);
            builder.AppendLine("BEGIN");

            var innerIndent = indent + "    ";
            builder.Append(innerIndent);
            builder.AppendLine("UPDATE d");
            builder.Append(innerIndent);
            builder.Append("SET d.");
            builder.Append(Quote(new DbColumnName("IdentityVersion")));
            builder.Append(" = NEXT VALUE FOR ");
            builder.Append(sequenceName);
            builder.Append(", d.");
            builder.Append(Quote(new DbColumnName("IdentityLastModifiedAt")));
            builder.AppendLine(" = sysutcdatetime()");
            builder.Append(innerIndent);
            builder.Append("FROM ");
            builder.Append(documentTable);
            builder.AppendLine(" d");
            builder.Append(innerIndent);
            builder.Append("INNER JOIN inserted i ON d.");
            builder.Append(Quote(new DbColumnName("DocumentId")));
            builder.Append(" = i.");
            builder.AppendLine(Quote(keyColumn));
            builder.Append(innerIndent);
            builder.Append("INNER JOIN deleted del ON del.");
            builder.Append(Quote(keyColumn));
            builder.Append(" = i.");
            builder.AppendLine(Quote(keyColumn));
            builder.Append(innerIndent);
            builder.Append("WHERE ");
            for (int i = 0; i < trigger.IdentityProjectionColumns.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(" OR ");
                }
                var col = Quote(trigger.IdentityProjectionColumns[i]);
                AppendMssqlNullSafeNotEqual(builder, "i", col, "del", col);
            }
            builder.AppendLine(";");

            builder.Append(indent);
            builder.AppendLine("END");
        }
    }

    /// <summary>
    /// Appends referential identity maintenance trigger body that maintains
    /// <c>dms.ReferentialIdentity</c> rows via UUIDv5 computation.
    /// </summary>
    private void AppendReferentialIdentityBody(StringBuilder builder, DbTriggerInfo trigger, string indent)
    {
        if (_dialectRules.Dialect == SqlDialect.Pgsql)
        {
            AppendPgsqlReferentialIdentityBody(builder, trigger, indent);
        }
        else
        {
            AppendMssqlReferentialIdentityBody(builder, trigger, indent);
        }
    }

    private void AppendPgsqlReferentialIdentityBody(
        StringBuilder builder,
        DbTriggerInfo trigger,
        string indent
    )
    {
        var refIdTable = Quote(DmsTableNames.ReferentialIdentity);

        // Primary referential identity
        AppendPgsqlReferentialIdentityBlock(
            builder,
            indent,
            refIdTable,
            trigger.ResourceKeyId!.Value,
            trigger.ProjectName!,
            trigger.ResourceName!,
            trigger.IdentityElements!
        );

        // Superclass alias
        if (trigger.SuperclassAlias is { } alias)
        {
            AppendPgsqlReferentialIdentityBlock(
                builder,
                indent,
                refIdTable,
                alias.ResourceKeyId,
                alias.ProjectName,
                alias.ResourceName,
                alias.IdentityElements
            );
        }
    }

    private void AppendPgsqlReferentialIdentityBlock(
        StringBuilder builder,
        string indent,
        string refIdTable,
        short resourceKeyId,
        string projectName,
        string resourceName,
        IReadOnlyList<IdentityElementMapping> identityElements
    )
    {
        if (identityElements.Count == 0)
        {
            throw new InvalidOperationException(
                $"ReferentialIdentityMaintenance trigger requires at least one identity element for resource '{resourceName}'."
            );
        }

        var uuidv5Func = FormatUuidv5FunctionName();

        // DELETE existing row
        builder.Append(indent);
        builder.Append("DELETE FROM ");
        builder.AppendLine(refIdTable);
        builder.Append(indent);
        builder.Append("WHERE ");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(" = NEW.");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(" AND ");
        builder.Append(Quote(new DbColumnName("ResourceKeyId")));
        builder.Append(" = ");
        builder.Append(resourceKeyId);
        builder.AppendLine(";");

        // INSERT new row with UUIDv5
        builder.Append(indent);
        builder.Append("INSERT INTO ");
        builder.Append(refIdTable);
        builder.Append(" (");
        builder.Append(Quote(new DbColumnName("ReferentialId")));
        builder.Append(", ");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(", ");
        builder.Append(Quote(new DbColumnName("ResourceKeyId")));
        builder.AppendLine(")");
        builder.Append(indent);
        builder.Append("VALUES (");
        builder.Append(uuidv5Func);
        builder.Append("('");
        builder.Append(Uuidv5Namespace);
        // Format intentionally matches ReferentialIdCalculator.ResourceInfoString: {ProjectName}{ResourceName}
        // with no separator — do not add one without updating the calculator.
        builder.Append("'::uuid, '");
        builder.Append(EscapeSqlLiteral(projectName));
        builder.Append(EscapeSqlLiteral(resourceName));
        builder.Append("' || ");
        AppendPgsqlIdentityHashExpression(builder, identityElements);
        builder.Append("), NEW.");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(", ");
        builder.Append(resourceKeyId);
        builder.AppendLine(");");
    }

    private void AppendPgsqlIdentityHashExpression(
        StringBuilder builder,
        IReadOnlyList<IdentityElementMapping> elements
    )
    {
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0)
                builder.Append(" || '#' || ");
            builder.Append("'$");
            builder.Append(EscapeSqlLiteral(elements[i].IdentityJsonPath));
            builder.Append("=' || NEW.");
            builder.Append(Quote(elements[i].Column));
            builder.Append("::text");
        }
    }

    private void AppendMssqlReferentialIdentityBody(
        StringBuilder builder,
        DbTriggerInfo trigger,
        string indent
    )
    {
        var refIdTable = Quote(DmsTableNames.ReferentialIdentity);

        // Primary referential identity
        AppendMssqlReferentialIdentityBlock(
            builder,
            indent,
            refIdTable,
            trigger.ResourceKeyId!.Value,
            trigger.ProjectName!,
            trigger.ResourceName!,
            trigger.IdentityElements!
        );

        // Superclass alias
        if (trigger.SuperclassAlias is { } alias)
        {
            AppendMssqlReferentialIdentityBlock(
                builder,
                indent,
                refIdTable,
                alias.ResourceKeyId,
                alias.ProjectName,
                alias.ResourceName,
                alias.IdentityElements
            );
        }
    }

    private void AppendMssqlReferentialIdentityBlock(
        StringBuilder builder,
        string indent,
        string refIdTable,
        short resourceKeyId,
        string projectName,
        string resourceName,
        IReadOnlyList<IdentityElementMapping> identityElements
    )
    {
        if (identityElements.Count == 0)
        {
            throw new InvalidOperationException(
                $"ReferentialIdentityMaintenance trigger requires at least one identity element for resource '{resourceName}'."
            );
        }

        var uuidv5Func = FormatUuidv5FunctionName();

        // DELETE existing row
        builder.Append(indent);
        builder.Append("DELETE FROM ");
        builder.AppendLine(refIdTable);
        builder.Append(indent);
        builder.Append("WHERE ");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(" IN (SELECT ");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(" FROM inserted) AND ");
        builder.Append(Quote(new DbColumnName("ResourceKeyId")));
        builder.Append(" = ");
        builder.Append(resourceKeyId);
        builder.AppendLine(";");

        // INSERT new rows from inserted table with UUIDv5
        builder.Append(indent);
        builder.Append("INSERT INTO ");
        builder.Append(refIdTable);
        builder.Append(" (");
        builder.Append(Quote(new DbColumnName("ReferentialId")));
        builder.Append(", ");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(", ");
        builder.Append(Quote(new DbColumnName("ResourceKeyId")));
        builder.AppendLine(")");
        builder.Append(indent);
        builder.Append("SELECT ");
        builder.Append(uuidv5Func);
        builder.Append("('");
        builder.Append(Uuidv5Namespace);
        // Format intentionally matches ReferentialIdCalculator.ResourceInfoString: {ProjectName}{ResourceName}
        // with no separator — do not add one without updating the calculator.
        builder.Append("', N'");
        builder.Append(EscapeSqlLiteral(projectName));
        builder.Append(EscapeSqlLiteral(resourceName));
        builder.Append("' + ");
        AppendMssqlIdentityHashExpression(builder, identityElements);
        builder.Append("), i.");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(", ");
        builder.AppendLine(resourceKeyId.ToString());
        builder.Append(indent);
        builder.AppendLine("FROM inserted i;");
    }

    private void AppendMssqlIdentityHashExpression(
        StringBuilder builder,
        IReadOnlyList<IdentityElementMapping> elements
    )
    {
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0)
                builder.Append(" + N'#' + ");
            builder.Append("N'$");
            builder.Append(EscapeSqlLiteral(elements[i].IdentityJsonPath));
            builder.Append("=' + CAST(i.");
            builder.Append(Quote(elements[i].Column));
            builder.Append(" AS nvarchar(max))");
        }
    }

    /// <summary>
    /// Appends abstract identity maintenance trigger body that maintains abstract identity
    /// tables from concrete resource root tables.
    /// </summary>
    private void AppendAbstractIdentityBody(StringBuilder builder, DbTriggerInfo trigger, string indent)
    {
        if (trigger.TargetTable is null || trigger.TargetColumnMappings is null)
        {
            throw new InvalidOperationException(
                $"AbstractIdentityMaintenance trigger '{trigger.Name.Value}' requires TargetTable and TargetColumnMappings."
            );
        }

        var validatedTargetTable = trigger.TargetTable.Value;
        var validatedMappings = trigger.TargetColumnMappings;

        if (_dialectRules.Dialect == SqlDialect.Pgsql)
        {
            AppendPgsqlAbstractIdentityBody(
                builder,
                trigger,
                indent,
                validatedTargetTable,
                validatedMappings
            );
        }
        else
        {
            AppendMssqlAbstractIdentityBody(
                builder,
                trigger,
                indent,
                validatedTargetTable,
                validatedMappings
            );
        }
    }

    private void AppendPgsqlAbstractIdentityBody(
        StringBuilder builder,
        DbTriggerInfo trigger,
        string indent,
        DbTableName targetTableName,
        IReadOnlyList<TriggerColumnMapping> mappings
    )
    {
        var targetTable = Quote(targetTableName);

        // INSERT ... ON CONFLICT DO UPDATE
        builder.Append(indent);
        builder.Append("INSERT INTO ");
        builder.Append(targetTable);
        builder.Append(" (");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        foreach (var mapping in mappings)
        {
            builder.Append(", ");
            builder.Append(Quote(mapping.TargetColumn));
        }
        builder.Append(", ");
        builder.Append(Quote(new DbColumnName("Discriminator")));
        builder.AppendLine(")");

        builder.Append(indent);
        builder.Append("VALUES (NEW.");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        foreach (var mapping in mappings)
        {
            builder.Append(", NEW.");
            builder.Append(Quote(mapping.SourceColumn));
        }
        builder.Append(", '");
        builder.Append(EscapeSqlLiteral(trigger.DiscriminatorValue ?? string.Empty));
        builder.AppendLine("')");

        builder.Append(indent);
        builder.Append("ON CONFLICT (");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.AppendLine(")");

        builder.Append(indent);
        builder.Append("DO UPDATE SET ");
        for (int i = 0; i < mappings.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(Quote(mappings[i].TargetColumn));
            builder.Append(" = EXCLUDED.");
            builder.Append(Quote(mappings[i].TargetColumn));
        }
        builder.AppendLine(";");
    }

    private void AppendMssqlAbstractIdentityBody(
        StringBuilder builder,
        DbTriggerInfo trigger,
        string indent,
        DbTableName targetTableName,
        IReadOnlyList<TriggerColumnMapping> mappings
    )
    {
        var targetTable = Quote(targetTableName);

        // MERGE statement
        builder.Append(indent);
        builder.Append("MERGE ");
        builder.Append(targetTable);
        builder.AppendLine(" AS t");
        builder.Append(indent);
        builder.Append("USING inserted AS s ON t.");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        builder.Append(" = s.");
        builder.AppendLine(Quote(new DbColumnName("DocumentId")));

        // WHEN MATCHED THEN UPDATE
        builder.Append(indent);
        builder.Append("WHEN MATCHED THEN UPDATE SET ");
        for (int i = 0; i < mappings.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append("t.");
            builder.Append(Quote(mappings[i].TargetColumn));
            builder.Append(" = s.");
            builder.Append(Quote(mappings[i].SourceColumn));
        }
        builder.AppendLine();

        // WHEN NOT MATCHED THEN INSERT
        builder.Append(indent);
        builder.Append("WHEN NOT MATCHED THEN INSERT (");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        foreach (var mapping in mappings)
        {
            builder.Append(", ");
            builder.Append(Quote(mapping.TargetColumn));
        }
        builder.Append(", ");
        builder.Append(Quote(new DbColumnName("Discriminator")));
        builder.AppendLine(")");

        builder.Append(indent);
        builder.Append("VALUES (s.");
        builder.Append(Quote(new DbColumnName("DocumentId")));
        foreach (var mapping in mappings)
        {
            builder.Append(", s.");
            builder.Append(Quote(mapping.SourceColumn));
        }
        builder.Append(", N'");
        builder.Append(EscapeSqlLiteral(trigger.DiscriminatorValue ?? string.Empty));
        builder.AppendLine("');");
    }

    /// <summary>
    /// Appends identity propagation fallback trigger body (MSSQL only) that cascades
    /// identity column updates to target tables when <c>ON UPDATE CASCADE</c> is not available.
    /// </summary>
    private void AppendIdentityPropagationBody(StringBuilder builder, DbTriggerInfo trigger, string indent)
    {
        if (trigger.TargetTable is null || trigger.TargetColumnMappings is null)
        {
            throw new InvalidOperationException(
                $"IdentityPropagationFallback trigger '{trigger.Name.Value}' requires TargetTable and TargetColumnMappings."
            );
        }

        var targetTable = Quote(trigger.TargetTable.Value);
        var fkColumn = trigger.KeyColumns[0];

        builder.Append(indent);
        builder.AppendLine("UPDATE t");
        builder.Append(indent);
        builder.Append("SET ");
        for (int i = 0; i < trigger.TargetColumnMappings.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append("t.");
            builder.Append(Quote(trigger.TargetColumnMappings[i].TargetColumn));
            builder.Append(" = i.");
            builder.Append(Quote(trigger.TargetColumnMappings[i].SourceColumn));
        }
        builder.AppendLine();

        builder.Append(indent);
        builder.Append("FROM ");
        builder.Append(targetTable);
        builder.AppendLine(" t");
        builder.Append(indent);
        builder.Append("INNER JOIN inserted i ON t.");
        builder.Append(Quote(fkColumn));
        builder.Append(" = i.");
        builder.AppendLine(Quote(fkColumn));
        // Correlate old/new rows of the trigger's owning table by DocumentId (the universal PK),
        // not by the FK column — the FK column is what changes, so it cannot be the join key.
        var documentIdCol = Quote(new DbColumnName("DocumentId"));
        builder.Append(indent);
        builder.Append("INNER JOIN deleted d ON d.");
        builder.Append(documentIdCol);
        builder.Append(" = i.");
        builder.AppendLine(documentIdCol);

        builder.Append(indent);
        builder.Append("WHERE ");
        for (int i = 0; i < trigger.TargetColumnMappings.Count; i++)
        {
            if (i > 0)
                builder.Append(" OR ");
            var col = Quote(trigger.TargetColumnMappings[i].SourceColumn);
            AppendMssqlNullSafeNotEqual(builder, "i", col, "d", col);
        }
        builder.AppendLine(";");
    }

    /// <summary>
    /// Appends a NULL-safe inequality comparison for MSSQL, equivalent to PostgreSQL's
    /// <c>IS DISTINCT FROM</c>. Emits <c>(left.col &lt;&gt; right.col OR (left.col IS NULL AND right.col IS NOT NULL) OR (left.col IS NOT NULL AND right.col IS NULL))</c>.
    /// </summary>
    private static void AppendMssqlNullSafeNotEqual(
        StringBuilder builder,
        string leftAlias,
        string quotedColumn,
        string rightAlias,
        string rightQuotedColumn
    )
    {
        builder.Append('(');
        builder.Append(leftAlias);
        builder.Append('.');
        builder.Append(quotedColumn);
        builder.Append(" <> ");
        builder.Append(rightAlias);
        builder.Append('.');
        builder.Append(rightQuotedColumn);
        builder.Append(" OR (");
        builder.Append(leftAlias);
        builder.Append('.');
        builder.Append(quotedColumn);
        builder.Append(" IS NULL AND ");
        builder.Append(rightAlias);
        builder.Append('.');
        builder.Append(rightQuotedColumn);
        builder.Append(" IS NOT NULL) OR (");
        builder.Append(leftAlias);
        builder.Append('.');
        builder.Append(quotedColumn);
        builder.Append(" IS NOT NULL AND ");
        builder.Append(rightAlias);
        builder.Append('.');
        builder.Append(rightQuotedColumn);
        builder.Append(" IS NULL))");
    }

    /// <summary>
    /// Resolves the SQL type for a column using explicit scalar type metadata or dialect defaults.
    /// </summary>
    private string ResolveColumnType(DbColumnModel column)
    {
        var scalarType = column.ScalarType;

        if (scalarType is null)
        {
            return column.Kind switch
            {
                ColumnKind.Ordinal => _dialectRules.ScalarTypeDefaults.Int32Type,
                _ => _dialectRules.ScalarTypeDefaults.Int64Type,
            };
        }

        return ResolveColumnType(scalarType);
    }

    /// <summary>
    /// Resolves the SQL type for a required scalar type.
    /// </summary>
    private string ResolveColumnType(RelationalScalarType scalarType)
    {
        return scalarType.Kind switch
        {
            ScalarKind.String => FormatStringType(scalarType),
            ScalarKind.Decimal => FormatDecimalType(scalarType),
            ScalarKind.Int32 => _dialectRules.ScalarTypeDefaults.Int32Type,
            ScalarKind.Int64 => _dialectRules.ScalarTypeDefaults.Int64Type,
            ScalarKind.Boolean => _dialectRules.ScalarTypeDefaults.BooleanType,
            ScalarKind.Date => _dialectRules.ScalarTypeDefaults.DateType,
            ScalarKind.DateTime => _dialectRules.ScalarTypeDefaults.DateTimeType,
            ScalarKind.Time => _dialectRules.ScalarTypeDefaults.TimeType,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scalarType.Kind),
                scalarType.Kind,
                "Unsupported scalar kind."
            ),
        };
    }

    /// <summary>
    /// Formats a string scalar type including a length specifier when present.
    /// For SQL Server, unbounded strings use (max) suffix.
    /// </summary>
    private string FormatStringType(RelationalScalarType scalarType)
    {
        if (scalarType.MaxLength is null)
        {
            // Unbounded string - use (max) for SQL Server
            return _dialectRules.Dialect == SqlDialect.Mssql
                ? $"{_dialectRules.ScalarTypeDefaults.StringType}(max)"
                : _dialectRules.ScalarTypeDefaults.StringType;
        }

        return $"{_dialectRules.ScalarTypeDefaults.StringType}({scalarType.MaxLength.Value})";
    }

    /// <summary>
    /// Formats a decimal scalar type including precision and scale when present.
    /// </summary>
    private string FormatDecimalType(RelationalScalarType scalarType)
    {
        if (scalarType.Decimal is null)
        {
            return _dialectRules.ScalarTypeDefaults.DecimalType;
        }

        var (precision, scale) = scalarType.Decimal.Value;
        return $"{_dialectRules.ScalarTypeDefaults.DecimalType}({precision},{scale})";
    }

    /// <summary>
    /// Formats a table constraint for inclusion within a <c>CREATE TABLE</c> statement.
    /// Returns null for FK constraints which are emitted separately in Phase 3.
    /// </summary>
    private string? FormatConstraint(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique =>
                $"CONSTRAINT {Quote(unique.Name)} UNIQUE ({FormatColumnList(unique.Columns)})",
            TableConstraint.ForeignKey => null, // Skip FKs, emit in Phase 3
            TableConstraint.AllOrNoneNullability allOrNone =>
                $"CONSTRAINT {Quote(allOrNone.Name)} CHECK ({FormatAllOrNoneCheck(allOrNone)})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(constraint),
                constraint,
                "Unsupported table constraint."
            ),
        };
    }

    /// <summary>
    /// Formats the expression for an all-or-none nullability check constraint.
    /// </summary>
    private string FormatAllOrNoneCheck(TableConstraint.AllOrNoneNullability constraint)
    {
        var dependencies = string.Join(
            " AND ",
            constraint.DependentColumns.Select(column => $"{Quote(column)} IS NOT NULL")
        );

        return $"({Quote(constraint.FkColumn)} IS NULL) OR ({dependencies})";
    }

    /// <summary>
    /// Formats <c>ON DELETE</c> and <c>ON UPDATE</c> clauses for a foreign key constraint.
    /// </summary>
    private string FormatReferentialActions(TableConstraint.ForeignKey foreignKey)
    {
        var deleteAction = FormatReferentialAction("DELETE", foreignKey.OnDelete);
        var updateAction = FormatReferentialAction("UPDATE", foreignKey.OnUpdate);

        return $"{deleteAction}{updateAction}";
    }

    /// <summary>
    /// Formats a referential action keyword clause when the action is not the dialect default.
    /// </summary>
    private static string FormatReferentialAction(string keyword, ReferentialAction action)
    {
        return action switch
        {
            ReferentialAction.NoAction => string.Empty,
            ReferentialAction.Cascade => $" ON {keyword} CASCADE",
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "Unsupported referential action."
            ),
        };
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
    /// Appends <c>CREATE VIEW</c> statements for abstract union views.
    /// </summary>
    private void AppendAbstractUnionViews(StringBuilder builder, IReadOnlyList<AbstractUnionViewInfo> views)
    {
        foreach (var viewInfo in views)
        {
            AppendCreateView(builder, viewInfo);
        }
    }

    /// <summary>
    /// Appends a <c>CREATE VIEW</c> statement for a single abstract union view.
    /// </summary>
    private void AppendCreateView(StringBuilder builder, AbstractUnionViewInfo viewInfo)
    {
        // Determine view creation pattern based on dialect
        var createKeyword = _dialectRules.Dialect switch
        {
            SqlDialect.Pgsql => "CREATE OR REPLACE VIEW",
            SqlDialect.Mssql => "CREATE OR ALTER VIEW",
            _ => throw new ArgumentOutOfRangeException(),
        };

        builder.Append(createKeyword);
        builder.Append(" ");
        builder.Append(Quote(viewInfo.ViewName));
        builder.AppendLine(" AS");

        // Emit UNION ALL arms
        for (int i = 0; i < viewInfo.UnionArmsInOrder.Count; i++)
        {
            var arm = viewInfo.UnionArmsInOrder[i];

            if (i > 0)
            {
                builder.AppendLine("UNION ALL");
            }

            builder.Append("SELECT ");

            // Emit projection expressions
            for (int j = 0; j < arm.ProjectionExpressionsInSelectOrder.Count; j++)
            {
                if (j > 0)
                    builder.Append(", ");

                var expr = arm.ProjectionExpressionsInSelectOrder[j];
                var outputColumn = viewInfo.OutputColumnsInSelectOrder[j];

                AppendProjectionExpression(builder, expr, outputColumn.ScalarType);
                builder.Append(" AS ");
                builder.Append(Quote(outputColumn.ColumnName));
            }

            builder.AppendLine();
            builder.Append("FROM ");
            builder.AppendLine(Quote(arm.FromTable));
        }

        builder.AppendLine(";");
        builder.AppendLine();
    }

    /// <summary>
    /// Appends a projection expression for an abstract union view select list.
    /// </summary>
    private void AppendProjectionExpression(
        StringBuilder builder,
        AbstractUnionViewProjectionExpression expr,
        RelationalScalarType targetType
    )
    {
        switch (expr)
        {
            case AbstractUnionViewProjectionExpression.SourceColumn sourceCol:
                builder.Append(Quote(sourceCol.ColumnName));
                break;

            case AbstractUnionViewProjectionExpression.StringLiteral literal:
                AppendStringLiteralWithCast(builder, literal.Value, targetType);
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
    /// Appends a string literal with dialect-specific CAST expression.
    /// </summary>
    private void AppendStringLiteralWithCast(
        StringBuilder builder,
        string value,
        RelationalScalarType targetType
    )
    {
        var sqlType = ResolveColumnType(targetType);

        if (_dialectRules.Dialect == SqlDialect.Pgsql)
        {
            // PostgreSQL: 'literal'::type
            builder.Append('\'');
            builder.Append(value.Replace("'", "''"));
            builder.Append("'::");
            builder.Append(sqlType);
        }
        else
        {
            // SQL Server: CAST(N'literal' AS type)
            builder.Append("CAST(N'");
            builder.Append(value.Replace("'", "''"));
            builder.Append("' AS ");
            builder.Append(sqlType);
            builder.Append(')');
        }
    }

    /// <summary>
    /// Resolves the primary key constraint name, falling back to a conventional default when unset.
    /// </summary>
    private static string ResolvePrimaryKeyConstraintName(DbTableModel table)
    {
        return string.IsNullOrWhiteSpace(table.Key.ConstraintName)
            ? $"PK_{table.Table.Name}"
            : table.Key.ConstraintName;
    }

    /// <summary>
    /// Quotes a raw identifier using the configured dialect rules.
    /// </summary>
    private string Quote(string identifier)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, identifier);
    }

    /// <summary>
    /// Quotes a schema name using the configured dialect rules.
    /// </summary>
    private string Quote(DbSchemaName schema)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, schema);
    }

    /// <summary>
    /// Quotes a fully-qualified table name using the configured dialect rules.
    /// </summary>
    private string Quote(DbTableName table)
    {
        return SqlIdentifierQuoter.QuoteTableName(_dialectRules.Dialect, table);
    }

    /// <summary>
    /// Quotes a column name using the configured dialect rules.
    /// </summary>
    private string Quote(DbColumnName column)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, column);
    }

    /// <summary>
    /// Quotes an index name using the configured dialect rules.
    /// </summary>
    private string Quote(DbIndexName index)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, index);
    }

    /// <summary>
    /// Quotes a trigger name using the configured dialect rules.
    /// </summary>
    private string Quote(DbTriggerName trigger)
    {
        return SqlIdentifierQuoter.QuoteIdentifier(_dialectRules.Dialect, trigger);
    }

    /// <summary>
    /// The UUIDv5 namespace used for referential identity computation.
    /// Matches <c>ReferentialIdCalculator.EdFiUuidv5Namespace</c>.
    /// </summary>
    private const string Uuidv5Namespace = "edf1edf1-3df1-3df1-3df1-3df1edf1edf1";

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
    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");
}
