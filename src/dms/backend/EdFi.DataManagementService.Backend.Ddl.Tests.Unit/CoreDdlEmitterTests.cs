// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

// ═══════════════════════════════════════════════════════════════════
// Phase 3: Deterministic ordering verification
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_CoreDdlEmitter_With_PgsqlDialect_Emitting_Twice
{
    private string _first = default!;
    private string _second = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new CoreDdlEmitter(new PgsqlDialect(new PgsqlDialectRules()));
        _first = emitter.Emit();
        _second = emitter.Emit();
    }

    [Test]
    public void It_should_produce_byte_for_byte_identical_output()
    {
        _first.Should().Be(_second);
    }

    [Test]
    public void It_should_produce_non_empty_output()
    {
        _first.Should().NotBeNullOrWhiteSpace();
    }
}

[TestFixture]
public class Given_CoreDdlEmitter_With_MssqlDialect_Emitting_Twice
{
    private string _first = default!;
    private string _second = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new CoreDdlEmitter(new MssqlDialect(new MssqlDialectRules()));
        _first = emitter.Emit();
        _second = emitter.Emit();
    }

    [Test]
    public void It_should_produce_byte_for_byte_identical_output()
    {
        _first.Should().Be(_second);
    }

    [Test]
    public void It_should_produce_non_empty_output()
    {
        _first.Should().NotBeNullOrWhiteSpace();
    }
}

// ═══════════════════════════════════════════════════════════════════
// Phase 4: Snapshot tests – PostgreSQL dialect
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_CoreDdlEmitter_With_PgsqlDialect
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new CoreDdlEmitter(new PgsqlDialect(new PgsqlDialectRules()));
        _ddl = emitter.Emit();
    }

    // ── Phased ordering ─────────────────────────────────────────────

    [Test]
    public void It_should_emit_phases_in_correct_order()
    {
        var phase1 = _ddl.IndexOf("Phase 1: Schemas", StringComparison.Ordinal);
        var phase2 = _ddl.IndexOf("Phase 2: Sequences", StringComparison.Ordinal);
        var phase3 = _ddl.IndexOf("Phase 3: Tables", StringComparison.Ordinal);
        var phase4 = _ddl.IndexOf("Phase 4: Foreign Keys", StringComparison.Ordinal);
        var phase5 = _ddl.IndexOf("Phase 5: Indexes", StringComparison.Ordinal);
        var phase6 = _ddl.IndexOf("Phase 6: Triggers", StringComparison.Ordinal);

        phase1.Should().BeGreaterOrEqualTo(0);
        phase2.Should().BeGreaterThan(phase1);
        phase3.Should().BeGreaterThan(phase2);
        phase4.Should().BeGreaterThan(phase3);
        phase5.Should().BeGreaterThan(phase4);
        phase6.Should().BeGreaterThan(phase5);
    }

    // ── Schema and sequence ─────────────────────────────────────────

    [Test]
    public void It_should_create_dms_schema()
    {
        _ddl.Should().Contain("CREATE SCHEMA IF NOT EXISTS \"dms\"");
    }

    [Test]
    public void It_should_create_change_version_sequence()
    {
        _ddl.Should().Contain("CREATE SEQUENCE IF NOT EXISTS \"dms\".\"ChangeVersionSequence\"");
    }

    // ── Tables ──────────────────────────────────────────────────────

    [Test]
    public void It_should_create_descriptor_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"dms\".\"Descriptor\"");
    }

    [Test]
    public void It_should_create_document_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"dms\".\"Document\"");
    }

    [Test]
    public void It_should_create_document_cache_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"dms\".\"DocumentCache\"");
    }

    [Test]
    public void It_should_create_document_change_event_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"dms\".\"DocumentChangeEvent\"");
    }

    [Test]
    public void It_should_create_effective_schema_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"dms\".\"EffectiveSchema\"");
    }

    [Test]
    public void It_should_create_referential_identity_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"dms\".\"ReferentialIdentity\"");
    }

    [Test]
    public void It_should_create_resource_key_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"dms\".\"ResourceKey\"");
    }

    [Test]
    public void It_should_create_schema_component_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"dms\".\"SchemaComponent\"");
    }

    // ── Tables in alphabetical order ────────────────────────────────

    [Test]
    public void It_should_emit_tables_in_alphabetical_order()
    {
        var descriptor = _ddl.IndexOf("\"dms\".\"Descriptor\"", StringComparison.Ordinal);
        var document = _ddl.IndexOf("\"dms\".\"Document\"", StringComparison.Ordinal);
        var documentCache = _ddl.IndexOf("\"dms\".\"DocumentCache\"", StringComparison.Ordinal);
        var documentChangeEvent = _ddl.IndexOf("\"dms\".\"DocumentChangeEvent\"", StringComparison.Ordinal);
        var effectiveSchema = _ddl.IndexOf("\"dms\".\"EffectiveSchema\"", StringComparison.Ordinal);
        var referentialIdentity = _ddl.IndexOf("\"dms\".\"ReferentialIdentity\"", StringComparison.Ordinal);
        var resourceKey = _ddl.IndexOf("\"dms\".\"ResourceKey\"", StringComparison.Ordinal);
        var schemaComponent = _ddl.IndexOf("\"dms\".\"SchemaComponent\"", StringComparison.Ordinal);

        descriptor.Should().BeLessThan(document);
        document.Should().BeLessThan(documentCache);
        documentCache.Should().BeLessThan(documentChangeEvent);
        documentChangeEvent.Should().BeLessThan(effectiveSchema);
        effectiveSchema.Should().BeLessThan(referentialIdentity);
        referentialIdentity.Should().BeLessThan(resourceKey);
        resourceKey.Should().BeLessThan(schemaComponent);
    }

    // ── PG-specific column types ────────────────────────────────────

    [Test]
    public void It_should_use_pg_identity_type_for_document_id()
    {
        _ddl.Should().Contain("bigint GENERATED ALWAYS AS IDENTITY");
    }

    [Test]
    public void It_should_use_uuid_type()
    {
        _ddl.Should().Contain("uuid NOT NULL");
    }

    [Test]
    public void It_should_use_bytea_type_for_resource_key_seed_hash()
    {
        _ddl.Should().Contain("\"ResourceKeySeedHash\" bytea NOT NULL");
    }

    [Test]
    public void It_should_use_jsonb_type_for_document_json()
    {
        _ddl.Should().Contain("\"DocumentJson\" jsonb NOT NULL");
    }

    [Test]
    public void It_should_use_boolean_type_for_is_extension_project()
    {
        _ddl.Should().Contain("\"IsExtensionProject\" boolean NOT NULL");
    }

    [Test]
    public void It_should_use_varchar_type()
    {
        _ddl.Should().Contain("varchar(");
    }

    [Test]
    public void It_should_use_timestamp_with_time_zone_type()
    {
        _ddl.Should().Contain("timestamp with time zone");
    }

    // ── PG-specific defaults ────────────────────────────────────────

    [Test]
    public void It_should_use_nextval_for_sequence_defaults()
    {
        _ddl.Should().Contain("nextval('\"dms\".\"ChangeVersionSequence\"')");
    }

    [Test]
    public void It_should_use_now_for_timestamp_defaults()
    {
        _ddl.Should().Contain("DEFAULT now()");
    }

    // ── Primary keys ────────────────────────────────────────────────

    [Test]
    public void It_should_have_named_pk_for_descriptor()
    {
        _ddl.Should().Contain("CONSTRAINT \"PK_Descriptor\" PRIMARY KEY (\"DocumentId\")");
    }

    [Test]
    public void It_should_have_named_pk_for_document()
    {
        _ddl.Should().Contain("CONSTRAINT \"PK_Document\" PRIMARY KEY (\"DocumentId\")");
    }

    [Test]
    public void It_should_have_composite_pk_for_document_change_event()
    {
        _ddl.Should()
            .Contain("CONSTRAINT \"PK_DocumentChangeEvent\" PRIMARY KEY (\"ChangeVersion\", \"DocumentId\")");
    }

    [Test]
    public void It_should_have_composite_pk_for_schema_component()
    {
        _ddl.Should()
            .Contain(
                "CONSTRAINT \"PK_SchemaComponent\" PRIMARY KEY (\"EffectiveSchemaHash\", \"ProjectEndpointName\")"
            );
    }

    [Test]
    public void It_should_have_simple_pk_for_referential_identity()
    {
        _ddl.Should().Contain("CONSTRAINT \"PK_ReferentialIdentity\" PRIMARY KEY (\"ReferentialId\")");
    }

    [Test]
    public void It_should_not_contain_clustered_keyword()
    {
        _ddl.Should().NotContain("CLUSTERED");
    }

    // ── UNIQUE constraints ──────────────────────────────────────────

    [Test]
    public void It_should_have_unique_on_descriptor_uri_discriminator()
    {
        _ddl.Should().Contain("\"UX_Descriptor_Uri_Discriminator\" UNIQUE");
    }

    [Test]
    public void It_should_have_unique_on_document_uuid()
    {
        _ddl.Should().Contain("\"UX_Document_DocumentUuid\" UNIQUE");
    }

    [Test]
    public void It_should_have_unique_on_document_cache_uuid()
    {
        _ddl.Should().Contain("\"UX_DocumentCache_DocumentUuid\" UNIQUE");
    }

    [Test]
    public void It_should_have_unique_on_resource_key_project_resource()
    {
        _ddl.Should().Contain("\"UX_ResourceKey_ProjectName_ResourceName\" UNIQUE");
    }

    [Test]
    public void It_should_have_unique_on_effective_schema_hash()
    {
        _ddl.Should().Contain("\"UX_EffectiveSchema_EffectiveSchemaHash\" UNIQUE");
    }

    // ── CHECK constraints ───────────────────────────────────────────

    [Test]
    public void It_should_have_singleton_check_on_effective_schema()
    {
        _ddl.Should().Contain("\"CK_EffectiveSchema_Singleton\"");
        _ddl.Should().Contain("\"EffectiveSchemaSingletonId\" = 1");
    }

    [Test]
    public void It_should_have_pg_only_bytea_length_check()
    {
        _ddl.Should().Contain("\"CK_EffectiveSchema_ResourceKeySeedHash_Length\"");
        _ddl.Should().Contain("octet_length(\"ResourceKeySeedHash\") = 32");
    }

    [Test]
    public void It_should_have_pg_jsonb_check_on_document_cache()
    {
        _ddl.Should().Contain("\"CK_DocumentCache_JsonObject\"");
        _ddl.Should().Contain("jsonb_typeof(\"DocumentJson\") = 'object'");
    }

    [Test]
    public void It_should_not_have_mssql_json_check()
    {
        _ddl.Should().NotContain("CK_DocumentCache_IsJsonObject");
        _ddl.Should().NotContain("ISJSON");
    }

    // ── Nullable column ─────────────────────────────────────────────

    [Test]
    public void It_should_have_nullable_description_in_descriptor()
    {
        _ddl.Should().Contain("\"Description\" varchar(1024) NULL");
    }

    // ── Foreign keys ────────────────────────────────────────────────

    [Test]
    public void It_should_have_fk_descriptor_document()
    {
        _ddl.Should().Contain("\"FK_Descriptor_Document\"");
        _ddl.Should().Contain("ON DELETE CASCADE");
    }

    [Test]
    public void It_should_have_fk_document_resource_key()
    {
        _ddl.Should().Contain("\"FK_Document_ResourceKey\"");
    }

    [Test]
    public void It_should_have_fk_document_cache_document()
    {
        _ddl.Should().Contain("\"FK_DocumentCache_Document\"");
    }

    [Test]
    public void It_should_have_fk_document_change_event_document()
    {
        _ddl.Should().Contain("\"FK_DocumentChangeEvent_Document\"");
    }

    [Test]
    public void It_should_have_fk_document_change_event_resource_key()
    {
        _ddl.Should().Contain("\"FK_DocumentChangeEvent_ResourceKey\"");
    }

    [Test]
    public void It_should_have_fk_referential_identity_document()
    {
        _ddl.Should().Contain("\"FK_ReferentialIdentity_Document\"");
    }

    [Test]
    public void It_should_have_fk_referential_identity_resource_key()
    {
        _ddl.Should().Contain("\"FK_ReferentialIdentity_ResourceKey\"");
    }

    [Test]
    public void It_should_have_fk_schema_component_effective_schema_hash()
    {
        _ddl.Should().Contain("\"FK_SchemaComponent_EffectiveSchemaHash\"");
    }

    // ── Indexes ─────────────────────────────────────────────────────

    [Test]
    public void It_should_have_index_descriptor_uri_discriminator()
    {
        _ddl.Should().Contain("\"IX_Descriptor_Uri_Discriminator\"");
    }

    [Test]
    public void It_should_have_index_document_resource_key_id()
    {
        _ddl.Should().Contain("\"IX_Document_ResourceKeyId_DocumentId\"");
    }

    [Test]
    public void It_should_have_index_document_cache_composite()
    {
        _ddl.Should().Contain("\"IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt\"");
    }

    [Test]
    public void It_should_have_index_document_change_event_document_id()
    {
        _ddl.Should().Contain("\"IX_DocumentChangeEvent_DocumentId\"");
    }

    [Test]
    public void It_should_have_index_document_change_event_resource_key_change_version()
    {
        _ddl.Should().Contain("\"IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion\"");
    }

    [Test]
    public void It_should_have_index_referential_identity_document_id()
    {
        _ddl.Should().Contain("\"IX_ReferentialIdentity_DocumentId\"");
    }

    // ── PG trigger ──────────────────────────────────────────────────

    [Test]
    public void It_should_create_trigger_function()
    {
        _ddl.Should().Contain("CREATE OR REPLACE FUNCTION \"dms\".\"TF_Document_Journal\"()");
        _ddl.Should().Contain("LANGUAGE plpgsql");
    }

    [Test]
    public void It_should_drop_existing_trigger_before_creating()
    {
        _ddl.Should().Contain("DROP TRIGGER IF EXISTS \"TR_Document_Journal\" ON \"dms\".\"Document\"");
    }

    [Test]
    public void It_should_create_trigger_on_document()
    {
        _ddl.Should().Contain("CREATE TRIGGER \"TR_Document_Journal\"");
        _ddl.Should().Contain("AFTER INSERT OR UPDATE OF \"ContentVersion\"");
        _ddl.Should().Contain("REFERENCING NEW TABLE AS new_table");
        _ddl.Should().Contain("FOR EACH STATEMENT");
        _ddl.Should().Contain("EXECUTE FUNCTION \"dms\".\"TF_Document_Journal\"()");
    }

    [Test]
    public void It_should_use_distinct_on_in_trigger_function()
    {
        _ddl.Should().Contain("DISTINCT ON (\"DocumentId\")");
    }

    [Test]
    public void It_should_not_contain_mssql_trigger_syntax()
    {
        _ddl.Should().NotContain("CREATE OR ALTER TRIGGER");
        _ddl.Should().NotContain("SET NOCOUNT ON");
        _ddl.Should().NotContain("sysutcdatetime()");
    }

    // ── No authorization objects ─────────────────────────────────────

    [Test]
    public void It_should_not_contain_auth_schema_objects()
    {
        _ddl.Should().NotContain("\"auth\"");
        _ddl.Should().NotContain("DocumentSubject");
    }

    // ── Canonicalization ─────────────────────────────────────────────

    [Test]
    public void It_should_use_unix_line_endings()
    {
        _ddl.Should().NotContain("\r");
    }

    [Test]
    public void It_should_not_have_trailing_whitespace_on_any_line()
    {
        var lines = _ddl.Split('\n');
        foreach (var line in lines)
        {
            line.Should().Be(line.TrimEnd(), $"Line has trailing whitespace: [{line}]");
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// Phase 4: Snapshot tests – SQL Server dialect
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_CoreDdlEmitter_With_MssqlDialect
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new CoreDdlEmitter(new MssqlDialect(new MssqlDialectRules()));
        _ddl = emitter.Emit();
    }

    // ── Phased ordering ─────────────────────────────────────────────

    [Test]
    public void It_should_emit_phases_in_correct_order()
    {
        var phase1 = _ddl.IndexOf("Phase 1: Schemas", StringComparison.Ordinal);
        var phase2 = _ddl.IndexOf("Phase 2: Sequences", StringComparison.Ordinal);
        var phase3 = _ddl.IndexOf("Phase 3: Tables", StringComparison.Ordinal);
        var phase4 = _ddl.IndexOf("Phase 4: Foreign Keys", StringComparison.Ordinal);
        var phase5 = _ddl.IndexOf("Phase 5: Indexes", StringComparison.Ordinal);
        var phase6 = _ddl.IndexOf("Phase 6: Triggers", StringComparison.Ordinal);

        phase1.Should().BeGreaterOrEqualTo(0);
        phase2.Should().BeGreaterThan(phase1);
        phase3.Should().BeGreaterThan(phase2);
        phase4.Should().BeGreaterThan(phase3);
        phase5.Should().BeGreaterThan(phase4);
        phase6.Should().BeGreaterThan(phase5);
    }

    // ── Schema and sequence ─────────────────────────────────────────

    [Test]
    public void It_should_create_dms_schema_with_catalog_check()
    {
        _ddl.Should().Contain("IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dms')");
    }

    [Test]
    public void It_should_create_change_version_sequence_with_catalog_check()
    {
        _ddl.Should().Contain("sys.sequences");
        _ddl.Should().Contain("[dms].[ChangeVersionSequence]");
    }

    // ── Tables ──────────────────────────────────────────────────────

    [Test]
    public void It_should_create_all_eight_tables()
    {
        _ddl.Should().Contain("[dms].[Descriptor]");
        _ddl.Should().Contain("[dms].[Document]");
        _ddl.Should().Contain("[dms].[DocumentCache]");
        _ddl.Should().Contain("[dms].[DocumentChangeEvent]");
        _ddl.Should().Contain("[dms].[EffectiveSchema]");
        _ddl.Should().Contain("[dms].[ReferentialIdentity]");
        _ddl.Should().Contain("[dms].[ResourceKey]");
        _ddl.Should().Contain("[dms].[SchemaComponent]");
    }

    [Test]
    public void It_should_use_object_id_check_for_tables()
    {
        _ddl.Should().Contain("IF OBJECT_ID(N'dms.Descriptor', N'U') IS NULL");
        _ddl.Should().Contain("IF OBJECT_ID(N'dms.Document', N'U') IS NULL");
    }

    // ── MSSQL-specific column types ─────────────────────────────────

    [Test]
    public void It_should_use_mssql_identity_type_for_document_id()
    {
        _ddl.Should().Contain("bigint IDENTITY(1,1)");
    }

    [Test]
    public void It_should_use_uniqueidentifier_type()
    {
        _ddl.Should().Contain("uniqueidentifier NOT NULL");
    }

    [Test]
    public void It_should_use_binary_type_for_resource_key_seed_hash()
    {
        _ddl.Should().Contain("[ResourceKeySeedHash] binary(32) NOT NULL");
    }

    [Test]
    public void It_should_use_nvarchar_max_for_document_json()
    {
        _ddl.Should().Contain("[DocumentJson] nvarchar(max) NOT NULL");
    }

    [Test]
    public void It_should_use_bit_type_for_is_extension_project()
    {
        _ddl.Should().Contain("[IsExtensionProject] bit NOT NULL");
    }

    [Test]
    public void It_should_use_nvarchar_type()
    {
        _ddl.Should().Contain("nvarchar(");
    }

    [Test]
    public void It_should_use_datetime2_type()
    {
        _ddl.Should().Contain("datetime2(7)");
    }

    // ── MSSQL-specific defaults ─────────────────────────────────────

    [Test]
    public void It_should_use_next_value_for_sequence_defaults()
    {
        _ddl.Should().Contain("(NEXT VALUE FOR [dms].[ChangeVersionSequence])");
    }

    [Test]
    public void It_should_use_sysutcdatetime_for_timestamp_defaults()
    {
        _ddl.Should().Contain("DEFAULT (sysutcdatetime())");
    }

    // ── MSSQL named default constraints ─────────────────────────────

    [Test]
    public void It_should_have_named_default_for_document_content_version()
    {
        _ddl.Should().Contain("CONSTRAINT [DF_Document_ContentVersion] DEFAULT");
    }

    [Test]
    public void It_should_have_named_default_for_document_identity_version()
    {
        _ddl.Should().Contain("CONSTRAINT [DF_Document_IdentityVersion] DEFAULT");
    }

    [Test]
    public void It_should_have_named_default_for_document_content_last_modified_at()
    {
        _ddl.Should().Contain("CONSTRAINT [DF_Document_ContentLastModifiedAt] DEFAULT");
    }

    [Test]
    public void It_should_have_named_default_for_document_identity_last_modified_at()
    {
        _ddl.Should().Contain("CONSTRAINT [DF_Document_IdentityLastModifiedAt] DEFAULT");
    }

    [Test]
    public void It_should_have_named_default_for_document_created_at()
    {
        _ddl.Should().Contain("CONSTRAINT [DF_Document_CreatedAt] DEFAULT");
    }

    [Test]
    public void It_should_have_named_default_for_effective_schema_applied_at()
    {
        _ddl.Should().Contain("CONSTRAINT [DF_EffectiveSchema_AppliedAt] DEFAULT");
    }

    [Test]
    public void It_should_have_named_default_for_document_change_event_created_at()
    {
        _ddl.Should().Contain("CONSTRAINT [DF_DocumentChangeEvent_CreatedAt] DEFAULT");
    }

    [Test]
    public void It_should_have_named_default_for_document_cache_computed_at()
    {
        _ddl.Should().Contain("CONSTRAINT [DF_DocumentCache_ComputedAt] DEFAULT");
    }

    // ── MSSQL CLUSTERED primary keys ────────────────────────────────

    [Test]
    public void It_should_have_clustered_pk_for_descriptor()
    {
        _ddl.Should().Contain("CONSTRAINT [PK_Descriptor] PRIMARY KEY CLUSTERED ([DocumentId])");
    }

    [Test]
    public void It_should_have_clustered_pk_for_document()
    {
        _ddl.Should().Contain("CONSTRAINT [PK_Document] PRIMARY KEY CLUSTERED ([DocumentId])");
    }

    [Test]
    public void It_should_have_clustered_pk_for_resource_key()
    {
        _ddl.Should().Contain("CONSTRAINT [PK_ResourceKey] PRIMARY KEY CLUSTERED ([ResourceKeyId])");
    }

    // ── MSSQL ReferentialIdentity: NONCLUSTERED PK + UNIQUE CLUSTERED ──

    [Test]
    public void It_should_have_nonclustered_pk_for_referential_identity()
    {
        _ddl.Should()
            .Contain("CONSTRAINT [PK_ReferentialIdentity] PRIMARY KEY NONCLUSTERED ([ReferentialId])");
    }

    [Test]
    public void It_should_have_unique_clustered_for_referential_identity()
    {
        _ddl.Should()
            .Contain(
                "CONSTRAINT [UX_ReferentialIdentity_DocumentId_ResourceKeyId] UNIQUE CLUSTERED ([DocumentId], [ResourceKeyId])"
            );
    }

    // ── CHECK constraints ───────────────────────────────────────────

    [Test]
    public void It_should_have_singleton_check_on_effective_schema()
    {
        _ddl.Should().Contain("[CK_EffectiveSchema_Singleton]");
        _ddl.Should().Contain("[EffectiveSchemaSingletonId] = 1");
    }

    [Test]
    public void It_should_not_have_pg_only_bytea_length_check()
    {
        _ddl.Should().NotContain("CK_EffectiveSchema_ResourceKeySeedHash_Length");
        _ddl.Should().NotContain("octet_length");
    }

    [Test]
    public void It_should_have_mssql_json_check_on_document_cache()
    {
        _ddl.Should().Contain("[CK_DocumentCache_IsJsonObject]");
        _ddl.Should().Contain("ISJSON([DocumentJson]) = 1");
    }

    [Test]
    public void It_should_not_have_pg_jsonb_check()
    {
        _ddl.Should().NotContain("CK_DocumentCache_JsonObject");
        _ddl.Should().NotContain("jsonb_typeof");
    }

    // ── Nullable column ─────────────────────────────────────────────

    [Test]
    public void It_should_have_nullable_description_in_descriptor()
    {
        _ddl.Should().Contain("[Description] nvarchar(1024) NULL");
    }

    // ── Foreign keys ────────────────────────────────────────────────

    [Test]
    public void It_should_have_all_eight_foreign_keys()
    {
        _ddl.Should().Contain("[FK_Descriptor_Document]");
        _ddl.Should().Contain("[FK_Document_ResourceKey]");
        _ddl.Should().Contain("[FK_DocumentCache_Document]");
        _ddl.Should().Contain("[FK_DocumentChangeEvent_Document]");
        _ddl.Should().Contain("[FK_DocumentChangeEvent_ResourceKey]");
        _ddl.Should().Contain("[FK_ReferentialIdentity_Document]");
        _ddl.Should().Contain("[FK_ReferentialIdentity_ResourceKey]");
        _ddl.Should().Contain("[FK_SchemaComponent_EffectiveSchemaHash]");
    }

    [Test]
    public void It_should_have_cascade_deletes_where_specified()
    {
        _ddl.Should().Contain("ON DELETE CASCADE");
    }

    [Test]
    public void It_should_have_no_action_where_specified()
    {
        _ddl.Should().Contain("ON DELETE NO ACTION");
    }

    // ── Indexes ─────────────────────────────────────────────────────

    [Test]
    public void It_should_have_all_six_indexes()
    {
        _ddl.Should().Contain("[IX_Descriptor_Uri_Discriminator]");
        _ddl.Should().Contain("[IX_Document_ResourceKeyId_DocumentId]");
        _ddl.Should().Contain("[IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt]");
        _ddl.Should().Contain("[IX_DocumentChangeEvent_DocumentId]");
        _ddl.Should().Contain("[IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion]");
        _ddl.Should().Contain("[IX_ReferentialIdentity_DocumentId]");
    }

    // ── MSSQL trigger ───────────────────────────────────────────────

    [Test]
    public void It_should_create_or_alter_trigger()
    {
        _ddl.Should().Contain("CREATE OR ALTER TRIGGER [dms].[TR_Document_Journal]");
    }

    [Test]
    public void It_should_attach_trigger_to_document_table()
    {
        _ddl.Should().Contain("ON [dms].[Document]");
        _ddl.Should().Contain("AFTER INSERT, UPDATE");
    }

    [Test]
    public void It_should_use_set_nocount_on()
    {
        _ddl.Should().Contain("SET NOCOUNT ON");
    }

    [Test]
    public void It_should_check_content_version_update()
    {
        _ddl.Should().Contain("IF UPDATE([ContentVersion])");
    }

    [Test]
    public void It_should_insert_from_inserted_pseudo_table()
    {
        _ddl.Should().Contain("FROM inserted i");
    }

    [Test]
    public void It_should_use_sysutcdatetime_in_trigger()
    {
        _ddl.Should().Contain("sysutcdatetime()");
    }

    [Test]
    public void It_should_not_contain_pg_trigger_syntax()
    {
        _ddl.Should().NotContain("LANGUAGE plpgsql");
        _ddl.Should().NotContain("DISTINCT ON");
        _ddl.Should().NotContain("new_table");
    }

    // ── No authorization objects ─────────────────────────────────────

    [Test]
    public void It_should_not_contain_auth_schema_objects()
    {
        _ddl.Should().NotContain("[auth]");
        _ddl.Should().NotContain("DocumentSubject");
    }

    // ── Canonicalization ─────────────────────────────────────────────

    [Test]
    public void It_should_use_unix_line_endings()
    {
        _ddl.Should().NotContain("\r");
    }

    [Test]
    public void It_should_not_have_trailing_whitespace_on_any_line()
    {
        var lines = _ddl.Split('\n');
        foreach (var line in lines)
        {
            line.Should().Be(line.TrimEnd(), $"Line has trailing whitespace: [{line}]");
        }
    }
}
