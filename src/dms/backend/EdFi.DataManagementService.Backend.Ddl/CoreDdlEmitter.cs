// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits deterministic DDL for the core <c>dms.*</c> schema objects.
/// <para>
/// This includes tables, constraints, indexes, sequences, and the descriptor
/// stamping trigger required by the v1 object inventory defined in
/// <c>reference/design/backend-redesign/design-docs/ddl-generation.md</c>.
/// </para>
/// <para>
/// Emission follows a strict phased order to satisfy dependency requirements
/// and ensure deterministic, byte-for-byte stable output:
/// <list type="number">
/// <item>Schemas</item>
/// <item>Extensions (pgcrypto for PostgreSQL; no-op for SQL Server)</item>
/// <item>Sequences</item>
/// <item>Functions (GetMaxChangeVersion, UUIDv5 helper)</item>
/// <item>Tables (PK / UNIQUE / CHECK inline; no cross-table FKs)</item>
/// <item>Foreign keys (ALTER TABLE ADD CONSTRAINT)</item>
/// <item>Indexes</item>
/// <item>Triggers</item>
/// </list>
/// </para>
/// </summary>
public sealed class CoreDdlEmitter
{
    private readonly ISqlDialect _dialect;
    private readonly TrackedChangeTableInfo? _sharedDescriptorTrackedChangeTable;

    /// <summary>
    /// Initializes a new <see cref="CoreDdlEmitter"/> for the specified dialect,
    /// optionally wiring the shared descriptor tracked-change tombstone.
    /// </summary>
    /// <param name="dialect">The SQL dialect to render DDL for.</param>
    /// <param name="sharedDescriptorTrackedChangeTable">
    /// When non-null, the tombstone <c>INSERT</c> is appended to the <c>DELETE</c> branch of
    /// the descriptor stamping trigger. Must have <see cref="TrackedChangeTableKind.SharedDescriptor"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dialect"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="sharedDescriptorTrackedChangeTable"/> is non-null but its
    /// <see cref="TrackedChangeTableInfo.Kind"/> is not <see cref="TrackedChangeTableKind.SharedDescriptor"/>.
    /// </exception>
    public CoreDdlEmitter(
        ISqlDialect dialect,
        TrackedChangeTableInfo? sharedDescriptorTrackedChangeTable = null
    )
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

        if (
            sharedDescriptorTrackedChangeTable is not null
            && sharedDescriptorTrackedChangeTable.Kind != TrackedChangeTableKind.SharedDescriptor
        )
        {
            throw new InvalidOperationException(
                $"CoreDdlEmitter only accepts a tracked-change table with kind SharedDescriptor; "
                    + $"received kind '{sharedDescriptorTrackedChangeTable.Kind}' for table "
                    + $"'{sharedDescriptorTrackedChangeTable.Table.Schema.Value}.{sharedDescriptorTrackedChangeTable.Table.Name}'."
            );
        }

        _sharedDescriptorTrackedChangeTable = sharedDescriptorTrackedChangeTable;
    }

    private const string DescriptorStampingTriggerName = "TR_Descriptor_Stamp_Document";

    private static readonly DbTableName _descriptorTable = DmsTableNames.Descriptor;
    private static readonly DbTableName _documentTable = DmsTableNames.Document;
    private static readonly DbTableName _documentCacheTable = DmsTableNames.DocumentCache;
    private static readonly DbTableName _effectiveSchemaTable = EffectiveSchemaTableDefinition.Table;
    private static readonly DbColumnName _effectiveSchemaSingletonIdColumn =
        EffectiveSchemaTableDefinition.EffectiveSchemaSingletonId;
    private static readonly DbColumnName _apiSchemaFormatVersionColumn =
        EffectiveSchemaTableDefinition.ApiSchemaFormatVersion;
    private static readonly DbColumnName _effectiveSchemaHashColumn =
        EffectiveSchemaTableDefinition.EffectiveSchemaHash;
    private static readonly DbColumnName _resourceKeyCountColumn =
        EffectiveSchemaTableDefinition.ResourceKeyCount;
    private static readonly DbColumnName _resourceKeySeedHashColumn =
        EffectiveSchemaTableDefinition.ResourceKeySeedHash;
    private static readonly DbColumnName _appliedAtColumn = EffectiveSchemaTableDefinition.AppliedAt;
    private static readonly DbTableName _referentialIdentityTable = DmsTableNames.ReferentialIdentity;
    private static readonly DbTableName _resourceKeyTable = DmsTableNames.ResourceKey;
    private static readonly DbTableName _schemaComponentTable = DmsTableNames.SchemaComponent;

    /// <summary>
    /// Creates a column-name value object for use in core DDL emission.
    /// </summary>
    private static DbColumnName Col(string name) => new(name);

    /// <summary>
    /// Builds a dialect-specific string type with the specified maximum length.
    /// </summary>
    private string StringType(int maxLength) =>
        $"{_dialect.Rules.ScalarTypeDefaults.StringType}({maxLength})";

    /// <summary>
    /// Gets the dialect default date scalar type.
    /// </summary>
    private string DateType => _dialect.Rules.ScalarTypeDefaults.DateType;

    /// <summary>
    /// Gets the dialect default date-time scalar type.
    /// </summary>
    private string DateTimeType => _dialect.Rules.ScalarTypeDefaults.DateTimeType;

    /// <summary>
    /// Gets the dialect default boolean scalar type.
    /// </summary>
    private string BooleanType => _dialect.Rules.ScalarTypeDefaults.BooleanType;

    /// <summary>
    /// Gets the default expression for generating change/version values from the core change-version sequence.
    /// </summary>
    private string SequenceDefault =>
        _dialect.RenderSequenceDefaultExpression(
            DmsTableNames.DmsSchema,
            DmsTableNames.ChangeVersionSequence
        );

    /// <summary>
    /// Generates the complete core <c>dms.*</c> DDL script for the configured dialect.
    /// </summary>
    /// <returns>
    /// A deterministic, canonicalized SQL string containing all core schema objects.
    /// </returns>
    public string Emit()
    {
        var writer = new SqlWriter(_dialect);

        EmitSchemas(writer);
        EmitExtensions(writer);
        EmitSequences(writer);
        EmitFunctions(writer);
        EmitTables(writer);
        EmitForeignKeys(writer);
        EmitIndexes(writer);
        EmitTriggers(writer);

        return writer.ToString();
    }

    // ── Phase 1: Schemas ────────────────────────────────────────────────

    /// <summary>
    /// Emits core schema creation statements.
    /// </summary>
    private void EmitSchemas(SqlWriter writer)
    {
        writer.WritePhaseHeader(1, "Schemas");

        writer.AppendLine(_dialect.CreateSchemaIfNotExists(DmsTableNames.DmsSchema));
        writer.AppendLine();
    }

    // ── Phase 2: Extensions ─────────────────────────────────────────────

    /// <summary>
    /// Emits database extension creation statements required by core functions.
    /// For PostgreSQL this includes <c>pgcrypto</c> (used by the UUIDv5 helper).
    /// For SQL Server this is a no-op.
    /// </summary>
    private void EmitExtensions(SqlWriter writer)
    {
        var pgcrypto = _dialect.CreateExtensionIfNotExists("pgcrypto");
        if (pgcrypto.Length == 0)
        {
            return;
        }

        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 2: Extensions");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        writer.AppendLine(pgcrypto);
        writer.AppendLine();
    }

    // ── Phase 3: Sequences ──────────────────────────────────────────────

    /// <summary>
    /// Emits the core sequence inventory required by core tables and triggers.
    /// </summary>
    private void EmitSequences(SqlWriter writer)
    {
        writer.WritePhaseHeader(3, "Sequences");

        EmitChangeVersionSequence(writer);
        EmitCollectionItemIdSequence(writer);
    }

    /// <summary>
    /// Emits the collection-item sequence used for stable collection row identity defaults.
    /// </summary>
    private void EmitCollectionItemIdSequence(SqlWriter writer)
    {
        writer.AppendLine(
            _dialect.CreateSequenceIfNotExists(
                DmsTableNames.DmsSchema,
                DmsTableNames.CollectionItemIdSequence
            )
        );
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the change-version sequence used for deterministic version stamping.
    /// </summary>
    private void EmitChangeVersionSequence(SqlWriter writer)
    {
        writer.AppendLine(
            _dialect.CreateSequenceIfNotExists(DmsTableNames.DmsSchema, DmsTableNames.ChangeVersionSequence)
        );
        writer.AppendLine();
    }

    // ── Phase 4: Functions and Types ──────────────────────────────────────

    /// <summary>
    /// Emits database functions and type definitions required by core infrastructure.
    /// Includes the <c>GetMaxChangeVersion</c> helper and the UUIDv5 helper (both dialects),
    /// the <c>throw_error</c> function (PostgreSQL), and user-defined table types for
    /// authorization TVPs (SQL Server).
    /// </summary>
    private void EmitFunctions(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 4: Functions and Types");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        if (_dialect.Rules.Dialect == SqlDialect.Mssql)
        {
            // Each CREATE OR ALTER FUNCTION must be the first statement in its T-SQL
            // batch. Alphabetical (case-insensitive) within Phase 4:
            //   GetMaxChangeVersion -> uuidv5 -> BigIntTable -> UniqueIdentifierTable.
            writer.AppendLine("GO");
            writer.AppendLine(_dialect.CreateGetMaxChangeVersionFunction(DmsTableNames.DmsSchema));
            writer.AppendLine("GO");
            writer.AppendLine(_dialect.CreateUuidv5Function(DmsTableNames.DmsSchema));
            writer.AppendLine("GO");
            writer.AppendLine();

            // User-Defined Table Types for authorization query parameterization (alphabetical)
            writer.AppendLine(
                _dialect.CreateUserDefinedTableTypeIfNotExists(
                    DmsTableNames.DmsSchema,
                    DmsTableNames.BigIntTableType,
                    "Id",
                    "bigint"
                )
            );
            writer.AppendLine();
            writer.AppendLine(
                _dialect.CreateUserDefinedTableTypeIfNotExists(
                    DmsTableNames.DmsSchema,
                    DmsTableNames.UniqueIdentifierTableType,
                    "Id",
                    "uniqueidentifier"
                )
            );
            writer.AppendLine();
            return;
        }

        // PostgreSQL: functions (alphabetical, case-insensitive)
        writer.AppendLine(_dialect.CreateGetMaxChangeVersionFunction(DmsTableNames.DmsSchema));
        writer.AppendLine();
        writer.AppendLine(_dialect.CreateThrowErrorFunction(DmsTableNames.DmsSchema));
        writer.AppendLine();
        writer.AppendLine(_dialect.CreateUuidv5Function(DmsTableNames.DmsSchema));
        writer.AppendLine();
    }

    // ── Phase 5: Tables ─────────────────────────────────────────────────

    /// <summary>
    /// Emits core table definitions (primary keys, unique constraints, and check constraints only).
    /// </summary>
    private void EmitTables(SqlWriter writer)
    {
        writer.WritePhaseHeader(5, "Tables (PK/UNIQUE/CHECK only, no cross-table FKs)");

        // Alphabetical order by table name within the dms schema.
        EmitDescriptorTable(writer);
        EmitDocumentTable(writer);
        EmitDocumentCacheTable(writer);
        EmitEffectiveSchemaTable(writer);
        EmitReferentialIdentityTable(writer);
        EmitResourceKeyTable(writer);
        EmitSchemaComponentTable(writer);
    }

    /// <summary>
    /// Emits the <c>dms.Descriptor</c> table definition.
    /// </summary>
    private void EmitDescriptorTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_descriptorTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.DocumentIdColumnType, false)},"
            );
            // Denormalized from dms.Document at insert time and immutable thereafter, so descriptor
            // paging can root on this table without touching dms.Document. Excluded from the
            // stamping trigger's no-op diff (_descriptorStoredColumns).
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyId"), _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("Namespace"), StringType(255), false)},"
            );
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("CodeValue"), StringType(50), false)},");
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ShortDescription"), StringType(75), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("Description"), StringType(1024), true)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("EffectiveBeginDate"), DateType, true)},"
            );
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("EffectiveEndDate"), DateType, true)},");
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("Discriminator"), StringType(128), false)},"
            );
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("Uri"), StringType(306), false)},");
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("ContentVersion"), "bigint", false, "DF_Descriptor_ContentVersion", "0")},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("ContentLastModifiedAt"), DateTimeType, false, "DF_Descriptor_ContentLastModifiedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(_dialect.RenderNamedPrimaryKeyClause("PK_Descriptor", [Col("DocumentId")]));
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddUniqueConstraint(
                _descriptorTable,
                "UX_Descriptor_Uri_Discriminator",
                [Col("Uri"), Col("Discriminator")]
            )
        );
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the <c>dms.Document</c> table definition.
    /// </summary>
    private void EmitDocumentTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_documentTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.IdentityBigintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentUuid"), _dialect.UuidColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyId"), _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("CreatedByOwnershipTokenId"), _dialect.SmallintColumnType, true)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("ContentVersion"), "bigint", false, "DF_Document_ContentVersion", SequenceDefault)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("IdentityVersion"), "bigint", false, "DF_Document_IdentityVersion", SequenceDefault)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("ContentLastModifiedAt"), DateTimeType, false, "DF_Document_ContentLastModifiedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("IdentityLastModifiedAt"), DateTimeType, false, "DF_Document_IdentityLastModifiedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("CreatedAt"), DateTimeType, false, "DF_Document_CreatedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(_dialect.RenderNamedPrimaryKeyClause("PK_Document", [Col("DocumentId")]));
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddUniqueConstraint(_documentTable, "UX_Document_DocumentUuid", [Col("DocumentUuid")])
        );
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the <c>dms.DocumentCache</c> table definition.
    /// </summary>
    private void EmitDocumentCacheTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_documentCacheTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.DocumentIdColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentUuid"), _dialect.UuidColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceVersion"), StringType(32), false)},"
            );
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("Etag"), StringType(64), false)},");
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("ContentVersion"), "bigint", false)},");
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("LastModifiedAt"), DateTimeType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentJson"), _dialect.JsonColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("ComputedAt"), DateTimeType, false, "DF_DocumentCache_ComputedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(_dialect.RenderNamedPrimaryKeyClause("PK_DocumentCache", [Col("DocumentId")]));
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddUniqueConstraint(
                _documentCacheTable,
                "UX_DocumentCache_DocumentUuid",
                [Col("DocumentUuid")]
            )
        );
        writer.AppendLine();

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine(
                _dialect.AddCheckConstraint(
                    _documentCacheTable,
                    "CK_DocumentCache_JsonObject",
                    $"jsonb_typeof({_dialect.QuoteIdentifier("DocumentJson")}) = 'object'"
                )
            );
        }
        else
        {
            writer.AppendLine(
                _dialect.AddCheckConstraint(
                    _documentCacheTable,
                    "CK_DocumentCache_IsJsonObject",
                    $"ISJSON({_dialect.QuoteIdentifier("DocumentJson")}) = 1 AND LEFT(LTRIM({_dialect.QuoteIdentifier("DocumentJson")}), 1) = '{{'"
                )
            );
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the <c>dms.EffectiveSchema</c> table definition.
    /// </summary>
    private void EmitEffectiveSchemaTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_effectiveSchemaTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(_effectiveSchemaSingletonIdColumn, _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(_apiSchemaFormatVersionColumn, StringType(64), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(_effectiveSchemaHashColumn, StringType(64), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(_resourceKeyCountColumn, _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(_resourceKeySeedHashColumn, _dialect.RenderBinaryColumnType(32), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(_appliedAtColumn, DateTimeType, false, "DF_EffectiveSchema_AppliedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(
                _dialect.RenderNamedPrimaryKeyClause(
                    "PK_EffectiveSchema",
                    [_effectiveSchemaSingletonIdColumn]
                )
            );
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddCheckConstraint(
                _effectiveSchemaTable,
                "CK_EffectiveSchema_Singleton",
                $"{_dialect.QuoteIdentifier(_effectiveSchemaSingletonIdColumn.Value)} = 1"
            )
        );
        writer.AppendLine();

        var apiSchemaFormatVersionCheck =
            _dialect.Rules.Dialect == SqlDialect.Pgsql
                ? $"btrim({_dialect.QuoteIdentifier(_apiSchemaFormatVersionColumn.Value)}) <> ''"
                : $"LEN(LTRIM(RTRIM({_dialect.QuoteIdentifier(_apiSchemaFormatVersionColumn.Value)}))) > 0";

        writer.AppendLine(
            _dialect.AddCheckConstraint(
                _effectiveSchemaTable,
                "CK_EffectiveSchema_ApiSchemaFormatVersion_NotBlank",
                apiSchemaFormatVersionCheck
            )
        );
        writer.AppendLine();

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine(
                _dialect.AddCheckConstraint(
                    _effectiveSchemaTable,
                    "CK_EffectiveSchema_ResourceKeySeedHash_Length",
                    $"octet_length({_dialect.QuoteIdentifier(_resourceKeySeedHashColumn.Value)}) = 32"
                )
            );
            writer.AppendLine();
        }

        writer.AppendLine(
            _dialect.AddUniqueConstraint(
                _effectiveSchemaTable,
                "UX_EffectiveSchema_EffectiveSchemaHash",
                [_effectiveSchemaHashColumn]
            )
        );
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the <c>dms.ReferentialIdentity</c> table definition.
    /// </summary>
    private void EmitReferentialIdentityTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_referentialIdentityTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ReferentialId"), _dialect.UuidColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.DocumentIdColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyId"), _dialect.SmallintColumnType, false)},"
            );

            if (_dialect.Rules.Dialect == SqlDialect.Mssql)
            {
                // MSSQL: PK NONCLUSTERED + inline UNIQUE CLUSTERED
                writer.AppendLine(
                    _dialect.RenderNamedPrimaryKeyClause(
                        "PK_ReferentialIdentity",
                        [Col("ReferentialId")],
                        clustered: false
                    ) + ","
                );
                var clusteredCols = string.Join(
                    ", ",
                    new[] { Col("DocumentId"), Col("ResourceKeyId") }.Select(c =>
                        _dialect.QuoteIdentifier(c.Value)
                    )
                );
                writer.AppendLine(
                    $"CONSTRAINT {_dialect.QuoteIdentifier("UX_ReferentialIdentity_DocumentId_ResourceKeyId")} UNIQUE CLUSTERED ({clusteredCols})"
                );
            }
            else
            {
                writer.AppendLine(
                    _dialect.RenderNamedPrimaryKeyClause("PK_ReferentialIdentity", [Col("ReferentialId")])
                        + ","
                );
                var uniqueCols = string.Join(
                    ", ",
                    new[] { Col("DocumentId"), Col("ResourceKeyId") }.Select(c =>
                        _dialect.QuoteIdentifier(c.Value)
                    )
                );
                writer.AppendLine(
                    $"CONSTRAINT {_dialect.QuoteIdentifier("UX_ReferentialIdentity_DocumentId_ResourceKeyId")} UNIQUE ({uniqueCols})"
                );
            }
        }
        writer.AppendLine(");");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the <c>dms.ResourceKey</c> table definition.
    /// </summary>
    private void EmitResourceKeyTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_resourceKeyTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyId"), _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceVersion"), StringType(32), false)},"
            );
            writer.AppendLine(_dialect.RenderNamedPrimaryKeyClause("PK_ResourceKey", [Col("ResourceKeyId")]));
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddUniqueConstraint(
                _resourceKeyTable,
                "UX_ResourceKey_ProjectName_ResourceName",
                [Col("ProjectName"), Col("ResourceName")]
            )
        );
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the <c>dms.SchemaComponent</c> table definition.
    /// </summary>
    private void EmitSchemaComponentTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_schemaComponentTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("EffectiveSchemaHash"), StringType(64), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectEndpointName"), StringType(128), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectVersion"), StringType(32), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("IsExtensionProject"), BooleanType, false)},"
            );
            writer.AppendLine(
                _dialect.RenderNamedPrimaryKeyClause(
                    "PK_SchemaComponent",
                    [Col("EffectiveSchemaHash"), Col("ProjectEndpointName")]
                )
            );
        }
        writer.AppendLine(");");
        writer.AppendLine();
    }

    // ── Phase 6: Foreign Keys ───────────────────────────────────────────

    /// <summary>
    /// Emits cross-table foreign keys for core tables using <c>ALTER TABLE</c> statements.
    /// </summary>
    private void EmitForeignKeys(SqlWriter writer)
    {
        writer.WritePhaseHeader(6, "Foreign Keys");

        // Ordered by (table name, constraint name).

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _descriptorTable,
                "FK_Descriptor_Document",
                [Col("DocumentId")],
                _documentTable,
                [Col("DocumentId")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _descriptorTable,
                "FK_Descriptor_ResourceKey",
                [Col("ResourceKeyId")],
                _resourceKeyTable,
                [Col("ResourceKeyId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _documentTable,
                "FK_Document_ResourceKey",
                [Col("ResourceKeyId")],
                _resourceKeyTable,
                [Col("ResourceKeyId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _documentCacheTable,
                "FK_DocumentCache_Document",
                [Col("DocumentId")],
                _documentTable,
                [Col("DocumentId")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _referentialIdentityTable,
                "FK_ReferentialIdentity_Document",
                [Col("DocumentId")],
                _documentTable,
                [Col("DocumentId")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _referentialIdentityTable,
                "FK_ReferentialIdentity_ResourceKey",
                [Col("ResourceKeyId")],
                _resourceKeyTable,
                [Col("ResourceKeyId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _schemaComponentTable,
                "FK_SchemaComponent_EffectiveSchemaHash",
                [Col("EffectiveSchemaHash")],
                _effectiveSchemaTable,
                [Col("EffectiveSchemaHash")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();
    }

    // ── Phase 7: Indexes ────────────────────────────────────────────────

    /// <summary>
    /// Emits core indexes that are required in addition to constraint-implied indexes.
    /// </summary>
    private void EmitIndexes(SqlWriter writer)
    {
        writer.WritePhaseHeader(7, "Indexes");

        // Ordered by (table name, index name).
        //
        // Deliberately not emitted:
        // - dms.Descriptor (Uri, Discriminator): already indexed by the
        //   UX_Descriptor_Uri_Discriminator unique constraint.
        // - dms.Document (ResourceKeyId, DocumentId): descriptor paging roots on
        //   dms.Descriptor via IX_Descriptor_ResourceKeyId_DocumentId, and no other
        //   query path filters dms.Document by ResourceKeyId. FK_Document_ResourceKey
        //   needs no referencing-side index because dms.ResourceKey rows are never
        //   deleted or updated at runtime.
        // - dms.ReferentialIdentity (DocumentId): DocumentId-keyed access (FK cascade from
        //   dms.Document and the identity-maintenance triggers) is served by the leading
        //   column of UX_ReferentialIdentity_DocumentId_ResourceKeyId.

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _descriptorTable,
                "IX_Descriptor_ResourceKeyId_DocumentId",
                [Col("ResourceKeyId"), Col("DocumentId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _documentTable,
                "IX_Document_CreatedByOwnershipTokenId",
                [Col("CreatedByOwnershipTokenId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _documentCacheTable,
                "IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt",
                [Col("ProjectName"), Col("ResourceName"), Col("LastModifiedAt"), Col("DocumentId")]
            )
        );
        writer.AppendLine();
    }

    // ── Phase 8: Triggers ───────────────────────────────────────────────

    /// <summary>
    /// Emits core triggers: the dialect-specific descriptor stamping trigger on
    /// <c>dms.Descriptor</c>. The descriptor stamping trigger bumps
    /// <c>dms.Document.ContentVersion</c> / <c>ContentLastModifiedAt</c> on real value
    /// changes to a descriptor row, with a DB-level no-op guard that short-circuits
    /// when no stored descriptor column actually changed.
    /// </summary>
    private void EmitTriggers(SqlWriter writer)
    {
        writer.WritePhaseHeader(8, "Triggers");

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlDescriptorStampingTrigger(writer);
        }
        else
        {
            EmitMssqlDescriptorStampingTrigger(writer);
        }
    }

    // ── Descriptor stamping trigger (dms.Descriptor → dms.Document) ────────

    /// <summary>
    /// Stored columns on <c>dms.Descriptor</c> in the order they are emitted by
    /// <see cref="EmitDescriptorTable"/>, paired with their <see cref="ScalarKind"/>.
    /// The kind metadata is load-bearing for the MSSQL trigger: <see cref="ScalarKind.String"/>
    /// columns are compared via <c>CAST(... AS varbinary(max))</c> so that trailing-space-only
    /// and case-only changes (which default CI collation + ANSI padding would miss) are still
    /// detected — matching the byte-comparison behavior used by <c>[dms].[uuidv5]</c>.
    /// </summary>
    private static readonly IReadOnlyList<(DbColumnName Column, ScalarKind Kind)> _descriptorStoredColumns =
        new (DbColumnName, ScalarKind)[]
        {
            (new("Namespace"), ScalarKind.String),
            (new("CodeValue"), ScalarKind.String),
            (new("ShortDescription"), ScalarKind.String),
            (new("Description"), ScalarKind.String),
            (new("EffectiveBeginDate"), ScalarKind.Date),
            (new("EffectiveEndDate"), ScalarKind.Date),
            (new("Discriminator"), ScalarKind.String),
            (new("Uri"), ScalarKind.String),
        };

    /// <summary>
    /// Emits the PostgreSQL descriptor stamping trigger function and trigger.
    /// On INSERT, DELETE, or a real value change to any stored column of <c>dms.Descriptor</c>,
    /// bumps <c>dms.Document.ContentVersion</c> and <c>ContentLastModifiedAt</c> on
    /// the owning document row, then mirrors those captured stamps back to the descriptor
    /// (INSERT copies the existing document stamp; DELETE stamps the document only, since
    /// the descriptor row is already gone).
    /// A DB-level no-op guard (<c>IS DISTINCT FROM</c> across every stored column)
    /// short-circuits same-value UPDATEs so unchanged PUTs do not bump the stamps.
    /// </summary>
    private void EmitPgsqlDescriptorStampingTrigger(SqlWriter writer)
    {
        var descriptorTable = _dialect.QualifyTable(_descriptorTable);
        var documentTable = _dialect.QualifyTable(_documentTable);
        var sequenceName =
            $"{Quote(DmsTableNames.DmsSchema.Value)}.{Quote(DmsTableNames.ChangeVersionSequence)}";
        var funcName = $"{Quote(DmsTableNames.DmsSchema.Value)}.{Quote("TF_Descriptor_Stamp_Document")}";

        writer.AppendLine($"CREATE OR REPLACE FUNCTION {funcName}()");
        writer.AppendLine("RETURNS TRIGGER AS $func$");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            // No-op guard: if no stored column actually changed, skip the stamp.
            writer.AppendLine("IF TG_OP = 'UPDATE' THEN");
            using (writer.Indent())
            {
                writer.Append("IF NOT (");
                EmitPgsqlDescriptorValueDiffDisjunction(writer);
                writer.AppendLine(") THEN");
                using (writer.Indent())
                {
                    writer.AppendLine("RETURN NEW;");
                }
                writer.AppendLine("END IF;");
            }
            writer.AppendLine("END IF;");

            writer.AppendLine("IF TG_OP = 'INSERT' THEN");
            using (writer.Indent())
            {
                writer.AppendLine("WITH stamped AS (");
                using (writer.Indent())
                {
                    writer.Append("SELECT ");
                    writer.Append(Quote("DocumentId"));
                    writer.Append(", ");
                    writer.Append(Quote("ContentVersion"));
                    writer.Append(", ");
                    writer.Append(Quote("ContentLastModifiedAt"));
                    writer.AppendLine();
                    writer.Append("FROM ");
                    writer.AppendLine(documentTable);
                    writer.Append("WHERE ");
                    writer.Append(Quote("DocumentId"));
                    writer.Append(" = NEW.");
                    writer.Append(Quote("DocumentId"));
                    writer.AppendLine();
                }
                writer.AppendLine(")");
                EmitPgsqlDescriptorMirrorUpdateFromStamped(writer, descriptorTable);
            }
            writer.AppendLine("ELSIF TG_OP = 'UPDATE' THEN");
            using (writer.Indent())
            {
                writer.AppendLine("WITH stamped AS (");
                using (writer.Indent())
                {
                    writer.Append("UPDATE ");
                    writer.AppendLine(documentTable);
                    writer.Append("SET ");
                    writer.Append(Quote("ContentVersion"));
                    writer.Append(" = nextval('");
                    writer.Append(sequenceName);
                    writer.Append("'), ");
                    writer.Append(Quote("ContentLastModifiedAt"));
                    writer.AppendLine(" = now()");
                    writer.Append("WHERE ");
                    writer.Append(Quote("DocumentId"));
                    writer.Append(" = NEW.");
                    writer.Append(Quote("DocumentId"));
                    writer.AppendLine();
                    writer.Append("RETURNING ");
                    writer.Append(Quote("DocumentId"));
                    writer.Append(", ");
                    writer.Append(Quote("ContentVersion"));
                    writer.Append(", ");
                    writer.Append(Quote("ContentLastModifiedAt"));
                    writer.AppendLine();
                }
                writer.AppendLine(")");
                EmitPgsqlDescriptorMirrorUpdateFromStamped(writer, descriptorTable);
            }
            writer.AppendLine("ELSIF TG_OP = 'DELETE' THEN");
            using (writer.Indent())
            {
                // DocumentId is the Descriptor PK and the row is already gone in the AFTER
                // DELETE branch, so a mirror update can never match; stamp dms.Document only.
                writer.Append("UPDATE ");
                writer.AppendLine(documentTable);
                writer.Append("SET ");
                writer.Append(Quote("ContentVersion"));
                writer.Append(" = nextval('");
                writer.Append(sequenceName);
                writer.Append("'), ");
                writer.Append(Quote("ContentLastModifiedAt"));
                writer.AppendLine(" = now()");
                writer.Append("WHERE ");
                writer.Append(Quote("DocumentId"));
                writer.Append(" = OLD.");
                writer.Append(Quote("DocumentId"));
                writer.AppendLine(";");
                if (_sharedDescriptorTrackedChangeTable is not null)
                {
                    TrackedChangeTriggerBodyEmitter.EmitDescriptorTombstoneInsert(
                        writer,
                        _dialect,
                        _sharedDescriptorTrackedChangeTable,
                        imageRef: "OLD",
                        fromDeletedSet: false
                    );
                }
                writer.AppendLine("RETURN OLD;");
            }
            writer.AppendLine("END IF;");
            writer.AppendLine("RETURN NEW;");
        }
        writer.AppendLine("END;");
        writer.AppendLine("$func$ LANGUAGE plpgsql;");
        writer.AppendLine();

        writer.AppendLine(_dialect.DropTriggerIfExists(_descriptorTable, DescriptorStampingTriggerName));
        writer.AppendLine($"CREATE TRIGGER {Quote(DescriptorStampingTriggerName)}");
        using (writer.Indent())
        {
            writer.AppendLine($"AFTER INSERT OR UPDATE OR DELETE ON {descriptorTable}");
            writer.AppendLine("FOR EACH ROW");
            writer.AppendLine($"EXECUTE FUNCTION {funcName}();");
        }
        writer.AppendLine();
    }

    private void EmitPgsqlDescriptorMirrorUpdateFromStamped(SqlWriter writer, string descriptorTable)
    {
        writer.Append("UPDATE ");
        writer.Append(descriptorTable);
        writer.AppendLine(" r");
        writer.Append("SET ");
        writer.Append(Quote("ContentVersion"));
        writer.Append(" = stamped.");
        writer.Append(Quote("ContentVersion"));
        writer.Append(", ");
        writer.Append(Quote("ContentLastModifiedAt"));
        writer.Append(" = stamped.");
        writer.Append(Quote("ContentLastModifiedAt"));
        writer.AppendLine();
        writer.AppendLine("FROM stamped");
        writer.Append("WHERE r.");
        writer.Append(Quote("DocumentId"));
        writer.Append(" = stamped.");
        writer.Append(Quote("DocumentId"));
        writer.AppendLine(";");
    }

    /// <summary>
    /// Emits the SQL Server descriptor stamping trigger. INSERT rows copy the
    /// <c>dms.Document</c> defaults into the descriptor mirror; UPDATE rows flow
    /// through the null-safe per-column diff predicates across every stored descriptor
    /// column, so no-op UPDATEs produce no CTE rows and the downstream stamp/mirror
    /// updates stamp nothing. DELETE rows stamp the owning document before it is removed.
    /// </summary>
    private void EmitMssqlDescriptorStampingTrigger(SqlWriter writer)
    {
        var descriptorTable = _dialect.QualifyTable(_descriptorTable);
        var documentTable = _dialect.QualifyTable(_documentTable);
        var sequenceName =
            $"{Quote(DmsTableNames.DmsSchema.Value)}.{Quote(DmsTableNames.ChangeVersionSequence)}";
        var triggerName = $"{Quote(DmsTableNames.DmsSchema.Value)}.{Quote(DescriptorStampingTriggerName)}";
        var quotedKeyColumn = Quote("DocumentId");

        // CREATE OR ALTER TRIGGER must be the first statement in a T-SQL batch.
        writer.AppendLine("GO");
        writer.AppendLine($"CREATE OR ALTER TRIGGER {triggerName}");
        writer.AppendLine($"ON {descriptorTable}");
        writer.AppendLine("AFTER INSERT, UPDATE, DELETE");
        writer.AppendLine("AS");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("SET NOCOUNT ON;");
            writer.AppendLine("DECLARE @stamped TABLE (");
            using (writer.Indent())
            {
                writer.AppendLine("[DocumentId] bigint NOT NULL PRIMARY KEY,");
                writer.AppendLine("[ContentVersion] bigint NOT NULL,");
                writer.AppendLine("[ContentLastModifiedAt] datetime2(7) NOT NULL");
            }
            writer.AppendLine(");");
            writer.AppendLine(
                "INSERT INTO @stamped ([DocumentId], [ContentVersion], [ContentLastModifiedAt])"
            );
            writer.AppendLine("SELECT d.[DocumentId], d.[ContentVersion], d.[ContentLastModifiedAt]");
            writer.Append("FROM ");
            writer.Append(documentTable);
            writer.AppendLine(" d");
            writer.Append("INNER JOIN inserted i ON d.");
            writer.Append(quotedKeyColumn);
            writer.Append(" = i.");
            writer.AppendLine(quotedKeyColumn);
            writer.Append("LEFT JOIN deleted del ON del.");
            writer.Append(quotedKeyColumn);
            writer.Append(" = i.");
            writer.AppendLine(quotedKeyColumn);
            writer.Append("WHERE del.");
            writer.Append(quotedKeyColumn);
            writer.AppendLine(" IS NULL;");
            writer.AppendLine(";WITH affectedDocs AS (");
            using (writer.Indent())
            {
                writer.Append("SELECT i.");
                writer.AppendLine(quotedKeyColumn);
                writer.AppendLine("FROM inserted i");
                writer.Append("LEFT JOIN deleted del ON del.");
                writer.Append(quotedKeyColumn);
                writer.Append(" = i.");
                writer.AppendLine(quotedKeyColumn);
                writer.Append("WHERE del.");
                writer.Append(quotedKeyColumn);
                writer.Append(" IS NOT NULL AND (");
                EmitMssqlDescriptorColumnDiffDisjunction(writer, "i", "del");
                writer.Append(")");
                writer.AppendLine();
                // Branches are disjoint (changed updates vs pure deletes), so UNION ALL
                // skips the dedup sort.
                writer.AppendLine("UNION ALL");
                writer.Append("SELECT del.");
                writer.AppendLine(quotedKeyColumn);
                writer.AppendLine("FROM deleted del");
                writer.Append("LEFT JOIN inserted i ON i.");
                writer.Append(quotedKeyColumn);
                writer.Append(" = del.");
                writer.AppendLine(quotedKeyColumn);
                writer.Append("WHERE i.");
                writer.Append(quotedKeyColumn);
                writer.AppendLine(" IS NULL");
            }
            writer.AppendLine(")");

            writer.AppendLine("UPDATE d");
            writer.Append("SET d.");
            writer.Append(Quote("ContentVersion"));
            writer.Append(" = NEXT VALUE FOR ");
            writer.Append(sequenceName);
            writer.Append(", d.");
            writer.Append(Quote("ContentLastModifiedAt"));
            writer.AppendLine(" = sysutcdatetime()");
            writer.AppendLine(
                "OUTPUT inserted.[DocumentId], inserted.[ContentVersion], inserted.[ContentLastModifiedAt] INTO @stamped"
            );
            writer.Append("FROM ");
            writer.Append(documentTable);
            writer.AppendLine(" d");
            writer.Append("INNER JOIN affectedDocs a ON d.");
            writer.Append(quotedKeyColumn);
            writer.Append(" = a.");
            writer.Append(quotedKeyColumn);
            writer.AppendLine(";");
            // The guard bounds direct recursion: without it the mirror self-UPDATE re-fires
            // this trigger even with an empty workset (statement triggers fire on 0 rows),
            // which recurses to the nesting limit on databases with RECURSIVE_TRIGGERS ON.
            writer.AppendLine("IF EXISTS (SELECT 1 FROM @stamped)");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine("UPDATE r");
                writer.Append("SET r.");
                writer.Append(Quote("ContentVersion"));
                writer.Append(" = s.");
                writer.Append(Quote("ContentVersion"));
                writer.AppendLine(",");
                writer.Append("    r.");
                writer.Append(Quote("ContentLastModifiedAt"));
                writer.Append(" = s.");
                writer.Append(Quote("ContentLastModifiedAt"));
                writer.AppendLine();
                writer.Append("FROM ");
                writer.Append(descriptorTable);
                writer.AppendLine(" r");
                writer.Append("INNER JOIN @stamped s ON s.");
                writer.Append(quotedKeyColumn);
                writer.Append(" = r.");
                writer.Append(quotedKeyColumn);
                writer.AppendLine(";");
            }
            writer.AppendLine("END");
            if (_sharedDescriptorTrackedChangeTable is not null)
            {
                writer.AppendLine(
                    "IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)"
                );
                writer.AppendLine("BEGIN");
                using (writer.Indent())
                {
                    TrackedChangeTriggerBodyEmitter.EmitDescriptorTombstoneInsert(
                        writer,
                        _dialect,
                        _sharedDescriptorTrackedChangeTable,
                        imageRef: "del",
                        fromDeletedSet: true
                    );
                }
                writer.AppendLine("END");
            }
        }
        writer.AppendLine("END;");
        // Close the batch so that subsequent DDL starts in a fresh batch.
        writer.AppendLine("GO");
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the PostgreSQL <c>OLD.col IS DISTINCT FROM NEW.col</c> disjunction across
    /// the stored descriptor columns, matching the form used by
    /// <c>RelationalModelDdlEmitter.EmitPgsqlValueDiffDisjunction</c>.
    /// </summary>
    private void EmitPgsqlDescriptorValueDiffDisjunction(SqlWriter writer)
    {
        for (int i = 0; i < _descriptorStoredColumns.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" OR ");
            }
            var col = Quote(_descriptorStoredColumns[i].Column.Value);
            writer.Append("OLD.");
            writer.Append(col);
            writer.Append(" IS DISTINCT FROM NEW.");
            writer.Append(col);
        }
    }

    /// <summary>
    /// Emits a MSSQL null-safe inequality disjunction across the stored descriptor
    /// columns. String columns are wrapped in <c>CAST(... AS varbinary(max))</c> so
    /// trailing-space-only and case-only changes are detected — mirrors
    /// <c>RelationalModelDdlEmitter.EmitMssqlColumnValueDiffDisjunction</c>.
    /// </summary>
    private void EmitMssqlDescriptorColumnDiffDisjunction(
        SqlWriter writer,
        string leftAlias,
        string rightAlias
    )
    {
        for (int i = 0; i < _descriptorStoredColumns.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(" OR ");
            }
            var quotedColumn = Quote(_descriptorStoredColumns[i].Column.Value);
            MssqlTriggerDiffEmitter.EmitNullSafeNotEqual(
                writer,
                leftAlias,
                quotedColumn,
                rightAlias,
                quotedColumn,
                _descriptorStoredColumns[i].Kind
            );
        }
    }

    private string Quote(string identifier) => _dialect.QuoteIdentifier(identifier);
}
