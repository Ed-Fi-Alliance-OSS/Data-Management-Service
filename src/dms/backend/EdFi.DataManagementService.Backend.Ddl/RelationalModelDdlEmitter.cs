// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    private static readonly DbColumnName DocumentUuidColumn =
        RelationalNameConventions.DocumentUuidColumnName;
    private static readonly DbColumnName ContentVersionColumn = new("ContentVersion");
    private static readonly DbColumnName ContentLastModifiedAtColumn = new("ContentLastModifiedAt");
    private static readonly DbColumnName IdentityVersionColumn = new("IdentityVersion");
    private static readonly DbColumnName IdentityLastModifiedAtColumn = new("IdentityLastModifiedAt");
    private static readonly DbColumnName CreatedAtColumn = RelationalNameConventions.CreatedAtColumnName;
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
    /// <paramref name="emitDocumentMetadataDefaults"/> is <c>false</c> only for abstract identity tables —
    /// see <see cref="TryResolveMirrorNamedDefault"/>.
    /// </summary>
    private void EmitCreateTable(
        SqlWriter writer,
        DbTableModel table,
        bool emitDocumentMetadataDefaults = true
    )
    {
        writer.AppendLine(_dialect.CreateTableHeader(table.Table));
        writer.AppendLine("(");

        var definitions = new List<string>();

        foreach (var column in table.Columns)
        {
            definitions.Add(RenderColumnDefinition(table, column, emitDocumentMetadataDefaults));
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
    /// <paramref name="emitDocumentMetadataDefaults"/> is <c>false</c> only for abstract identity tables —
    /// see <see cref="TryResolveMirrorNamedDefault"/>.
    /// </summary>
    private string RenderColumnDefinition(
        DbTableModel table,
        DbColumnModel column,
        bool emitDocumentMetadataDefaults = true
    )
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

        if (UsesDocumentIdSequenceDefault(table, column))
        {
            return _dialect.RenderColumnDefinitionWithNamedDefault(
                column.ColumnName,
                type,
                column.IsNullable,
                BuildNamedDefaultConstraintName(table, column),
                _dialect.RenderSequenceDefaultExpression(
                    DmsTableNames.DmsSchema,
                    DmsTableNames.DocumentIdSequence
                )
            );
        }

        if (
            emitDocumentMetadataDefaults
            && TryResolveMirrorNamedDefault(
                table,
                column,
                out var mirrorConstraintName,
                out var mirrorDefault
            )
        )
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
    /// Resolves the named default constraint for a synthesized document-metadata column:
    /// <c>ContentVersion</c> and <c>IdentityVersion</c> default to a non-null sentinel,
    /// <c>ContentLastModifiedAt</c> / <c>IdentityLastModifiedAt</c> / <c>CreatedAt</c> default to the current
    /// UTC timestamp, and <c>DocumentUuid</c> defaults to a freshly generated UUID. All use a
    /// <c>DF_&lt;Table&gt;_&lt;Column&gt;</c> constraint name (rendered by SQL Server; ignored by
    /// PostgreSQL). The trigger overwrites these defaults at write time.
    /// The nullable <c>CreatedByOwnershipTokenId</c> column has no default.
    /// </summary>
    /// <remarks>
    /// Abstract identity tables suppress these defaults: their single <c>INSERT</c> from the maintenance
    /// trigger supplies <c>DocumentUuid</c>, and an out-of-band insert must fail rather than acquire a random
    /// UUID (Phase 2 reads this column for link injection).
    /// </remarks>
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
            case ColumnKind.MirroredIdentityVersion:
                constraintName = BuildNamedDefaultConstraintName(table, column);
                defaultExpression = "0";
                return true;
            case ColumnKind.MirroredContentLastModifiedAt:
            case ColumnKind.MirroredIdentityLastModifiedAt:
            case ColumnKind.CreatedAt:
                constraintName = BuildNamedDefaultConstraintName(table, column);
                defaultExpression = _dialect.CurrentTimestampDefaultExpression;
                return true;
            case ColumnKind.DocumentUuid:
                constraintName = BuildNamedDefaultConstraintName(table, column);
                defaultExpression = _dialect.NewGuidDefaultExpression;
                return true;
            default:
                constraintName = string.Empty;
                defaultExpression = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// Builds the <c>DF_&lt;Table&gt;_&lt;Column&gt;</c> default constraint name for a defaulted column,
    /// applying the dialect identifier length limit. Resource-root table names can already sit at the
    /// dialect limit after identifier shortening, so the generated default-constraint name must be
    /// shortened too (SQL Server enforces a 128-character identifier limit on the named
    /// <c>CONSTRAINT</c>).
    /// </summary>
    private string BuildNamedDefaultConstraintName(DbTableModel table, DbColumnModel column)
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
    /// Reports whether a column originates its value from <c>dms.DocumentIdSequence</c>: the
    /// <c>DocumentId</c> key column of a resource root table. The root <c>INSERT</c> omits the column and
    /// returns the drawn value, which the rest of the write binds onto child rows — the same
    /// sequence-default shape <see cref="UsesCollectionItemSequenceDefault"/> gives collection rows, but
    /// under a named <c>DF_</c> constraint so SQL Server does not assign a system-generated name.
    /// </summary>
    /// <remarks>
    /// Abstract identity tables are excluded by the <see cref="DbTableKind.Root"/> gate (they carry no
    /// identity metadata): their <c>DocumentId</c> is copied from the concrete root row by the maintenance
    /// trigger and must never draw a fresh value.
    /// </remarks>
    private static bool UsesDocumentIdSequenceDefault(DbTableModel table, DbColumnModel column)
    {
        return column.ColumnName.Equals(RelationalNameConventions.DocumentIdColumnName)
            && table.IdentityMetadata.TableKind is DbTableKind.Root;
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

        // Emit any FKs declared on abstract identity tables (the derivation declares none today)
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
    /// Emits <c>CREATE TABLE IF NOT EXISTS</c> statements for abstract identity tables. Their copied
    /// <c>DocumentUuid</c> gets no default, so an insert that bypasses the maintenance trigger fails on
    /// <c>NOT NULL</c> instead of storing a random UUID that link injection would then serve.
    /// </summary>
    private void EmitAbstractIdentityTables(SqlWriter writer, IReadOnlyList<AbstractIdentityTableInfo> tables)
    {
        foreach (var tableInfo in tables)
        {
            // Reuse existing EmitCreateTable - it already handles all table types
            EmitCreateTable(writer, tableInfo.TableModel, emitDocumentMetadataDefaults: false);
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
            // EmitAbstractIdentityBody, FormatNullOrTrueCheck, EmitStringLiteralWithCast.
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
        // Only root stamping paths hold the content stamp in a local: the tracked-change tombstone and
        // key-change rows read it back as their ChangeVersion. Child paths stamp the root row in place
        // and need no locals.
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
            }
        }
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            // For triggers with a ChangeTracking attachment the DELETE branch takes a change
            // version off the sequence and then inserts the tracked-change tombstone (DMS-1179).
            // Without one it falls back to stamping ContentVersion via OLD. On DELETE there is
            // no NEW row, so it returns OLD and skips the normal body.
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
                    if (trackedChangePlan is not null)
                    {
                        // A non-null plan already implies a root stamping trigger: TryBuildTrackedChangePlan
                        // rejects non-root attachments for both dialects, which is also what guarantees the
                        // DECLARE block above introduced the _stampedContentVersion local read below.
                        //
                        // The tombstone's ChangeVersion is a fresh sequence value taken right here.
                        // The OLD image carries the pre-delete ContentVersion, and no row survives a
                        // delete to be stamped into one. The change version stays post-delete and
                        // monotonic because dms.ChangeVersionSequence is the same source every stamp
                        // uses and is what GetMaxChangeVersion reports.
                        EmitPgsqlStampedContentVersionFromSequence(writer, FormatSequenceName());
                        TrackedChangeTriggerBodyEmitter.EmitPgsqlTombstoneInsert(
                            writer,
                            _dialect,
                            trackedChangePlan
                        );
                    }
                    else if (!isRootDocumentStampingTrigger)
                    {
                        // Deleting a child/collection row is a content change to the surviving root
                        // row, so it still takes a stamp. A root delete stamps nothing: the root row
                        // is the stamp store and it is the row going away.
                        EmitPgsqlRootContentStampUpdate(
                            writer,
                            FormatSequenceName(),
                            deleteKeyColumn,
                            mirrorStampTargetTable,
                            "OLD"
                        );
                    }
                    writer.AppendLine("RETURN OLD;");
                }
                writer.AppendLine("END IF;");
            }

            if (trigger.Parameters is TriggerKindParameters.AbstractIdentityMaintenance abstractIdentity)
            {
                // Retiring the concrete root row retires its <Abstract>Identity row with it. That row is
                // the FK target every abstract reference points at, so deleting it here is what makes a
                // still-referenced document's delete fail on the referencing constraint — the 409 path —
                // and what keeps abstract natural-key resolution from binding a deleted document. Until
                // the DocumentId FK into dms.Document was dropped this was that FK's ON DELETE CASCADE.
                writer.AppendLine("IF TG_OP = 'DELETE' THEN");
                using (writer.Indent())
                {
                    EmitPgsqlAbstractIdentityDelete(writer, abstractIdentity.TargetTable);
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

        // DELETE is part of the trigger event list for two kinds: document stamping emits
        // tracked-change tombstones there (DMS-1179), and abstract-identity maintenance retires the
        // owning <Abstract>Identity row there.
        var pgsqlTriggerEvent = trigger.Parameters switch
        {
            TriggerKindParameters.DocumentStamping => "BEFORE INSERT OR UPDATE OR DELETE ON ",
            TriggerKindParameters.AbstractIdentityMaintenance => "BEFORE INSERT OR UPDATE OR DELETE ON ",
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
            TriggerKindParameters.AbstractIdentityMaintenance => "AFTER INSERT, UPDATE, DELETE",
            TriggerKindParameters.MssqlIdentityPropagationTrigger => "AFTER UPDATE",
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
            case TriggerKindParameters.MssqlIdentityPropagationTrigger propagation:
                if (!tableModelsByTableName.TryGetValue(trigger.Table, out var propagationTableModel))
                {
                    throw new InvalidOperationException(
                        $"MssqlIdentityPropagationTrigger trigger '{trigger.Name.Value}' requires a table model for '{trigger.Table.Schema.Value}.{trigger.Table.Name}', but none was found."
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

        var sequenceName = FormatSequenceName();
        var keyColumn = trigger.KeyColumns[0];
        var mirrorStampTargetTable = RequireMirrorStampTargetTable(trigger);

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlDocumentStampingBody(
                writer,
                trigger,
                tableModel,
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
    /// projection columns (key-change detection would be impossible); when the attached trigger does not
    /// stamp a root table; or when <see cref="TrackedChangeTriggerBodyEmitter.BuildPlan"/> finds an
    /// inventory inconsistency.
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

        // Dialect-independent because both dialect bodies obtain their plan exclusively from here. On the
        // PgSQL side the DELETE-branch tombstone reads the plpgsql local _stampedContentVersion, and only
        // the root stamping path emits the DECLARE block that introduces it, so a non-root attachment
        // would render a function body that does not compile.
        var mirrorTarget = RequireMirrorStampTargetTable(trigger);
        if (!IsRootDocumentStampingTrigger(trigger, tableModel, mirrorTarget))
        {
            throw new InvalidOperationException(
                $"DocumentStamping trigger '{trigger.Name.Value}' is attached to tracked-change table "
                    + $"'{attachment.TrackedChangeTable.Schema.Value}.{attachment.TrackedChangeTable.Name}' "
                    + "but is not a root document-stamping trigger; only root triggers may carry a "
                    + "tracked-change attachment. Only root stamping triggers declare the "
                    + "_stampedContentVersion local the tombstone reads."
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

        // The root row owns its stamps outright, so a BEFORE trigger assigns them straight onto NEW.
        // An INSERT is a brand new row with no prior stamp to preserve, so it takes both the content
        // and the identity stamp plus CreatedAt; a changed UPDATE takes only the content stamp.
        if (isRootDocumentStampingTrigger)
        {
            writer.AppendLine("IF TG_OP = 'INSERT' THEN");
            using (writer.Indent())
            {
                EmitPgsqlRootContentStamp(writer, sequenceName);
                EmitPgsqlNewSequenceAssignment(writer, IdentityVersionColumn, sequenceName);
                EmitPgsqlNewNowAssignment(writer, IdentityLastModifiedAtColumn);
                EmitPgsqlNewNowAssignment(writer, CreatedAtColumn);
            }
            writer.AppendLine("ELSIF TG_OP = 'UPDATE' THEN");
            using (writer.Indent())
            {
                EmitPgsqlRootContentStamp(writer, sequenceName);
            }
            writer.AppendLine("END IF;");
        }
        else
        {
            EmitPgsqlRootContentStampUpdate(writer, sequenceName, keyColumn, mirrorStampTargetTable, "NEW");
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
                // IdentityProjectionColumns is populated only for root stamping triggers
                // (DeriveTriggerInventoryPass passes [] for child/collection/_ext tables), so the bump
                // is always a local assignment onto the root row this BEFORE trigger is writing.
                EmitPgsqlNewSequenceAssignment(writer, IdentityVersionColumn, sequenceName);
                EmitPgsqlNewNowAssignment(writer, IdentityLastModifiedAtColumn);

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
                        trackedChangePlan
                    );
                }
            }

            writer.AppendLine("END IF;");
        }
    }

    /// <summary>
    /// Emits the plpgsql assignments that stamp the root row's own content pair: the change version is
    /// taken into <c>_stampedContentVersion</c> first so the tracked-change tombstone and key-change rows
    /// can report the same value, then assigned onto <c>NEW</c>. Legal because the root stamping trigger
    /// is a BEFORE trigger, so assigning <c>NEW</c> is what the row is stored with.
    /// </summary>
    private void EmitPgsqlRootContentStamp(SqlWriter writer, string sequenceName)
    {
        EmitPgsqlStampedContentVersionFromSequence(writer, sequenceName);
        writer.Append("NEW.");
        writer.Append(Quote(ContentVersionColumn));
        writer.AppendLine(" := _stampedContentVersion;");
        EmitPgsqlNewNowAssignment(writer, ContentLastModifiedAtColumn);
    }

    /// <summary>
    /// Emits <c>NEW."&lt;column&gt;" := nextval('&lt;sequence&gt;');</c>.
    /// </summary>
    private void EmitPgsqlNewSequenceAssignment(SqlWriter writer, DbColumnName column, string sequenceName)
    {
        writer.Append("NEW.");
        writer.Append(Quote(column));
        writer.Append(" := nextval('");
        writer.Append(sequenceName);
        writer.AppendLine("');");
    }

    /// <summary>
    /// Emits <c>NEW."&lt;column&gt;" := now();</c>.
    /// </summary>
    private void EmitPgsqlNewNowAssignment(SqlWriter writer, DbColumnName column)
    {
        writer.Append("NEW.");
        writer.Append(Quote(column));
        writer.AppendLine(" := now();");
    }

    /// <summary>
    /// Emits the plpgsql assignment that gives the tracked-change tombstone its change version:
    /// a fresh value off <c>dms.ChangeVersionSequence</c>. The DELETE branch has no row left to stamp
    /// and read back, and taking the value here keeps the tombstone independent of the delete order.
    /// </summary>
    private static void EmitPgsqlStampedContentVersionFromSequence(SqlWriter writer, string sequenceName)
    {
        writer.Append("_stampedContentVersion := nextval('");
        writer.Append(sequenceName);
        writer.AppendLine("');");
    }

    /// <summary>
    /// Emits the content stamp a child / collection / <c>_ext</c> trigger applies to its owning root row:
    /// a single <c>UPDATE &lt;root&gt; r SET "ContentVersion" = nextval(...), "ContentLastModifiedAt" = now()
    /// WHERE r."DocumentId" = &lt;NEW|OLD&gt;."&lt;locator&gt;"</c>. A cascade-deleted root leaves no row to
    /// match, which is what the old CTE's <c>EXISTS</c> guard used to enforce explicitly.
    /// </summary>
    private void EmitPgsqlRootContentStampUpdate(
        SqlWriter writer,
        string sequenceName,
        DbColumnName keyColumn,
        DbTableName rootTable,
        string sourceRowAlias
    )
    {
        writer.Append("UPDATE ");
        writer.Append(Quote(rootTable));
        writer.AppendLine(" r");
        writer.Append("SET ");
        writer.Append(Quote(ContentVersionColumn));
        writer.Append(" = nextval('");
        writer.Append(sequenceName);
        writer.Append("'), ");
        writer.Append(Quote(ContentLastModifiedAtColumn));
        writer.AppendLine(" = now()");
        writer.Append("WHERE r.");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(" = ");
        writer.Append(sourceRowAlias);
        writer.Append(".");
        writer.Append(Quote(keyColumn));
        writer.AppendLine(";");
    }

    private void EmitMssqlDocumentStampingBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
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
        var quotedDocumentIdColumn = Quote(DocumentIdColumn);
        var stampTarget = Quote(mirrorStampTargetTable);
        var isRootDocumentStampingTrigger = IsRootDocumentStampingTrigger(
            trigger,
            tableModel,
            mirrorStampTargetTable
        );

        // Built unconditionally so attachment inconsistencies (unknown tracked table, empty identity
        // workset on a Resource-kind attachment, non-root attachment) throw even when neither
        // tracked-change block below is emitted.
        var trackedChangePlan = TryBuildTrackedChangePlan(trigger, tableModel, trackedChangeTablesByName);

        if (isRootDocumentStampingTrigger)
        {
            // A pure insert is a brand new row with no prior stamp to preserve, so it takes both stamps
            // and CreatedAt in one pass. The workset is captured into a table variable so the UPDATE can
            // be gated: the stamp now writes the trigger's own table, and a zero-row UPDATE still re-fires
            // the trigger, which recurses to the nesting limit on databases with RECURSIVE_TRIGGERS ON.
            writer.AppendLine("DECLARE @insertedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);");
            writer.Append("INSERT INTO @insertedDocs (");
            writer.Append(quotedDocumentIdColumn);
            writer.AppendLine(")");
            writer.Append("SELECT i.");
            writer.AppendLine(quotedKeyColumn);
            writer.AppendLine("FROM inserted i");
            writer.Append("LEFT JOIN deleted del ON ");
            EmitMssqlJoinConjunction(writer, "del", "i", tableKeyColumns);
            writer.AppendLine();
            writer.Append("WHERE del.");
            writer.Append(quotedProbeKeyColumn);
            writer.AppendLine(" IS NULL;");
            writer.AppendLine("IF EXISTS (SELECT 1 FROM @insertedDocs)");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine("UPDATE r");
                writer.Append("SET r.");
                writer.Append(Quote(ContentVersionColumn));
                writer.Append(" = NEXT VALUE FOR ");
                writer.Append(sequenceName);
                writer.AppendLine(",");
                writer.Append("    r.");
                writer.Append(Quote(ContentLastModifiedAtColumn));
                writer.AppendLine(" = sysutcdatetime(),");
                writer.Append("    r.");
                writer.Append(Quote(IdentityVersionColumn));
                writer.Append(" = NEXT VALUE FOR ");
                writer.Append(sequenceName);
                writer.AppendLine(",");
                writer.Append("    r.");
                writer.Append(Quote(IdentityLastModifiedAtColumn));
                writer.AppendLine(" = sysutcdatetime(),");
                writer.Append("    r.");
                writer.Append(Quote(CreatedAtColumn));
                writer.AppendLine(" = sysutcdatetime()");
                writer.Append("FROM ");
                writer.Append(stampTarget);
                writer.AppendLine(" r");
                writer.Append("INNER JOIN @insertedDocs s ON s.");
                writer.Append(quotedDocumentIdColumn);
                writer.Append(" = r.");
                writer.Append(quotedDocumentIdColumn);
                writer.AppendLine(";");
            }
            writer.AppendLine("END");
        }

        // ContentVersion stamp - compute the set of affected documents from inserted/deleted
        // rows that are inserts, deletes, or actual value changes. No-op UPDATEs are excluded.
        // A root pure delete stamps nothing: the root row is the stamp store and it is the row going
        // away, so only child / collection / _ext triggers carry a deleted-side arm.
        writer.AppendLine("DECLARE @stamped TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);");
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
            if (!isRootDocumentStampingTrigger)
            {
                // Child rows map many-to-one onto the root document, so the child shape keeps UNION's
                // dedup and the deleted-side diff for changed updates.
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
                EmitMssqlColumnValueDiffDisjunction(writer, tableModel, "i", "del", storedColumns);
                writer.AppendLine();
            }
        }
        writer.AppendLine(")");
        writer.Append("INSERT INTO @stamped (");
        writer.Append(quotedDocumentIdColumn);
        writer.AppendLine(")");
        writer.Append("SELECT ");
        writer.Append(quotedKeyColumn);
        writer.AppendLine(" FROM affectedDocs;");

        // The guard bounds direct recursion: without it the stamp UPDATE re-fires this trigger even with
        // an empty workset (statement triggers fire on 0 rows), which recurses to the nesting limit on
        // databases with RECURSIVE_TRIGGERS ON. A cascade-deleted root leaves no row for the child arm's
        // join to match, so a child stamp is naturally a no-op there.
        writer.AppendLine("IF EXISTS (SELECT 1 FROM @stamped)");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("UPDATE r");
            writer.Append("SET r.");
            writer.Append(Quote(ContentVersionColumn));
            writer.Append(" = NEXT VALUE FOR ");
            writer.Append(sequenceName);
            writer.AppendLine(",");
            writer.Append("    r.");
            writer.Append(Quote(ContentLastModifiedAtColumn));
            writer.AppendLine(" = sysutcdatetime()");
            writer.Append("FROM ");
            writer.Append(stampTarget);
            writer.AppendLine(" r");
            writer.Append("INNER JOIN @stamped s ON s.");
            writer.Append(quotedDocumentIdColumn);
            writer.Append(" = r.");
            writer.Append(quotedDocumentIdColumn);
            writer.AppendLine(";");
        }
        writer.AppendLine("END");

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
                    sequenceName
                );
            }
            writer.AppendLine("END");
        }

        // IdentityVersion stamp. IdentityProjectionColumns is populated only for root stamping triggers
        // (DeriveTriggerInventoryPass passes [] for child / collection / _ext tables), so every statement
        // below writes the root row this trigger is attached to.
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
                    stampTarget,
                    sequenceName,
                    keyColumn,
                    trackedChangePlan
                );
            }
            else
            {
                // Performance pre-filter: UPDATE(col) returns true if the column appeared in the SET clause,
                // regardless of whether the value actually changed. The WHERE clause below (using null-safe
                // inequality) is the authoritative value-change check that filters to only actually changed
                // rows. It also bounds recursion: the bump below sets only stamp columns, so the re-fired
                // trigger never re-enters this block.
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
                        stampTarget,
                        sequenceName,
                        keyColumn
                    );
                }

                writer.AppendLine("END");
            }
        }
    }

    /// <summary>
    /// Emits the SQL Server IdentityVersion stamp for a stamping trigger attached to a
    /// <see cref="TrackedChangeTableKind.Resource"/> tracked-change table, capturing the
    /// identity-changed workset into <c>@identityChangedDocs</c> and rendering the key-change INSERT from
    /// it. Gated only by row-set existence plus the authoritative null-safe value diff — never by
    /// <c>UPDATE(column)</c>, which reports SET-clause membership rather than value change (DMS-1179 AC
    /// bans <c>UPDATE(column)</c> for key-change eligibility).
    /// </summary>
    /// <param name="rootStampTarget">The quoted root table that owns the identity stamp.</param>
    private void EmitMssqlIdentityStampWithKeyChange(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        string rootStampTarget,
        string sequenceName,
        DbColumnName keyColumn,
        TrackedChangeInsertPlan trackedChangePlan
    )
    {
        var quotedKeyColumn = Quote(keyColumn);
        var quotedDocumentIdColumn = Quote(DocumentIdColumn);

        writer.AppendLine("IF EXISTS (SELECT 1 FROM deleted) AND EXISTS (SELECT 1 FROM inserted)");
        writer.AppendLine("BEGIN");

        using (writer.Indent())
        {
            writer.AppendLine(
                "DECLARE @identityChangedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY, [ContentVersion] bigint NOT NULL);"
            );
            // The captured ContentVersion is the root row's post-content-stamp value: an identity change
            // is also a stored-column change, so the content stamp above already bumped this row. The
            // key-change row reports it as its ChangeVersion.
            writer.Append("INSERT INTO @identityChangedDocs (");
            writer.Append(quotedDocumentIdColumn);
            writer.Append(", ");
            writer.Append(Quote(ContentVersionColumn));
            writer.AppendLine(")");
            writer.Append("SELECT r.");
            writer.Append(quotedDocumentIdColumn);
            writer.Append(", r.");
            writer.AppendLine(Quote(ContentVersionColumn));
            writer.Append("FROM ");
            writer.Append(rootStampTarget);
            writer.AppendLine(" r");
            writer.Append("INNER JOIN inserted i ON i.");
            writer.Append(quotedKeyColumn);
            writer.Append(" = r.");
            writer.AppendLine(quotedDocumentIdColumn);
            writer.Append("INNER JOIN deleted del ON del.");
            writer.Append(quotedKeyColumn);
            writer.Append(" = i.");
            writer.AppendLine(quotedKeyColumn);
            EmitMssqlIdentityDiffWhereClause(writer, trigger, tableModel);

            // Gated on a non-empty workset: this block re-enters on any UPDATE with both row sets
            // present — including the stamp UPDATEs this trigger issues — so an unguarded zero-row bump
            // would keep re-firing the trigger up to the nesting limit under RECURSIVE_TRIGGERS ON.
            writer.AppendLine("IF EXISTS (SELECT 1 FROM @identityChangedDocs)");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine("UPDATE r");
                writer.Append("SET r.");
                writer.Append(Quote(IdentityVersionColumn));
                writer.Append(" = NEXT VALUE FOR ");
                writer.Append(sequenceName);
                writer.AppendLine(",");
                writer.Append("    r.");
                writer.Append(Quote(IdentityLastModifiedAtColumn));
                writer.AppendLine(" = sysutcdatetime()");
                writer.Append("FROM ");
                writer.Append(rootStampTarget);
                writer.AppendLine(" r");
                writer.Append("INNER JOIN @identityChangedDocs idc ON idc.");
                writer.Append(quotedDocumentIdColumn);
                writer.Append(" = r.");
                writer.Append(quotedDocumentIdColumn);
                writer.AppendLine(";");
            }
            writer.AppendLine("END");

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
    /// Emits the root-local <c>UPDATE r SET r.[IdentityVersion] = NEXT VALUE FOR ... FROM &lt;root&gt; r
    /// INNER JOIN inserted ... INNER JOIN deleted ... WHERE &lt;null-safe diff&gt;</c> statement.
    /// </summary>
    private void EmitMssqlIdentityVersionUpdate(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        string rootStampTarget,
        string sequenceName,
        DbColumnName keyColumn
    )
    {
        var quotedKeyColumn = Quote(keyColumn);
        var quotedDocumentIdColumn = Quote(DocumentIdColumn);

        writer.AppendLine("UPDATE r");
        writer.Append("SET r.");
        writer.Append(Quote(IdentityVersionColumn));
        writer.Append(" = NEXT VALUE FOR ");
        writer.Append(sequenceName);
        writer.AppendLine(",");
        writer.Append("    r.");
        writer.Append(Quote(IdentityLastModifiedAtColumn));
        writer.AppendLine(" = sysutcdatetime()");
        writer.Append("FROM ");
        writer.Append(rootStampTarget);
        writer.AppendLine(" r");
        writer.Append("INNER JOIN inserted i ON i.");
        writer.Append(quotedKeyColumn);
        writer.Append(" = r.");
        writer.AppendLine(quotedDocumentIdColumn);
        writer.Append("INNER JOIN deleted del ON del.");
        writer.Append(quotedKeyColumn);
        writer.Append(" = i.");
        writer.AppendLine(quotedKeyColumn);
        EmitMssqlIdentityDiffWhereClause(writer, trigger, tableModel);
    }

    /// <summary>
    /// Emits the terminating <c>WHERE &lt;null-safe identity-projection diff&gt;;</c> clause shared by the
    /// identity workset capture and the plain identity bump.
    /// </summary>
    private void EmitMssqlIdentityDiffWhereClause(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel
    )
    {
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

    /// <summary>
    /// Emits the DELETE that retires the <c>&lt;Abstract&gt;Identity</c> row belonging to the concrete
    /// root row being deleted.
    /// </summary>
    private void EmitPgsqlAbstractIdentityDelete(SqlWriter writer, DbTableName targetTableName)
    {
        writer.Append("DELETE FROM ");
        writer.Append(Quote(targetTableName));
        writer.Append(" WHERE ");
        writer.Append(Quote(DocumentIdColumn));
        writer.Append(" = OLD.");
        writer.Append(Quote(DocumentIdColumn));
        writer.AppendLine(";");
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
            writer.Append(", ");
            writer.Append(Quote(DocumentUuidColumn));
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
            // DocumentUuid is read straight off NEW: the root row carries its own DocumentUuid, bound by
            // the write path on the INSERT itself rather than filled in by TR_<R>_Stamp, so the value is
            // present no matter which same-event BEFORE row trigger PostgreSQL runs first.
            writer.Append(", NEW.");
            writer.Append(Quote(DocumentUuidColumn));
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

            // DocumentUuid is immutable for the life of the document, so the conflict path deliberately
            // leaves it alone and only refreshes the projected identity columns.
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
        // Retiring the concrete root row retires its <Abstract>Identity row with it. That row is the FK
        // target every abstract reference points at, so deleting it here is what makes a still-referenced
        // document's delete fail on the referencing constraint — the 409 path — and what keeps abstract
        // natural-key resolution from binding a deleted document. Until the DocumentId FK into
        // dms.Document was dropped this was that FK's ON DELETE CASCADE.
        writer.AppendLine("IF NOT EXISTS (SELECT 1 FROM inserted)");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.Append("DELETE FROM ");
            writer.AppendLine(Quote(targetTableName));
            writer.Append("WHERE ");
            writer.Append(Quote(DocumentIdColumn));
            writer.Append(" IN (SELECT ");
            writer.Append(Quote(DocumentIdColumn));
            writer.AppendLine(" FROM deleted);");
        }
        writer.AppendLine("END");
        writer.AppendLine("ELSE");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
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
        writer.AppendLine("END");
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

        // UPDATE existing rows first. DocumentUuid is immutable for the life of the document, so the update
        // branch deliberately touches only the projected identity columns.
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
        // DocumentUuid comes from the inserted image: the root row carries its own DocumentUuid, written by
        // the triggering statement itself rather than filled in later by TR_<R>_Stamp, so the AFTER-trigger
        // image already holds the final value. The join is on the same row set the SELECT already reads,
        // so it never drops or duplicates a row.
        writer.Append("INSERT INTO ");
        writer.Append(targetTable);
        writer.Append(" (");
        writer.Append(documentIdCol);
        writer.Append(", ");
        writer.Append(Quote(DocumentUuidColumn));
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
        writer.Append(", d.");
        writer.Append(Quote(DocumentUuidColumn));
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

        writer.Append("INNER JOIN inserted d ON d.");
        writer.Append(documentIdCol);
        writer.Append(" = s.");
        writer.Append(documentIdCol);
        writer.AppendLine();

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
    /// Emits the MSSQL identity-propagation trigger body that cascades
    /// identity column updates to referrer tables when <c>ON UPDATE CASCADE</c> is not available.
    /// The trigger is placed on the referenced entity and propagates to all referrers.
    /// </summary>
    private void EmitIdentityPropagationBody(
        SqlWriter writer,
        DbTriggerInfo trigger,
        DbTableModel tableModel,
        TriggerKindParameters.MssqlIdentityPropagationTrigger propagation
    )
    {
        if (_dialect.Rules.Dialect != SqlDialect.Mssql)
        {
            throw new InvalidOperationException(
                $"Identity-propagation triggers are only supported for MSSQL, but dialect is {_dialect.Rules.Dialect}."
            );
        }

        if (propagation.ReferrerUpdates.Count == 0)
        {
            throw new InvalidOperationException(
                "MssqlIdentityPropagationTrigger trigger was created with zero referrer updates. "
                    + "This indicates a bug in DeriveTriggerInventoryPass — triggers with no "
                    + "referrers should be skipped."
            );
        }

        var documentIdCol = Quote(DocumentIdColumn);

        // AFTER UPDATE trigger: SQL Server has already applied the owning row update by
        // the time this body runs. We propagate identity-column changes to referrer
        // tables. The ON UPDATE NO ACTION FKs are emitted as DocumentId-only on MSSQL
        // (identity columns excluded), so the parent UPDATE does not violate the FK,
        // and the referrer UPDATE that follows reconciles the stored projected
        // identity columns.
        //
        // Guard: the trigger fires on every UPDATE statement against the owning table,
        // including the abstract-identity upsert's zero-row UPDATE phase. Without a
        // value-diff gate, each referrer UPDATE below would itself fire any
        // identity-propagation triggers on the referrer's table and cascade through
        // the schema, easily exceeding SQL Server's 32-level trigger nesting limit.
        // The IF EXISTS short-circuits the entire body when no identity column
        // actually changed, which makes the inner UPDATEs (and their cascades) run
        // only when there is real work to do.
        writer.Append("IF (");
        EmitMssqlUpdateColumnDisjunction(writer, trigger.IdentityProjectionColumns);
        writer.AppendLine(")");
        writer.AppendLine("AND EXISTS (");
        using (writer.Indent())
        {
            writer.Append("SELECT 1 FROM inserted i INNER JOIN deleted d ON i.");
            writer.Append(documentIdCol);
            writer.Append(" = d.");
            writer.AppendLine(documentIdCol);
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
                    "d",
                    trigger.IdentityProjectionColumns[i]
                );
            }
            writer.AppendLine();
        }
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
                writer.Append("WHERE (");
                for (int i = 0; i < referrer.ColumnMappings.Count; i++)
                {
                    if (i > 0)
                    {
                        writer.Append(" OR ");
                    }
                    EmitMssqlColumnValueDiffPredicate(
                        writer,
                        tableModel,
                        "i",
                        "d",
                        referrer.ColumnMappings[i].SourceColumn
                    );
                }
                writer.AppendLine(")");
                writer.Append("AND ");
                EmitMssqlPropagationOldValueConjunction(writer, referrer.ColumnMappings);
                writer.AppendLine(";");
                writer.AppendLine();
            }
        }

        writer.AppendLine("END");
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
    /// <see cref="ISqlDialect.RenderColumnType"/>. For implicit system columns (Ordinal, FK, and the
    /// synthesized <c>DocumentUuid</c> / <c>CreatedByOwnershipTokenId</c> metadata mirrors), falls back to the
    /// dialect type dedicated to that column kind, because those kinds have no dialect-neutral
    /// <see cref="ScalarKind"/>.
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
                ColumnKind.DocumentUuid => _dialect.UuidColumnType,
                ColumnKind.CreatedByOwnershipTokenId => _dialect.SmallintColumnType,
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
        // Exclude every synthesized document-metadata column: they are stamp targets, not client
        // content, so a stamp-only update must not be treated as a representation change. This
        // matches change-queries.md invariant #5 (affectedDocs excludes rows differing only in stamp
        // columns) and keeps those columns out of the no-op diff predicate.
        var storedColumns = tableModel
            .Columns.Where(column =>
                column.Storage is ColumnStorage.Stored && !IsDocumentMetadataMirrorColumn(column.Kind)
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

    /// <summary>
    /// Returns <see langword="true"/> for the synthesized root-table columns that carry the row's own
    /// document metadata. These are system columns maintained only by document-stamping
    /// triggers, never client content, so they are excluded wherever the emitter reasons about a row's
    /// client-visible representation.
    /// </summary>
    /// <remarks>
    /// Write invariant for these columns, which downstream consumers depend on: no client content ever
    /// reaches them (<c>IsWritable=false</c> keeps them out of client-writable projections), and the
    /// stamp values are assigned by the generated triggers and the column defaults. Neither dialect
    /// repairs out-of-band tampering: <c>DocumentUuid</c> and <c>CreatedAt</c> are settled on the
    /// insert path and each stamping trigger re-asserts only the stamp values it just bumped, so a
    /// later out-of-band <c>UPDATE</c> of those two columns is <em>not</em> self-healed on either
    /// dialect. Consumers must therefore not assume tamper repair; the root row is authoritative for
    /// this metadata.
    /// </remarks>
    private static bool IsDocumentMetadataMirrorColumn(ColumnKind kind)
    {
        return kind
            is ColumnKind.MirroredContentVersion
                or ColumnKind.MirroredContentLastModifiedAt
                or ColumnKind.DocumentUuid
                or ColumnKind.MirroredIdentityVersion
                or ColumnKind.MirroredIdentityLastModifiedAt
                or ColumnKind.CreatedAt
                or ColumnKind.CreatedByOwnershipTokenId;
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
    /// Formats the qualified <c>dms.ChangeVersionSequence</c> name for the current dialect.
    /// </summary>
    private string FormatSequenceName()
    {
        return $"{Quote(DmsTableNames.DmsSchema)}.{Quote(DmsTableNames.ChangeVersionSequence)}";
    }
}
