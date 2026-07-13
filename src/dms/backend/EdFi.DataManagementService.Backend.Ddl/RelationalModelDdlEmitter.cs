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
    private static readonly DbColumnName DocumentIdColumn = RelationalNameConventions.DocumentIdColumnName;
    private static readonly DbColumnName ContentVersionColumn = new("ContentVersion");
    private static readonly DbColumnName ContentLastModifiedAtColumn = new("ContentLastModifiedAt");
    private static readonly DbColumnName IdentityVersionColumn = new("IdentityVersion");
    private static readonly DbColumnName IdentityLastModifiedAtColumn = new("IdentityLastModifiedAt");
    private static readonly DbColumnName ReferentialIdColumn = new("ReferentialId");
    private static readonly DbColumnName ResourceKeyIdColumn = new("ResourceKeyId");
    private static readonly DbColumnName DiscriminatorColumn = new("Discriminator");
    private static readonly DbColumnName DescriptorUriColumn = new("Uri");

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
            // The shared dms.Descriptor stamping trigger is derived into the model set so its
            // change-tracking attachment flows through manifests/planners, but dms.Descriptor is a core
            // table owned and rendered by CoreDdlEmitter. Exclude it here to avoid double emission.
            .TriggersInCreateOrder.Where(t => t.Table != DmsTableNames.Descriptor)
            .OrderBy(t => t.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(t => t.Table.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Name.Value, StringComparer.Ordinal)
            .ToList();

        var trackedChangeTables = modelSet
            .TrackedChangeTablesInNameOrder.OrderBy(t => t.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(t => t.Table.Name, StringComparer.Ordinal)
            .ToList();

        var authHierarchy = modelSet.AuthEdOrgHierarchy;

        // Phase 1: Schemas (includes the auth schema when the hierarchy is present, plus each distinct
        // tracked_changes_<project> schema required by the tracked-change inventory).
        var additionalSchemas = new List<DbSchemaName>();
        if (authHierarchy is { EntitiesInNameOrder.Count: > 0 })
        {
            additionalSchemas.Add(AuthNames.AuthSchema);
        }
        foreach (
            var trackedChangeSchema in trackedChangeTables
                .Select(t => t.Table.Schema)
                .Distinct()
                .OrderBy(s => s.Value, StringComparer.Ordinal)
        )
        {
            additionalSchemas.Add(trackedChangeSchema);
        }
        EmitSchemas(writer, schemas, additionalSchemas);

        // Phase 2: Tables (PK/UK/CHECK only, no cross-table FKs; includes auth and tracked-change tables)
        EmitTables(writer, concreteResources);
        EmitAuthTable(writer, authHierarchy);
        EmitTrackedChangeTables(writer, trackedChangeTables);

        // Phase 3: Abstract Identity Tables (must precede FKs that reference them)
        EmitAbstractIdentityTables(writer, abstractIdentityTables);

        // Phase 4: Foreign Keys (separate ALTER TABLE statements)
        EmitForeignKeys(writer, concreteResources, abstractIdentityTables);

        // Phase 5: Indexes
        EmitIndexes(writer, indexes);

        // Phase 6: Views (must precede Triggers per design)
        EmitAbstractUnionViews(writer, abstractUnionViews);
        EmitPeopleAuthViews(writer, authHierarchy, concreteResources);
        EmitReadChangesAuthViews(writer, authHierarchy, concreteResources, trackedChangeTables);

        var tableModelsByTableName = BuildTableModelLookup(concreteResources, abstractIdentityTables);
        var trackedChangeTablesByName = trackedChangeTables.ToDictionary(t => t.Table, t => t);

        // Phase 7: Triggers (includes auth hierarchy triggers)
        EmitTriggers(writer, triggers, tableModelsByTableName, trackedChangeTablesByName);

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
    /// Renders from <see cref="AuthObjectDefinitions.AuthEdOrgTable"/> so the manifest emitter and
    /// the SQL emitter share one source of truth for auth-table shape (DMS-1096 AC).
    /// </summary>
    private void EmitAuthTable(SqlWriter writer, AuthEdOrgHierarchy? authHierarchy)
    {
        if (authHierarchy is not { EntitiesInNameOrder.Count: > 0 })
        {
            return;
        }

        var def = AuthObjectDefinitions.AuthEdOrgTable;

        writer.AppendLine(_dialect.CreateTableHeader(def.Table));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            foreach (var column in def.Columns)
            {
                writer.AppendLine(
                    $"{_dialect.RenderColumnDefinition(column.Name, column.SqlType, column.IsNullable)},"
                );
            }
            writer.AppendLine(
                _dialect.RenderNamedPrimaryKeyClause(def.PrimaryKeyName, def.PrimaryKeyColumns)
            );
        }
        writer.AppendLine(");");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits <c>CREATE TABLE</c> statements for each derived tracked-change table
    /// (<c>tracked_changes_*</c>). Renders the value columns (each as an <c>Old*</c> / <c>New*</c>
    /// pair) in table order, then the fixed-by-role system columns, then the primary key. The inventory
    /// is already shortened and canonicalized, so rendering is purely mechanical (DMS-1177).
    /// </summary>
    private void EmitTrackedChangeTables(
        SqlWriter writer,
        IReadOnlyList<TrackedChangeTableInfo> trackedChangeTables
    )
    {
        foreach (var trackedTable in trackedChangeTables)
        {
            EmitCreateTrackedChangeTable(writer, trackedTable);
        }
    }

    /// <summary>
    /// Emits a single tracked-change <c>CREATE TABLE</c> statement.
    /// </summary>
    private void EmitCreateTrackedChangeTable(SqlWriter writer, TrackedChangeTableInfo trackedTable)
    {
        writer.AppendLine(_dialect.CreateTableHeader(trackedTable.Table));
        writer.AppendLine("(");

        var definitions = new List<string>();

        foreach (var column in trackedTable.ValueColumnsInTableOrder)
        {
            var type = _dialect.RenderColumnType(column.ScalarType);
            definitions.Add(
                _dialect.RenderColumnDefinition(column.OldColumnName, type, column.IsOldColumnNullable)
            );
            definitions.Add(
                _dialect.RenderColumnDefinition(column.NewColumnName, type, column.IsNewColumnNullable)
            );
        }

        foreach (var systemColumn in trackedTable.SystemColumns)
        {
            definitions.Add(RenderTrackedChangeSystemColumn(trackedTable.Table, systemColumn));
        }

        if (trackedTable.PrimaryKeyColumns.Count > 0)
        {
            definitions.Add(
                _dialect.RenderNamedPrimaryKeyClause(
                    ResolveTrackedChangePrimaryKeyName(trackedTable.Table),
                    trackedTable.PrimaryKeyColumns
                )
            );
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
    /// Renders a fixed-by-role tracked-change system column. The <c>Id</c> role has no
    /// <see cref="RelationalScalarType"/> and renders as the dialect UUID type; the <c>CreatedAt</c> role
    /// carries the current-UTC-timestamp default under a named <c>DF_*</c> constraint (consistent with the
    /// core DDL convention so SQL Server does not assign a system-generated default-constraint name); all
    /// other roles render directly from their scalar type.
    /// </summary>
    private string RenderTrackedChangeSystemColumn(
        DbTableName table,
        TrackedChangeSystemColumnInfo systemColumn
    )
    {
        var type = systemColumn.ScalarType is null
            ? _dialect.UuidColumnType
            : _dialect.RenderColumnType(systemColumn.ScalarType);

        if (systemColumn.Role == TrackedChangeSystemColumnRole.CreatedAt)
        {
            return _dialect.RenderColumnDefinitionWithNamedDefault(
                systemColumn.ColumnName,
                type,
                systemColumn.IsNullable,
                ResolveTrackedChangeDefaultName(table, systemColumn.ColumnName),
                _dialect.CurrentTimestampDefaultExpression
            );
        }

        return _dialect.RenderColumnDefinition(systemColumn.ColumnName, type, systemColumn.IsNullable);
    }

    /// <summary>
    /// Resolves the primary-key constraint name for a tracked-change table, applying the dialect
    /// identifier limit.
    /// </summary>
    private string ResolveTrackedChangePrimaryKeyName(DbTableName table)
    {
        return _dialect.Rules.ShortenIdentifier(RelationalNameConventions.TrackedChangePrimaryKeyName(table));
    }

    /// <summary>
    /// Resolves a named default-constraint name (<c>DF_&lt;schema&gt;_&lt;table&gt;_&lt;column&gt;</c>) for a
    /// tracked-change system column, applying the dialect identifier limit. Schema-qualified to stay unique
    /// across the per-project <c>tracked_changes_*</c> schemas, mirroring
    /// <see cref="ResolveTrackedChangePrimaryKeyName"/>.
    /// </summary>
    private string ResolveTrackedChangeDefaultName(DbTableName table, DbColumnName column)
    {
        return _dialect.Rules.ShortenIdentifier(
            RelationalNameConventions.TrackedChangeDefaultName(table, column)
        );
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

        if (TryResolveMirrorNamedDefault(table, column, out var mirrorConstraintName, out var mirrorDefault))
        {
            return _dialect.RenderColumnDefinitionWithNamedDefault(
                column.ColumnName,
                type,
                column.IsNullable,
                mirrorConstraintName,
                mirrorDefault
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

    /// <summary>
    /// Resolves the named default constraint for a synthesized change-version mirror column:
    /// <c>ContentVersion</c> defaults to a non-null sentinel and
    /// <c>ContentLastModifiedAt</c> defaults to the current UTC timestamp. Both use a
    /// <c>DF_&lt;Table&gt;_&lt;Column&gt;</c> constraint name (rendered by SQL Server; ignored by PostgreSQL),
    /// matching the <c>dms.Document</c> convention. The trigger overwrites these defaults at write time.
    /// </summary>
    private bool TryResolveMirrorNamedDefault(
        DbTableModel table,
        DbColumnModel column,
        out string constraintName,
        out string defaultExpression
    )
    {
        switch (column.Kind)
        {
            case ColumnKind.MirroredContentVersion:
                constraintName = BuildMirrorDefaultConstraintName(table, column);
                defaultExpression = "0";
                return true;
            case ColumnKind.MirroredContentLastModifiedAt:
                constraintName = BuildMirrorDefaultConstraintName(table, column);
                defaultExpression = _dialect.CurrentTimestampDefaultExpression;
                return true;
            default:
                constraintName = string.Empty;
                defaultExpression = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// Builds the <c>DF_&lt;Table&gt;_&lt;Column&gt;</c> default constraint name for a mirror column, applying
    /// the dialect identifier length limit. Resource-root table names can already sit at the dialect limit
    /// after identifier shortening, so the generated default-constraint name must be shortened too (SQL
    /// Server enforces a 128-character identifier limit on the named <c>CONSTRAINT</c>).
    /// </summary>
    private string BuildMirrorDefaultConstraintName(DbTableModel table, DbColumnModel column)
    {
        return SqlIdentifierShortening.ApplyDialectLimit(
            $"DF_{table.Table.Name}_{column.ColumnName.Value}",
            $"Default|{table.Table}|{column.ColumnName.Value}",
            _dialect.Rules
        );
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
    /// PK and UK indexes are skipped because their constraint definitions already create
    /// them; every other kind (FK-support, Explicit, Authorization) is emitted here.
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
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByTableName,
        IReadOnlyDictionary<DbTableName, TrackedChangeTableInfo> trackedChangeTablesByName
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
                EmitPgsqlTrigger(writer, trigger, tableModelsByTableName, trackedChangeTablesByName);
            }
            else
            {
                EmitMssqlTrigger(writer, trigger, tableModelsByTableName, trackedChangeTablesByName);
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
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByTableName,
        IReadOnlyDictionary<DbTableName, TrackedChangeTableInfo> trackedChangeTablesByName
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
        // Only root stamping paths read the captured stamp values (to assign NEW mirror
        // columns); child paths mirror through a CTE and need no locals.
        if (
            trigger.Parameters is TriggerKindParameters.DocumentStamping
            && IsRootDocumentStampingTrigger(
                trigger,
                RequireDocumentStampingTableModel(trigger, tableModelsByTableName),
                RequireMirrorStampTargetTable(trigger)
            )
        )
        {
            writer.AppendLine("DECLARE");
            using (writer.Indent())
            {
                writer.AppendLine("_stampedContentVersion bigint;");
                writer.AppendLine("_stampedContentLastModifiedAt timestamp with time zone;");
            }
        }
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            // The DELETE branch bumps ContentVersion via OLD and, for triggers with a
            // ChangeTracking attachment, inserts the tracked-change tombstone (DMS-1179).
            // On DELETE there is no NEW row, so it returns OLD and skips the normal body.
            TrackedChangeInsertPlan? trackedChangePlan = null;
            if (trigger.Parameters is TriggerKindParameters.DocumentStamping)
            {
                if (trigger.KeyColumns.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"DocumentStamping trigger '{trigger.Name.Value}' requires exactly one key column in the PgSQL path, but has {trigger.KeyColumns.Count}."
                    );
                }

                var deleteKeyColumn = trigger.KeyColumns[0];
                var tableModel = RequireDocumentStampingTableModel(trigger, tableModelsByTableName);
                var mirrorStampTargetTable = RequireMirrorStampTargetTable(trigger);
                var isRootDocumentStampingTrigger = IsRootDocumentStampingTrigger(
                    trigger,
                    tableModel,
                    mirrorStampTargetTable
                );
                // Built once per trigger and shared by the DELETE-branch tombstone below and
                // the UPDATE-path body (EmitPgsqlDocumentStampingBody). Built unconditionally
                // so attachment inconsistencies throw even when neither branch emits.
                trackedChangePlan = TryBuildTrackedChangePlan(trigger, tableModel, trackedChangeTablesByName);
                writer.AppendLine("IF TG_OP = 'DELETE' THEN");
                using (writer.Indent())
                {
                    EmitPgsqlDocumentContentStampUpdate(
                        writer,
                        Quote(DmsTableNames.Document),
                        FormatSequenceName(),
                        deleteKeyColumn,
                        mirrorStampTargetTable,
                        "OLD",
                        assignToNewMirrorColumns: false,
                        updateMirrorTable: !isRootDocumentStampingTrigger
                    );
                    if (trackedChangePlan is not null)
                    {
                        TrackedChangeTriggerBodyEmitter.EmitPgsqlTombstoneInsert(
                            writer,
                            _dialect,
                            trackedChangePlan,
                            deleteKeyColumn
                        );
                    }
                    writer.AppendLine("RETURN OLD;");
                }
                writer.AppendLine("END IF;");
            }

            EmitTriggerBody(
                writer,
                trigger,
                tableModelsByTableName,
                trackedChangeTablesByName,
                trackedChangePlan
            );
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

        // DELETE is part of the trigger event list because the DELETE branch stamps
        // ContentVersion and emits tracked-change tombstones (DMS-1179).
        var pgsqlTriggerEvent = trigger.Parameters switch
        {
            TriggerKindParameters.DocumentStamping => "BEFORE INSERT OR UPDATE OR DELETE ON ",
            // Referential identity triggers must be AFTER so that GENERATED ALWAYS STORED
            // columns (e.g. key-unified alias columns) are already recomputed when the
            // trigger body reads them.
            TriggerKindParameters.ReferentialIdentityMaintenance => "AFTER INSERT OR UPDATE ON ",
            _ => "BEFORE INSERT OR UPDATE ON ",
        };
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
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByTableName,
        IReadOnlyDictionary<DbTableName, TrackedChangeTableInfo> trackedChangeTablesByName
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
            TriggerKindParameters.DocumentStamping => "AFTER INSERT, UPDATE, DELETE",
            _ => "AFTER INSERT, UPDATE",
        };
        writer.AppendLine(mssqlTriggerEvent);
        writer.AppendLine("AS");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("SET NOCOUNT ON;");
            // SQL Server emits DELETE tombstones inside EmitMssqlDocumentStampingBody, where
            // the tracked-change plan is built. PostgreSQL prebuilds it in the function
            // wrapper because the PostgreSQL DELETE branch is emitted there.
            EmitTriggerBody(writer, trigger, tableModelsByTableName, trackedChangeTablesByName, null);
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
            _ => throw new ArgumentOutOfRangeException(
                nameof(auth),
                auth.TriggerEvent,
                "Unsupported auth hierarchy trigger event."
            ),
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
            _ => throw new ArgumentOutOfRangeException(
                nameof(auth),
                auth.TriggerEvent,
                "Unsupported auth hierarchy trigger event."
            ),
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
    /// The <paramref name="pgsqlTrackedChangePlan"/> is the prebuilt PG tracked-change plan;
    /// it is only meaningful on the PostgreSQL document-stamping path and is null for MSSQL callers.
    /// </summary>
    private void EmitTriggerBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByTableName,
        IReadOnlyDictionary<DbTableName, TrackedChangeTableInfo> trackedChangeTablesByName,
        TrackedChangeInsertPlan? pgsqlTrackedChangePlan
    )
    {
        switch (trigger.Parameters)
        {
            case TriggerKindParameters.DocumentStamping:
                var tableModel = RequireDocumentStampingTableModel(trigger, tableModelsByTableName);
                EmitDocumentStampingBody(
                    writer,
                    trigger,
                    tableModel,
                    trackedChangeTablesByName,
                    pgsqlTrackedChangePlan
                );
                break;
            case TriggerKindParameters.ReferentialIdentityMaintenance refId:
                if (!tableModelsByTableName.TryGetValue(trigger.Table, out var refIdTableModel))
                {
                    throw new InvalidOperationException(
                        $"ReferentialIdentityMaintenance trigger '{trigger.Name.Value}' requires a table model for "
                            + $"'{trigger.Table.Schema.Value}.{trigger.Table.Name}', but none was found."
                    );
                }
                EmitReferentialIdentityBody(writer, trigger, refIdTableModel, refId);
                break;
            case TriggerKindParameters.AbstractIdentityMaintenance abstractId:
                if (!tableModelsByTableName.TryGetValue(trigger.Table, out var abstractIdTableModel))
                {
                    throw new InvalidOperationException(
                        $"AbstractIdentityMaintenance trigger '{trigger.Name.Value}' requires a table model for "
                            + $"'{trigger.Table.Schema.Value}.{trigger.Table.Name}', but none was found."
                    );
                }
                EmitAbstractIdentityBody(writer, trigger, abstractIdTableModel, abstractId);
                break;
            case TriggerKindParameters.AuthHierarchyMaintenance:
                // Auth triggers are handled by dedicated scaffolding methods
                // (EmitPgsqlAuthTrigger / EmitMssqlAuthTrigger), not this switch.
                throw new InvalidOperationException(
                    $"Auth hierarchy trigger '{trigger.Name.Value}' should not reach EmitTriggerBody."
                );
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(trigger),
                    trigger.Parameters,
                    "Unsupported trigger kind parameters type."
                );
        }
    }

    /// <summary>
    /// Emits the document stamping trigger body: INSERT/UPDATE representation stamping,
    /// <c>IdentityVersion</c> stamping on root tables with identity projection columns,
    /// and — for triggers with a ChangeTracking attachment — tracked-change tombstone and
    /// key-change emission on DELETE (DMS-1179).
    /// </summary>
    private void EmitDocumentStampingBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        IReadOnlyDictionary<DbTableName, TrackedChangeTableInfo> trackedChangeTablesByName,
        TrackedChangeInsertPlan? pgsqlTrackedChangePlan
    )
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
        var mirrorStampTargetTable = RequireMirrorStampTargetTable(trigger);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlDocumentStampingBody(
                writer,
                trigger,
                tableModel,
                documentTable,
                sequenceName,
                keyColumn,
                mirrorStampTargetTable,
                pgsqlTrackedChangePlan
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
                keyColumn,
                mirrorStampTargetTable,
                trackedChangeTablesByName
            );
        }
    }

    /// <summary>
    /// Resolves the tracked-change insert plan for a <see cref="TriggerKindParameters.DocumentStamping"/>
    /// trigger that carries a <see cref="TrackedChangeAttachment"/>, or returns <c>null</c> when the
    /// trigger is not a stamping trigger or has no attachment. Validation runs for every attached
    /// trigger regardless of which body branches are ultimately emitted.
    /// </summary>
    /// <param name="trigger">The trigger whose parameters may carry a tracked-change attachment.</param>
    /// <param name="tableModel">The live source table model used to resolve value column sources.</param>
    /// <param name="trackedChangeTablesByName">The tracked-change inventory keyed by table name.</param>
    /// <returns>The resolved insert plan, or <c>null</c> when the trigger is unattached.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the attachment references a table absent from the tracked-change inventory; when the
    /// tracked table is <see cref="TrackedChangeTableKind.Resource"/> but the trigger has no identity
    /// projection columns (key-change detection would be impossible); or when
    /// <see cref="TrackedChangeTriggerBodyEmitter.BuildPlan"/> finds an inventory inconsistency.
    /// </exception>
    private static TrackedChangeInsertPlan? TryBuildTrackedChangePlan(
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        IReadOnlyDictionary<DbTableName, TrackedChangeTableInfo> trackedChangeTablesByName
    )
    {
        if (
            trigger.Parameters
            is not TriggerKindParameters.DocumentStamping { ChangeTracking: { } attachment }
        )
        {
            return null;
        }

        if (!trackedChangeTablesByName.TryGetValue(attachment.TrackedChangeTable, out var tableInfo))
        {
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{trigger.Name.Value}' references tracked-change table "
                    + $"'{attachment.TrackedChangeTable.Schema.Value}.{attachment.TrackedChangeTable.Name}', "
                    + "but no such table exists in the tracked-change inventory."
            );
        }

        if (tableInfo.Kind == TrackedChangeTableKind.Resource && trigger.IdentityProjectionColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{trigger.Name.Value}' is attached to Resource-kind "
                    + $"tracked-change table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}' "
                    + "but has empty IdentityProjectionColumns; key-change detection requires a "
                    + "non-empty identity workset."
            );
        }

        var mirrorTarget = RequireMirrorStampTargetTable(trigger);
        if (!IsRootDocumentStampingTrigger(trigger, tableModel, mirrorTarget))
        {
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{trigger.Name.Value}' is attached to tracked-change table "
                    + $"'{attachment.TrackedChangeTable.Schema.Value}.{attachment.TrackedChangeTable.Name}' "
                    + "but is not a root document-stamping trigger; only root triggers may carry a "
                    + "tracked-change attachment."
            );
        }

        return TrackedChangeTriggerBodyEmitter.BuildPlan(tableInfo, tableModel);
    }

    private static DbTableName RequireMirrorStampTargetTable(DbTriggerInfo trigger)
    {
        if (trigger.MirrorStampTargetTable is not { } mirrorStampTargetTable)
        {
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{trigger.Name.Value}' requires a non-null MirrorStampTargetTable."
            );
        }

        return mirrorStampTargetTable;
    }

    private static DbTableModel RequireDocumentStampingTableModel(
        DbTriggerInfo trigger,
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByTableName
    )
    {
        if (tableModelsByTableName.TryGetValue(trigger.Table, out var tableModel))
        {
            return tableModel;
        }

        throw new InvalidOperationException(
            $"DocumentStamping trigger '{trigger.Name.Value}' requires a table model for '{trigger.Table.Schema.Value}.{trigger.Table.Name}', but none was found."
        );
    }

    private static bool IsRootDocumentStampingTrigger(
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        DbTableName mirrorStampTargetTable
    )
    {
        var tableKindIdentifiesRoot = tableModel.IdentityMetadata.TableKind == DbTableKind.Root;
        var mirrorTargetIdentifiesRoot = trigger.Table.Equals(mirrorStampTargetTable);

        if (tableModel.IdentityMetadata.TableKind == DbTableKind.Unspecified)
        {
            return mirrorTargetIdentifiesRoot;
        }

        if (tableKindIdentifiesRoot != mirrorTargetIdentifiesRoot)
        {
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{trigger.Name.Value}' has inconsistent root classification: "
                    + $"table kind is '{tableModel.IdentityMetadata.TableKind}', but MirrorStampTargetTable is "
                    + $"'{mirrorStampTargetTable.Schema.Value}.{mirrorStampTargetTable.Name}'."
            );
        }

        return tableKindIdentifiesRoot;
    }

    private void EmitPgsqlDocumentStampingBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn,
        DbTableName mirrorStampTargetTable,
        TrackedChangeInsertPlan? trackedChangePlan
    )
    {
        var storedColumns = GetStoredColumnsForDocumentStamping(tableModel, trigger.Name.Value);
        var isRootDocumentStampingTrigger = IsRootDocumentStampingTrigger(
            trigger,
            tableModel,
            mirrorStampTargetTable
        );

        // Skip successful no-op UPDATEs that do not change any stored row values.
        writer.Append("IF TG_OP = 'UPDATE' AND NOT (");
        EmitPgsqlValueDiffDisjunction(writer, storedColumns);
        writer.AppendLine(") THEN");
        using (writer.Indent())
        {
            writer.AppendLine("RETURN NEW;");
        }
        writer.AppendLine("END IF;");

        // Root-document INSERTs and changed UPDATEs capture the dms.Document stamp and mirror the same values into NEW.
        if (isRootDocumentStampingTrigger)
        {
            writer.AppendLine("IF TG_OP = 'INSERT' THEN");
            using (writer.Indent())
            {
                EmitPgsqlExistingDocumentContentStampRead(
                    writer,
                    documentTable,
                    keyColumn,
                    "NEW",
                    assignToNewMirrorColumns: true
                );
            }
            writer.AppendLine("ELSIF TG_OP = 'UPDATE' THEN");
            using (writer.Indent())
            {
                EmitPgsqlDocumentContentStampUpdate(
                    writer,
                    documentTable,
                    sequenceName,
                    keyColumn,
                    mirrorStampTargetTable,
                    "NEW",
                    assignToNewMirrorColumns: true,
                    updateMirrorTable: false
                );
            }
            writer.AppendLine("END IF;");
        }
        else
        {
            EmitPgsqlDocumentContentStampUpdate(
                writer,
                documentTable,
                sequenceName,
                keyColumn,
                mirrorStampTargetTable,
                "NEW",
                assignToNewMirrorColumns: false,
                updateMirrorTable: true
            );
        }

        // IdentityVersion stamp for root tables with identity projection columns
        if (trigger.IdentityProjectionColumns.Count > 0)
        {
            // PostgreSQL: IS DISTINCT FROM provides null-safe inequality comparison.
            // (NULL IS DISTINCT FROM NULL) → false, (NULL IS DISTINCT FROM value) → true.
            // Equivalent to MssqlTriggerDiffEmitter.EmitNullSafeNotEqual which expands to:
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

                // Resource-kind tracked-change attachments record a key-change row for the same
                // identity-diff workset that bumped IdentityVersion above. ConcreteAbstract tables
                // are tombstone-only by design.
                if (
                    trackedChangePlan is not null
                    && trackedChangePlan.Table.Kind == TrackedChangeTableKind.Resource
                )
                {
                    TrackedChangeTriggerBodyEmitter.EmitPgsqlKeyChangeInsert(
                        writer,
                        _dialect,
                        trackedChangePlan,
                        keyColumn
                    );
                }
            }

            writer.AppendLine("END IF;");
        }
    }

    private void EmitPgsqlExistingDocumentContentStampRead(
        SqlWriter writer,
        string documentTable,
        DbColumnName keyColumn,
        string sourceRowAlias,
        bool assignToNewMirrorColumns
    )
    {
        writer.Append("SELECT ");
        writer.Append(Quote(ContentVersionColumn));
        writer.Append(", ");
        writer.Append(Quote(ContentLastModifiedAtColumn));
        writer.AppendLine();
        // STRICT: a missing dms.Document row must fail with a clear no-rows error here,
        // not as a misleading not-null violation when the NULL locals reach NEW.
        writer.AppendLine("INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt");
        writer.Append("FROM ");
        writer.AppendLine(documentTable);
        writer.Append("WHERE ");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(" = ");
        writer.Append(sourceRowAlias);
        writer.Append(".");
        writer.Append(Quote(keyColumn));
        writer.AppendLine(";");

        if (assignToNewMirrorColumns)
        {
            writer.Append("NEW.");
            writer.Append(Quote(ContentVersionColumn));
            writer.AppendLine(" := _stampedContentVersion;");
            writer.Append("NEW.");
            writer.Append(Quote(ContentLastModifiedAtColumn));
            writer.AppendLine(" := _stampedContentLastModifiedAt;");
        }
    }

    private void EmitPgsqlDocumentContentStampUpdate(
        SqlWriter writer,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn,
        DbTableName mirrorStampTargetTable,
        string sourceRowAlias,
        bool assignToNewMirrorColumns,
        bool updateMirrorTable
    )
    {
        if (assignToNewMirrorColumns || !updateMirrorTable)
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
            writer.Append(" = ");
            writer.Append(sourceRowAlias);
            writer.Append(".");
            writer.Append(Quote(keyColumn));

            if (assignToNewMirrorColumns)
            {
                writer.AppendLine();
                writer.Append("RETURNING ");
                writer.Append(Quote(ContentVersionColumn));
                writer.Append(", ");
                writer.Append(Quote(ContentLastModifiedAtColumn));
                // STRICT: a missing dms.Document row must fail with a clear no-rows error
                // here, not as a misleading not-null violation when the NULL locals reach NEW.
                writer.AppendLine(" INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt;");
                writer.Append("NEW.");
                writer.Append(Quote(ContentVersionColumn));
                writer.AppendLine(" := _stampedContentVersion;");
                writer.Append("NEW.");
                writer.Append(Quote(ContentLastModifiedAtColumn));
                writer.AppendLine(" := _stampedContentLastModifiedAt;");
            }
            else
            {
                writer.AppendLine(";");
            }

            return;
        }

        writer.AppendLine("WITH stamped AS (");
        using (writer.Indent())
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
            writer.Append(" = ");
            writer.Append(sourceRowAlias);
            writer.Append(".");
            writer.Append(Quote(keyColumn));
            writer.AppendLine();
            writer.Append("AND EXISTS (");
            writer.Append("SELECT 1 FROM ");
            writer.Append(Quote(mirrorStampTargetTable));
            writer.Append(" r WHERE r.");
            writer.Append(Quote(DocumentIdColumn));
            writer.Append(" = ");
            writer.Append(sourceRowAlias);
            writer.Append(".");
            writer.Append(Quote(keyColumn));
            writer.AppendLine(")");
            writer.Append("RETURNING ");
            writer.Append(Quote(DocumentIdColumn));
            writer.Append(", ");
            writer.Append(Quote(ContentVersionColumn));
            writer.Append(", ");
            writer.Append(Quote(ContentLastModifiedAtColumn));
            writer.AppendLine();
        }
        writer.AppendLine(")");
        writer.Append("UPDATE ");
        writer.Append(Quote(mirrorStampTargetTable));
        writer.AppendLine(" r");
        writer.Append("SET ");
        writer.Append(Quote(ContentVersionColumn));
        writer.Append(" = stamped.");
        writer.Append(Quote(ContentVersionColumn));
        writer.Append(", ");
        writer.Append(Quote(ContentLastModifiedAtColumn));
        writer.Append(" = stamped.");
        writer.Append(Quote(ContentLastModifiedAtColumn));
        writer.AppendLine();
        writer.AppendLine("FROM stamped");
        writer.Append("WHERE r.");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(" = stamped.");
        writer.Append(Quote(DocumentIdColumn));
        writer.AppendLine(";");
    }

    private void EmitMssqlDocumentStampingBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn,
        DbTableName mirrorStampTargetTable,
        IReadOnlyDictionary<DbTableName, TrackedChangeTableInfo> trackedChangeTablesByName
    )
    {
        var tableKeyColumns = GetKeyColumnsForDocumentStamping(tableModel, trigger.Name.Value);
        var storedColumns = GetStoredColumnsForDocumentStamping(tableModel, trigger.Name.Value);
        var quotedKeyColumn = Quote(keyColumn);
        var quotedProbeKeyColumn = Quote(tableKeyColumns[0]);
        var mirrorStampTarget = Quote(mirrorStampTargetTable);
        var isRootDocumentStampingTrigger = IsRootDocumentStampingTrigger(
            trigger,
            tableModel,
            mirrorStampTargetTable
        );

        writer.AppendLine("DECLARE @stamped TABLE (");
        using (writer.Indent())
        {
            writer.AppendLine("[DocumentId] bigint NOT NULL PRIMARY KEY,");
            writer.AppendLine("[ContentVersion] bigint NOT NULL,");
            writer.AppendLine("[ContentLastModifiedAt] datetime2(7) NOT NULL");
        }
        writer.AppendLine(");");

        if (isRootDocumentStampingTrigger)
        {
            writer.AppendLine(
                "INSERT INTO @stamped ([DocumentId], [ContentVersion], [ContentLastModifiedAt])"
            );
            writer.Append("SELECT d.");
            writer.Append(Quote(DocumentIdColumn));
            writer.Append(", d.");
            writer.Append(Quote(ContentVersionColumn));
            writer.Append(", d.");
            writer.AppendLine(Quote(ContentLastModifiedAtColumn));
            writer.Append("FROM ");
            writer.Append(documentTable);
            writer.AppendLine(" d");
            writer.Append("INNER JOIN inserted i ON d.");
            writer.Append(Quote(DocumentIdColumn));
            writer.Append(" = i.");
            writer.AppendLine(quotedKeyColumn);
            writer.Append("LEFT JOIN deleted del ON ");
            EmitMssqlJoinConjunction(writer, "del", "i", tableKeyColumns);
            writer.AppendLine();
            writer.Append("WHERE del.");
            writer.Append(quotedProbeKeyColumn);
            writer.AppendLine(" IS NULL;");
        }

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
            if (isRootDocumentStampingTrigger)
            {
                writer.Append(" IS NOT NULL AND (");
                EmitMssqlColumnValueDiffDisjunction(writer, tableModel, "i", "del", storedColumns);
                writer.Append(")");
            }
            else
            {
                writer.Append(" IS NULL OR ");
                EmitMssqlColumnValueDiffDisjunction(writer, tableModel, "i", "del", storedColumns);
            }
            writer.AppendLine();
            // Root branches are disjoint (inserted-side takes changed updates, deleted-side
            // pure deletes), so UNION ALL skips the dedup sort and the diff disjunction runs
            // once per row. Child rows map many-to-one onto the root document, so the child
            // shape keeps UNION's dedup and the deleted-side diff for changed updates.
            writer.AppendLine(isRootDocumentStampingTrigger ? "UNION ALL" : "UNION");
            writer.Append("SELECT del.");
            writer.AppendLine(quotedKeyColumn);
            writer.AppendLine("FROM deleted del");
            writer.Append("LEFT JOIN inserted i ON ");
            EmitMssqlJoinConjunction(writer, "i", "del", tableKeyColumns);
            writer.AppendLine();
            writer.Append("WHERE i.");
            writer.Append(quotedProbeKeyColumn);
            if (isRootDocumentStampingTrigger)
            {
                writer.AppendLine(" IS NULL");
            }
            else
            {
                writer.Append(" IS NULL OR ");
                EmitMssqlColumnValueDiffDisjunction(writer, tableModel, "i", "del", storedColumns);
                writer.AppendLine();
            }
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
        writer.AppendLine(
            "OUTPUT inserted.[DocumentId], inserted.[ContentVersion], inserted.[ContentLastModifiedAt] INTO @stamped"
        );
        writer.Append("FROM ");
        writer.Append(documentTable);
        writer.AppendLine(" d");
        writer.Append("INNER JOIN affectedDocs a ON d.");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(" = a.");
        writer.Append(quotedKeyColumn);
        if (!isRootDocumentStampingTrigger)
        {
            writer.AppendLine();
            writer.Append("INNER JOIN ");
            writer.Append(mirrorStampTarget);
            writer.Append(" stampTarget ON stampTarget.");
            writer.Append(Quote(DocumentIdColumn));
            writer.Append(" = a.");
            writer.Append(quotedKeyColumn);
            writer.AppendLine(";");
        }
        else
        {
            writer.AppendLine(";");
        }

        // The guard bounds direct recursion: without it the mirror self-UPDATE re-fires
        // this trigger even with an empty workset (statement triggers fire on 0 rows),
        // which recurses to the nesting limit on databases with RECURSIVE_TRIGGERS ON.
        writer.AppendLine("IF EXISTS (SELECT 1 FROM @stamped)");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("UPDATE r");
            writer.Append("SET r.");
            writer.Append(Quote(ContentVersionColumn));
            writer.Append(" = s.");
            writer.Append(Quote(ContentVersionColumn));
            writer.AppendLine(",");
            writer.Append("    r.");
            writer.Append(Quote(ContentLastModifiedAtColumn));
            writer.Append(" = s.");
            writer.Append(Quote(ContentLastModifiedAtColumn));
            writer.AppendLine();
            writer.Append("FROM ");
            writer.Append(mirrorStampTarget);
            writer.AppendLine(" r");
            writer.Append("INNER JOIN @stamped s ON s.");
            writer.Append(Quote(DocumentIdColumn));
            writer.Append(" = r.");
            writer.Append(Quote(DocumentIdColumn));
            writer.AppendLine(";");
        }
        writer.AppendLine("END");

        // Built unconditionally so attachment inconsistencies (unknown tracked table, empty identity
        // workset on a Resource-kind attachment) throw even when neither tracked-change block below
        // is emitted.
        var trackedChangePlan = TryBuildTrackedChangePlan(trigger, tableModel, trackedChangeTablesByName);
        if (trackedChangePlan is not null)
        {
            writer.AppendLine("IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                TrackedChangeTriggerBodyEmitter.EmitMssqlTombstoneInsert(
                    writer,
                    _dialect,
                    trackedChangePlan,
                    keyColumn
                );
            }
            writer.AppendLine("END");
        }

        // IdentityVersion stamp for root tables with identity projection columns
        if (trigger.IdentityProjectionColumns.Count > 0)
        {
            if (
                trackedChangePlan is not null
                && trackedChangePlan.Table.Kind == TrackedChangeTableKind.Resource
            )
            {
                EmitMssqlIdentityStampWithKeyChange(
                    writer,
                    trigger,
                    tableModel,
                    documentTable,
                    sequenceName,
                    keyColumn,
                    trackedChangePlan
                );
            }
            else
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
                    EmitMssqlIdentityVersionUpdate(
                        writer,
                        trigger,
                        tableModel,
                        documentTable,
                        sequenceName,
                        keyColumn,
                        captureIdentityChangedDocs: false
                    );
                }

                writer.AppendLine("END");
            }
        }
    }

    /// <summary>
    /// Emits the SQL Server IdentityVersion stamp for a stamping trigger attached to a
    /// <see cref="TrackedChangeTableKind.Resource"/> tracked-change table, capturing the
    /// identity-changed workset into <c>@identityChangedDocs</c> via an OUTPUT clause and rendering the
    /// key-change INSERT from it. Gated only by row-set existence plus the authoritative null-safe
    /// value diff — never by <c>UPDATE(column)</c>, which reports SET-clause membership rather than
    /// value change (DMS-1179 AC bans <c>UPDATE(column)</c> for key-change eligibility).
    /// </summary>
    private void EmitMssqlIdentityStampWithKeyChange(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn,
        TrackedChangeInsertPlan trackedChangePlan
    )
    {
        writer.AppendLine("IF EXISTS (SELECT 1 FROM deleted) AND EXISTS (SELECT 1 FROM inserted)");
        writer.AppendLine("BEGIN");

        using (writer.Indent())
        {
            writer.AppendLine(
                "DECLARE @identityChangedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY, [ContentVersion] bigint NOT NULL);"
            );
            EmitMssqlIdentityVersionUpdate(
                writer,
                trigger,
                tableModel,
                documentTable,
                sequenceName,
                keyColumn,
                captureIdentityChangedDocs: true
            );

            TrackedChangeTriggerBodyEmitter.EmitMssqlKeyChangeInsert(
                writer,
                _dialect,
                trackedChangePlan,
                keyColumn
            );
        }

        writer.AppendLine("END");
    }

    /// <summary>
    /// Emits the core <c>UPDATE d SET d.[IdentityVersion] = ... FROM ... INNER JOIN inserted ...
    /// INNER JOIN deleted ... WHERE &lt;null-safe diff&gt;</c> statement, optionally appending an
    /// <c>OUTPUT inserted.[DocumentId], inserted.[ContentVersion] INTO @identityChangedDocs</c> line
    /// when <paramref name="captureIdentityChangedDocs"/> is <see langword="true"/>.
    /// </summary>
    private void EmitMssqlIdentityVersionUpdate(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        string documentTable,
        string sequenceName,
        DbColumnName keyColumn,
        bool captureIdentityChangedDocs
    )
    {
        writer.AppendLine("UPDATE d");
        writer.Append("SET d.");
        writer.Append(Quote(IdentityVersionColumn));
        writer.Append(" = NEXT VALUE FOR ");
        writer.Append(sequenceName);
        writer.Append(", d.");
        writer.Append(Quote(IdentityLastModifiedAtColumn));
        writer.AppendLine(" = sysutcdatetime()");
        if (captureIdentityChangedDocs)
        {
            writer.AppendLine(
                "OUTPUT inserted.[DocumentId], inserted.[ContentVersion] INTO @identityChangedDocs"
            );
        }
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
            EmitMssqlColumnValueDiffPredicate(
                writer,
                tableModel,
                "i",
                "del",
                trigger.IdentityProjectionColumns[i]
            );
        }
        writer.AppendLine(";");
    }

    /// <summary>
    /// Emits referential identity maintenance trigger body that maintains
    /// <c>dms.ReferentialIdentity</c> rows via UUIDv5 computation.
    /// </summary>
    private void EmitReferentialIdentityBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
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
            EmitMssqlReferentialIdentityBody(writer, trigger.IdentityProjectionColumns, tableModel, refId);
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
            writer.Append("'");
            writer.Append(SqlDialectBase.EscapeSingleQuote(elements[i].IdentityJsonPath));
            writer.Append("=' || ");
            EmitPgsqlIdentityElementToText(writer, elements[i]);
        }
    }

    /// <summary>
    /// Emits a canonical text conversion for an identity column value in PostgreSQL.
    /// Delegates to <see cref="DialectIdentityTextFormatter.PgsqlColumnToText"/> so the
    /// trigger and the runtime reference-lookup verification SQL share one source of truth.
    /// </summary>
    private void EmitPgsqlIdentityElementToText(SqlWriter writer, IdentityElementMapping element)
    {
        var columnExpression = $"NEW.{Quote(element.Column)}";

        if (element.IsDescriptorReference)
        {
            writer.Append("lower((SELECT descriptor.");
            writer.Append(Quote(DescriptorUriColumn));
            writer.Append(" FROM ");
            writer.Append(Quote(DmsTableNames.Descriptor));
            writer.Append(" descriptor WHERE descriptor.");
            writer.Append(Quote(DocumentIdColumn));
            writer.Append(" = ");
            writer.Append(columnExpression);
            writer.Append("))");
            return;
        }

        writer.Append(DialectIdentityTextFormatter.PgsqlColumnToText(columnExpression, element.ScalarType));
    }

    private void EmitMssqlReferentialIdentityBody(
        SqlWriter writer,
        IReadOnlyList<DbColumnName> identityProjectionColumns,
        DbTableModel tableModel,
        TriggerKindParameters.ReferentialIdentityMaintenance refId
    )
    {
        var refIdTable = Quote(DmsTableNames.ReferentialIdentity);

        EmitMssqlInsertUpdateDispatch(
            writer,
            identityProjectionColumns,
            tableModel,
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
            writer.Append("N'");
            writer.Append(SqlDialectBase.EscapeSingleQuote(elements[i].IdentityJsonPath));
            writer.Append("=' + ");
            EmitMssqlIdentityElementToNvarchar(writer, elements[i]);
        }
    }

    /// <summary>
    /// Emits a canonical nvarchar conversion for an identity column value in MSSQL.
    /// Delegates to <see cref="DialectIdentityTextFormatter.MssqlColumnToNvarchar"/> so the
    /// trigger and the runtime reference-lookup verification SQL share one source of truth.
    /// </summary>
    private void EmitMssqlIdentityElementToNvarchar(SqlWriter writer, IdentityElementMapping element)
    {
        var columnExpression = $"i.{Quote(element.Column)}";

        if (element.IsDescriptorReference)
        {
            writer.Append("LOWER((SELECT descriptor.");
            writer.Append(Quote(DescriptorUriColumn));
            writer.Append(" FROM ");
            writer.Append(Quote(DmsTableNames.Descriptor));
            writer.Append(" descriptor WHERE descriptor.");
            writer.Append(Quote(DocumentIdColumn));
            writer.Append(" = ");
            writer.Append(columnExpression);
            writer.Append("))");
            return;
        }

        writer.Append(
            DialectIdentityTextFormatter.MssqlColumnToNvarchar(columnExpression, element.ScalarType)
        );
    }

    /// <summary>
    /// Emits abstract identity maintenance trigger body that maintains abstract identity
    /// tables from concrete resource root tables.
    /// </summary>
    private void EmitAbstractIdentityBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
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
                tableModel,
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
        DbTableModel tableModel,
        IReadOnlyList<DbColumnName> identityProjectionColumns,
        DbTableName targetTableName,
        IReadOnlyList<TriggerColumnMapping> mappings,
        string discriminatorValue
    )
    {
        EmitMssqlInsertUpdateDispatch(
            writer,
            identityProjectionColumns,
            tableModel,
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
        DbTableModel tableModel,
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
            EmitMssqlValueDiffWorkset(writer, tableModel, identityProjectionColumns);
            emitBlock(false);
        }

        writer.AppendLine("END");
    }

    /// <summary>
    /// Emits an UPDATE + INSERT upsert for abstract identity maintenance.
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
        DbTableModel tableModel,
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
            EmitMssqlColumnValueDiffPredicate(writer, tableModel, "i", "d", identityProjectionColumns[i]);
        }
        writer.AppendLine(";");
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
    private void EmitPeopleAuthViews(
        SqlWriter writer,
        AuthEdOrgHierarchy? authHierarchy,
        IReadOnlyList<ConcreteResourceModel> concreteResources
    )
    {
        // Guard: skip when there is no auth hierarchy table or the model does not include all
        // association resources the people auth views join against. This is intentional for
        // synthetic/partial test models. Emitting views that reference nonexistent auth objects or
        // tables would cause SQL deployment failures.
        if (
            !AuthObjectDefinitions.GetPeopleAuthViewAvailability(authHierarchy, concreteResources).IsAvailable
        )
        {
            return;
        }

        foreach (var view in AuthObjectDefinitions.PeopleAuthViews)
        {
            EmitPeopleAuthView(writer, view);
        }
    }

    /// <summary>
    /// Emits the <c>CREATE VIEW</c> statements for the four <c>ReadChanges</c> authorization views
    /// in the <c>auth</c> schema, rendered from
    /// <see cref="AuthObjectDefinitions.ReadChangesAuthViews"/>. Each view unions
    /// current-association arms with tracked-change (<c>tracked_changes_edfi.*</c>) arms — combined
    /// with <c>UNION</c>, not <c>UNION ALL</c>, so duplicate authorization pairs are eliminated —
    /// against the current EdOrg hierarchy. Used only by <c>ReadChanges</c> authorization for
    /// Change Query <c>/deletes</c> and <c>/keyChanges</c>.
    /// </summary>
    /// <remarks>
    /// Guarded by the same prerequisites as <see cref="EmitPeopleAuthViews"/> — the auth hierarchy
    /// and all five PrimaryAssociation resources must be present — plus the five
    /// <c>tracked_changes_edfi</c> association tables the tracked arms join, so a synthetic /
    /// partial model set without the tracked-change inventory never emits views referencing
    /// nonexistent tables. Must be emitted after the people auth views (the current/current arms
    /// select from them) and after the tracked-change tables.
    /// </remarks>
    private void EmitReadChangesAuthViews(
        SqlWriter writer,
        AuthEdOrgHierarchy? authHierarchy,
        IReadOnlyList<ConcreteResourceModel> concreteResources,
        IReadOnlyList<TrackedChangeTableInfo> trackedChangeTables
    )
    {
        if (
            !AuthObjectDefinitions.GetPeopleAuthViewAvailability(authHierarchy, concreteResources).IsAvailable
            || !AuthObjectDefinitions.HasReadChangesTrackedChangeTables(trackedChangeTables)
        )
        {
            return;
        }

        foreach (var view in AuthObjectDefinitions.ReadChangesAuthViews)
        {
            EmitPeopleAuthView(writer, view);
        }
    }

    /// <summary>
    /// Emits a single people auth view from its shared <see cref="AuthViewDefinition"/>: header,
    /// arms joined by the view's set-operator, and trailing terminator.
    /// </summary>
    private void EmitPeopleAuthView(SqlWriter writer, AuthViewDefinition view)
    {
        EmitViewHeader(writer, view.View);

        for (int i = 0; i < view.Arms.Count; i++)
        {
            if (i > 0)
            {
                writer.AppendLine(SetOperatorKeyword(view));
            }
            EmitPeopleAuthViewArm(writer, view.Arms[i]);
        }

        writer.AppendLine(";");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits a single <c>SELECT [DISTINCT] ... FROM ... INNER JOIN ...</c> arm of a people auth view.
    /// </summary>
    private void EmitPeopleAuthViewArm(SqlWriter writer, AuthViewArm arm)
    {
        writer.AppendLine(arm.SelectDistinct ? "SELECT DISTINCT" : "SELECT");
        using (writer.Indent())
        {
            for (int j = 0; j < arm.OutputColumns.Count; j++)
            {
                var column = arm.OutputColumns[j];
                var rename = column.OutputName is { } outputName ? $" AS {Quote(outputName)}" : string.Empty;
                var trailing = j < arm.OutputColumns.Count - 1 ? "," : string.Empty;
                writer.AppendLine($"{column.Alias}.{Quote(column.Column)}{rename}{trailing}");
            }
        }
        writer.AppendLine($"FROM {Quote(arm.SourceTable)} {arm.SourceAlias}");
        foreach (var join in arm.Joins)
        {
            var predicates = string.Join(
                " AND ",
                join.On.Select(p =>
                    $"{p.LeftAlias}.{Quote(p.LeftColumn)} = {p.RightAlias}.{Quote(p.RightColumn)}"
                )
            );
            writer.AppendLine($"INNER JOIN {Quote(join.Table)} {join.Alias} ON {predicates}");
        }
    }

    private static string SetOperatorKeyword(AuthViewDefinition view) =>
        view.ArmsSetOperator switch
        {
            AuthViewSetOperator.Union => "UNION",
            AuthViewSetOperator.UnionAll => "UNION ALL",
            AuthViewSetOperator.None => throw new InvalidOperationException(
                $"Auth view '{view.View.Schema.Value}.{view.View.Name}' has multiple arms but "
                    + $"ArmsSetOperator is {nameof(AuthViewSetOperator.None)}."
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(view),
                view.ArmsSetOperator,
                "Unsupported AuthViewSetOperator."
            ),
        };

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
            _ => throw new InvalidOperationException(
                $"Unsupported view creation pattern: {_dialect.ViewCreationPattern}."
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

        foreach (var tableModel in abstractIdentityTables.Select(a => a.TableModel))
        {
            tableModelsByTableName[tableModel.Table] = tableModel;
        }

        return tableModelsByTableName;
    }

    private static IReadOnlyList<DbColumnName> GetStoredColumnsForDocumentStamping(
        DbTableModel tableModel,
        string triggerName
    )
    {
        // Exclude the synthesized change-version mirror columns: they are stamp targets, not client
        // content, so a stamp-only mirror update must not be treated as a representation change. This
        // matches change-queries.md invariant #5 (affectedDocs excludes rows differing only in stamp
        // columns) and keeps mirror columns out of the no-op diff predicate.
        var storedColumns = tableModel
            .Columns.Where(column =>
                column.Storage is ColumnStorage.Stored
                && column.Kind
                    is not (ColumnKind.MirroredContentVersion or ColumnKind.MirroredContentLastModifiedAt)
            )
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
        DbTableModel tableModel,
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

            EmitMssqlColumnValueDiffPredicate(writer, tableModel, leftAlias, rightAlias, columns[i]);
        }
    }

    private void EmitMssqlColumnValueDiffPredicate(
        SqlWriter writer,
        DbTableModel tableModel,
        string leftAlias,
        string rightAlias,
        DbColumnName columnName
    )
    {
        var quotedColumn = Quote(columnName);
        MssqlTriggerDiffEmitter.EmitNullSafeNotEqual(
            writer,
            leftAlias,
            quotedColumn,
            rightAlias,
            quotedColumn,
            GetScalarKind(tableModel, columnName)
        );
    }

    private static ScalarKind? GetScalarKind(DbTableModel tableModel, DbColumnName columnName)
    {
        var columnModel = tableModel.Columns.FirstOrDefault(column => column.ColumnName == columnName);

        if (columnModel is null)
        {
            throw new InvalidOperationException(
                $"Column '{columnName.Value}' was not found on table '{tableModel.Table.Schema.Value}.{tableModel.Table.Name}'."
            );
        }

        return columnModel.ScalarType?.Kind;
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
