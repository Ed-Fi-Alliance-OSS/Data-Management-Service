// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits deterministic DDL for the core <c>dms.*</c> schema objects.
/// <para>
/// This includes tables, constraints, indexes, sequences, and journaling
/// triggers required by the v1 object inventory defined in
/// <c>reference/design/backend-redesign/design-docs/ddl-generation.md</c>.
/// </para>
/// <para>
/// Emission follows a strict phased order to satisfy dependency requirements
/// and ensure deterministic, byte-for-byte stable output:
/// <list type="number">
/// <item>Schemas</item>
/// <item>Extensions (pgcrypto for PostgreSQL; no-op for SQL Server)</item>
/// <item>Sequences</item>
/// <item>Functions (UUIDv5 helper)</item>
/// <item>Tables (PK / UNIQUE / CHECK inline; no cross-table FKs)</item>
/// <item>Foreign keys (ALTER TABLE ADD CONSTRAINT)</item>
/// <item>Indexes</item>
/// <item>Triggers</item>
/// </list>
/// </para>
/// </summary>
public sealed class CoreDdlEmitter(ISqlDialect dialect)
{
    private readonly ISqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

    private static readonly DbTableName _descriptorTable = DmsTableNames.Descriptor;
    private static readonly DbTableName _documentTable = DmsTableNames.Document;
    private static readonly DbTableName _documentCacheTable = DmsTableNames.DocumentCache;
    private static readonly DbTableName _documentChangeEventTable = DmsTableNames.DocumentChangeEvent;
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
        _dialect.RenderSequenceDefaultExpression(DmsTableNames.DmsSchema, "ChangeVersionSequence");

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
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 1: Schemas");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

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
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 3: Sequences");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        EmitChangeVersionSequence(writer);
    }

    /// <summary>
    /// Emits the change-version sequence used for deterministic version stamping.
    /// </summary>
    private void EmitChangeVersionSequence(SqlWriter writer)
    {
        writer.AppendLine(
            _dialect.CreateSequenceIfNotExists(DmsTableNames.DmsSchema, "ChangeVersionSequence")
        );
        writer.AppendLine();
    }

    // ── Phase 4: Functions ──────────────────────────────────────────────

    /// <summary>
    /// Emits database functions required by core triggers and relational model triggers.
    /// The UUIDv5 helper must exist before any triggers that call it.
    /// </summary>
    private void EmitFunctions(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 4: Functions");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        // CREATE OR ALTER FUNCTION must be the first statement in a T-SQL batch.
        bool isMssql = _dialect.Rules.Dialect == SqlDialect.Mssql;
        if (isMssql)
        {
            writer.AppendLine("GO");
        }

        writer.AppendLine(_dialect.CreateUuidv5Function(DmsTableNames.DmsSchema));

        if (isMssql)
        {
            writer.AppendLine("GO");
        }

        writer.AppendLine();
    }

    // ── Phase 5: Tables ─────────────────────────────────────────────────

    /// <summary>
    /// Emits core table definitions (primary keys, unique constraints, and check constraints only).
    /// </summary>
    private void EmitTables(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 5: Tables (PK/UNIQUE/CHECK only, no cross-table FKs)");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        // Alphabetical order by table name within the dms schema.
        EmitDescriptorTable(writer);
        EmitDocumentTable(writer);
        EmitDocumentCacheTable(writer);
        EmitDocumentChangeEventTable(writer);
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
    /// Emits the <c>dms.DocumentChangeEvent</c> table definition.
    /// </summary>
    private void EmitDocumentChangeEventTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_documentChangeEventTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("ChangeVersion"), "bigint", false)},");
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.DocumentIdColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyId"), _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("CreatedAt"), DateTimeType, false, "DF_DocumentChangeEvent_CreatedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(
                _dialect.RenderNamedPrimaryKeyClause(
                    "PK_DocumentChangeEvent",
                    [Col("ChangeVersion"), Col("DocumentId")]
                )
            );
        }
        writer.AppendLine(");");
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
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 6: Foreign Keys");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

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
                _documentChangeEventTable,
                "FK_DocumentChangeEvent_Document",
                [Col("DocumentId")],
                _documentTable,
                [Col("DocumentId")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _documentChangeEventTable,
                "FK_DocumentChangeEvent_ResourceKey",
                [Col("ResourceKeyId")],
                _resourceKeyTable,
                [Col("ResourceKeyId")]
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
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 7: Indexes");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        // Ordered by (table name, index name).

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _descriptorTable,
                "IX_Descriptor_Uri_Discriminator",
                [Col("Uri"), Col("Discriminator")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _documentTable,
                "IX_Document_ResourceKeyId_DocumentId",
                [Col("ResourceKeyId"), Col("DocumentId")]
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

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _documentChangeEventTable,
                "IX_DocumentChangeEvent_DocumentId",
                [Col("DocumentId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _documentChangeEventTable,
                "IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion",
                [Col("ResourceKeyId"), Col("ChangeVersion"), Col("DocumentId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _referentialIdentityTable,
                "IX_ReferentialIdentity_DocumentId",
                [Col("DocumentId")]
            )
        );
        writer.AppendLine();
    }

    // ── Phase 8: Triggers ───────────────────────────────────────────────

    /// <summary>
    /// Emits core triggers, including dialect-specific document journaling triggers.
    /// </summary>
    private void EmitTriggers(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 8: Triggers");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlJournalingTrigger(writer);
        }
        else
        {
            EmitMssqlJournalingTrigger(writer);
        }
    }

    /// <summary>
    /// Emits the PostgreSQL document journaling trigger function and trigger, inserting rows into
    /// <c>dms.DocumentChangeEvent</c> when <c>dms.Document.ContentVersion</c> changes.
    /// </summary>
    private void EmitPgsqlJournalingTrigger(SqlWriter writer)
    {
        var docTable = _dialect.QualifyTable(_documentTable);
        var changeTable = _dialect.QualifyTable(_documentChangeEventTable);
        var funcName = $"{Quote(DmsTableNames.DmsSchema.Value)}.{Quote("TF_Document_Journal")}";

        // Row-level trigger function. Uses FOR EACH ROW instead of statement-level
        // transition tables because PostgreSQL 16 does not support transition tables
        // with column lists or multiple events.
        writer.AppendLine($"CREATE OR REPLACE FUNCTION {funcName}()");
        writer.AppendLine("RETURNS TRIGGER AS $func$");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"INSERT INTO {changeTable} ({Quote("ChangeVersion")}, {Quote("DocumentId")}, {Quote("ResourceKeyId")}, {Quote("CreatedAt")})"
            );
            writer.AppendLine(
                $"VALUES (NEW.{Quote("ContentVersion")}, NEW.{Quote("DocumentId")}, NEW.{Quote("ResourceKeyId")}, now());"
            );
            writer.AppendLine("RETURN NEW;");
        }
        writer.AppendLine("END;");
        writer.AppendLine("$func$ LANGUAGE plpgsql;");
        writer.AppendLine();

        // Drop and recreate trigger
        writer.AppendLine(_dialect.DropTriggerIfExists(_documentTable, "TR_Document_Journal"));
        writer.AppendLine($"CREATE TRIGGER {Quote("TR_Document_Journal")}");
        using (writer.Indent())
        {
            writer.AppendLine($"AFTER INSERT OR UPDATE OF {Quote("ContentVersion")} ON {docTable}");
            writer.AppendLine("FOR EACH ROW");
            writer.AppendLine($"EXECUTE FUNCTION {funcName}();");
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the SQL Server document journaling trigger, inserting rows into
    /// <c>dms.DocumentChangeEvent</c> on document inserts and on updates that modify
    /// <c>dms.Document.ContentVersion</c>.
    /// </summary>
    private void EmitMssqlJournalingTrigger(SqlWriter writer)
    {
        var docTable = _dialect.QualifyTable(_documentTable);
        var changeTable = _dialect.QualifyTable(_documentChangeEventTable);
        var triggerName = $"{Quote(DmsTableNames.DmsSchema.Value)}.{Quote("TR_Document_Journal")}";

        // CREATE OR ALTER TRIGGER must be the first statement in a T-SQL batch.
        writer.AppendLine("GO");
        writer.AppendLine($"CREATE OR ALTER TRIGGER {triggerName}");
        writer.AppendLine($"ON {docTable}");
        writer.AppendLine("AFTER INSERT, UPDATE");
        writer.AppendLine("AS");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("SET NOCOUNT ON;");
            writer.AppendLine($"IF UPDATE({Quote("ContentVersion")}) OR NOT EXISTS (SELECT 1 FROM deleted)");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine(
                    $"INSERT INTO {changeTable} ({Quote("ChangeVersion")}, {Quote("DocumentId")}, {Quote("ResourceKeyId")}, {Quote("CreatedAt")})"
                );
                writer.AppendLine(
                    $"SELECT i.{Quote("ContentVersion")}, i.{Quote("DocumentId")}, i.{Quote("ResourceKeyId")}, sysutcdatetime()"
                );
                writer.AppendLine("FROM inserted i;");
            }
            writer.AppendLine("END");
        }
        writer.AppendLine("END;");
        // Close the batch so that subsequent DDL (e.g., relational model DDL
        // concatenated after core DDL) starts in a fresh batch.
        writer.AppendLine("GO");
        writer.AppendLine();
    }

    private string Quote(string identifier) => _dialect.QuoteIdentifier(identifier);
}
