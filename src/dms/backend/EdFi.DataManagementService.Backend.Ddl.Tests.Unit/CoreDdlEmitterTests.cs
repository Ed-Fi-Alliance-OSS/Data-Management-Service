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
        var phase2 = _ddl.IndexOf("Phase 2: Extensions", StringComparison.Ordinal);
        var phase3 = _ddl.IndexOf("Phase 3: Sequences", StringComparison.Ordinal);
        var phase4 = _ddl.IndexOf("Phase 4: Functions", StringComparison.Ordinal);
        var phase5 = _ddl.IndexOf("Phase 5: Tables", StringComparison.Ordinal);
        var phase6 = _ddl.IndexOf("Phase 6: Foreign Keys", StringComparison.Ordinal);
        var phase7 = _ddl.IndexOf("Phase 7: Indexes", StringComparison.Ordinal);
        var phase8 = _ddl.IndexOf("Phase 8: Triggers", StringComparison.Ordinal);

        phase1.Should().BeGreaterOrEqualTo(0);
        phase2.Should().BeGreaterThan(phase1);
        phase3.Should().BeGreaterThan(phase2);
        phase4.Should().BeGreaterThan(phase3);
        phase5.Should().BeGreaterThan(phase4);
        phase6.Should().BeGreaterThan(phase5);
        phase7.Should().BeGreaterThan(phase6);
        phase8.Should().BeGreaterThan(phase7);
    }

    // ── Schema and sequence ─────────────────────────────────────────

    [Test]
    public void It_should_create_dms_schema()
    {
        _ddl.Should().Contain("CREATE SCHEMA IF NOT EXISTS \"dms\"");
    }

    [Test]
    public void It_should_create_pgcrypto_extension()
    {
        _ddl.Should().Contain("CREATE EXTENSION IF NOT EXISTS \"pgcrypto\"");
    }

    [Test]
    public void It_should_create_change_version_sequence()
    {
        _ddl.Should().Contain("CREATE SEQUENCE IF NOT EXISTS \"dms\".\"ChangeVersionSequence\"");
    }

    [Test]
    public void It_should_create_collection_item_id_sequence()
    {
        _ddl.Should().Contain("CREATE SEQUENCE IF NOT EXISTS \"dms\".\"CollectionItemIdSequence\"");
    }

    [Test]
    public void It_should_emit_collection_item_id_sequence_before_tables()
    {
        var sequence = _ddl.IndexOf(
            "CREATE SEQUENCE IF NOT EXISTS \"dms\".\"CollectionItemIdSequence\"",
            StringComparison.Ordinal
        );
        var firstTable = _ddl.IndexOf(
            "CREATE TABLE IF NOT EXISTS \"dms\".\"Descriptor\"",
            StringComparison.Ordinal
        );

        sequence.Should().BeGreaterThan(0);
        sequence.Should().BeLessThan(firstTable);
    }

    // ── Functions ───────────────────────────────────────────────────

    [Test]
    public void It_should_create_uuidv5_function()
    {
        _ddl.Should()
            .Contain("CREATE OR REPLACE FUNCTION \"dms\".\"uuidv5\"(namespace_uuid uuid, name_text text)");
    }

    [Test]
    public void It_should_emit_uuidv5_before_triggers()
    {
        var funcPos = _ddl.IndexOf("\"dms\".\"uuidv5\"", StringComparison.Ordinal);
        var triggerPos = _ddl.IndexOf("Phase 8: Triggers", StringComparison.Ordinal);
        funcPos.Should().BeGreaterThan(0);
        funcPos.Should().BeLessThan(triggerPos);
    }

    [Test]
    public void It_should_create_throw_error_function()
    {
        _ddl.Should().Contain("CREATE OR REPLACE FUNCTION \"dms\".\"throw_error\"");
    }

    [Test]
    public void It_should_create_get_max_change_version_function()
    {
        _ddl.Should().Contain("CREATE OR REPLACE FUNCTION \"dms\".\"GetMaxChangeVersion\"() RETURNS bigint");
    }

    [Test]
    public void It_should_emit_get_max_change_version_after_change_version_sequence()
    {
        var sequence = _ddl.IndexOf(
            "CREATE SEQUENCE IF NOT EXISTS \"dms\".\"ChangeVersionSequence\"",
            StringComparison.Ordinal
        );
        var function = _ddl.IndexOf("\"dms\".\"GetMaxChangeVersion\"()", StringComparison.Ordinal);

        sequence.Should().BeGreaterThan(0);
        function.Should().BeGreaterThan(sequence);
    }

    [Test]
    public void It_should_emit_get_max_change_version_before_phase_5_tables()
    {
        var function = _ddl.IndexOf("\"dms\".\"GetMaxChangeVersion\"()", StringComparison.Ordinal);
        var phase5 = _ddl.IndexOf("Phase 5: Tables", StringComparison.Ordinal);

        function.Should().BeGreaterThan(0);
        function.Should().BeLessThan(phase5);
    }

    [Test]
    public void It_should_emit_get_max_change_version_first_among_functions()
    {
        var getMax = _ddl.IndexOf("\"dms\".\"GetMaxChangeVersion\"()", StringComparison.Ordinal);
        var throwError = _ddl.IndexOf("\"dms\".\"throw_error\"", StringComparison.Ordinal);
        var uuidv5 = _ddl.IndexOf("\"dms\".\"uuidv5\"", StringComparison.Ordinal);

        getMax.Should().BeLessThan(throwError);
        throwError.Should().BeLessThan(uuidv5);
    }

    [Test]
    public void It_should_bind_get_max_change_version_to_document_content_version_sequence()
    {
        // Document.ContentVersion defaults to nextval('"dms"."ChangeVersionSequence"').
        // GetMaxChangeVersion must read from the same sequence so change-query
        // semantics stay coherent.
        _ddl.Should().Contain("DEFAULT nextval('\"dms\".\"ChangeVersionSequence\"')");
        _ddl.Should().Contain("SELECT last_value FROM \"dms\".\"ChangeVersionSequence\" INTO result;");
    }

    [Test]
    public void It_should_not_emit_user_defined_table_types()
    {
        _ddl.Should().NotContain("CREATE TYPE");
    }

    // ── Tables ──────────────────────────────────────────────────────

    [Test]
    public void It_should_create_descriptor_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"dms\".\"Descriptor\"");
    }

    [Test]
    public void It_should_default_descriptor_content_version_to_zero()
    {
        _ddl.Should().Contain("\"ContentVersion\" bigint NOT NULL DEFAULT 0");
    }

    [Test]
    public void It_should_default_every_descriptor_document_metadata_mirror_column()
    {
        // TR_Descriptor_Stamp_Document is AFTER INSERT and mirrors through a separate UPDATE, so the
        // client INSERT — which supplies none of these columns — must satisfy NOT NULL on its own.
        // Without the defaults, every descriptor create would fail before the trigger could run.
        var block = DescriptorTableColumnExtractor.ExtractPgBlock(_ddl);

        block.Should().Contain("\"DocumentUuid\" uuid NOT NULL DEFAULT gen_random_uuid()");
        block.Should().Contain("\"IdentityVersion\" bigint NOT NULL DEFAULT 0");
        block.Should().Contain("\"IdentityLastModifiedAt\" timestamp with time zone NOT NULL DEFAULT now()");
        block.Should().Contain("\"CreatedAt\" timestamp with time zone NOT NULL DEFAULT now()");
    }

    [Test]
    public void It_should_emit_descriptor_created_by_ownership_token_id_as_a_nullable_placeholder()
    {
        // dms.Document has no CreatedByOwnershipTokenId column, so nothing copies a value onto the
        // descriptor; the column exists only so the shape matches the root tables.
        var block = DescriptorTableColumnExtractor.ExtractPgBlock(_ddl);

        block.Should().Contain("\"CreatedByOwnershipTokenId\" smallint NULL");
    }

    [Test]
    public void It_should_emit_a_persisted_lower_cased_descriptor_uri_column()
    {
        // The descriptor probe seeks lower-cased URIs. lower("Uri") in a predicate is not sargable on
        // PostgreSQL, so the lower-casing is materialized by the engine into its own stored column.
        var block = DescriptorTableColumnExtractor.ExtractPgBlock(_ddl);

        block.Should().Contain("\"UriLowered\" varchar(306) GENERATED ALWAYS AS (lower(\"Uri\")) STORED,");
        // The original-case column is the stored representation and stays exactly as it was.
        block.Should().Contain("\"Uri\" varchar(306) NOT NULL,");
    }

    [Test]
    public void It_should_carry_resource_key_id_on_the_descriptor_row()
    {
        // Descriptor reads root on dms.Descriptor and filter by the project-qualified descriptor type, so
        // the column lives here. Nullable with no default: a fabricated default would be silently-wrong
        // data on a column the read path filters by, and the descriptor write path binds it on every
        // supported insert, so a NULL can only come from an out-of-band write.
        var block = DescriptorTableColumnExtractor.ExtractPgBlock(_ddl);

        // The trailing comma is load-bearing — it proves nothing (a DEFAULT) follows the null marker.
        block.Should().Contain("\"ResourceKeyId\" smallint NULL,");
        _ddl.Should().Contain("\"IX_Descriptor_ResourceKeyId_DocumentId\"");
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
    public void It_should_emit_content_version_column_on_document_cache_table()
    {
        // Schema-only column added for the cached-vs-canonical freshness check
        // (cached.ContentVersion == dms.Document.ContentVersion AND
        //  cached.LastModifiedAt == dms.Document.ContentLastModifiedAt).
        // Runtime cache reader/writer lands in a follow-on ticket.
        _ddl.Should().Contain("\"ContentVersion\" bigint NOT NULL");
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
        var effectiveSchema = _ddl.IndexOf("\"dms\".\"EffectiveSchema\"", StringComparison.Ordinal);
        var referentialIdentity = _ddl.IndexOf("\"dms\".\"ReferentialIdentity\"", StringComparison.Ordinal);
        var resourceKey = _ddl.IndexOf("\"dms\".\"ResourceKey\"", StringComparison.Ordinal);
        var schemaComponent = _ddl.IndexOf("\"dms\".\"SchemaComponent\"", StringComparison.Ordinal);

        descriptor.Should().BeLessThan(document);
        document.Should().BeLessThan(documentCache);
        documentCache.Should().BeLessThan(effectiveSchema);
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
    public void It_should_have_unique_on_descriptor_uri_lowered_discriminator()
    {
        // The probe's seek target. Case-insensitive uniqueness is not new behavior: the UUIDv5
        // ReferentialId it replaces was hashed over the lower-cased URI, so case-variant URIs
        // already collapsed onto one document.
        _ddl.Should()
            .Contain(
                "ADD CONSTRAINT \"UX_Descriptor_UriLowered_Discriminator\" UNIQUE (\"UriLowered\", \"Discriminator\");"
            );
    }

    [Test]
    public void It_should_have_unique_on_descriptor_document_uuid()
    {
        _ddl.Should().Contain("\"UX_Descriptor_DocumentUuid\" UNIQUE (\"DocumentUuid\")");
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
    public void It_should_have_non_blank_api_schema_format_version_check_on_effective_schema()
    {
        _ddl.Should().Contain("\"CK_EffectiveSchema_ApiSchemaFormatVersion_NotBlank\"");
        _ddl.Should().Contain("btrim(\"ApiSchemaFormatVersion\") <> ''");
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

    [Test]
    public void It_should_have_nullable_effective_begin_date_in_descriptor()
    {
        _ddl.Should().Contain("\"EffectiveBeginDate\" date NULL");
    }

    [Test]
    public void It_should_have_nullable_effective_end_date_in_descriptor()
    {
        _ddl.Should().Contain("\"EffectiveEndDate\" date NULL");
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
    public void It_should_have_index_referential_identity_document_id()
    {
        _ddl.Should().Contain("\"IX_ReferentialIdentity_DocumentId\"");
    }

    // ── PG descriptor stamping trigger ──────────────────────────────

    [Test]
    public void It_should_create_descriptor_stamping_function()
    {
        _ddl.Should().Contain("CREATE OR REPLACE FUNCTION \"dms\".\"TF_Descriptor_Stamp_Document\"()");
    }

    [Test]
    public void It_should_drop_existing_descriptor_stamping_trigger_before_creating()
    {
        _ddl.Should()
            .Contain("DROP TRIGGER IF EXISTS \"TR_Descriptor_Stamp_Document\" ON \"dms\".\"Descriptor\"");
    }

    [Test]
    public void It_should_create_descriptor_stamping_trigger()
    {
        _ddl.Should().Contain("CREATE TRIGGER \"TR_Descriptor_Stamp_Document\"");
        _ddl.Should().Contain("AFTER INSERT OR UPDATE OR DELETE ON \"dms\".\"Descriptor\"");
        _ddl.Should().Contain("EXECUTE FUNCTION \"dms\".\"TF_Descriptor_Stamp_Document\"()");
    }

    [Test]
    public void It_should_emit_no_op_guard_in_descriptor_stamping_function()
    {
        _ddl.Should().Contain("IF TG_OP = 'UPDATE' THEN");
        _ddl.Should().Contain("IF NOT (");
        _ddl.Should().Contain("OLD.\"Namespace\" IS DISTINCT FROM NEW.\"Namespace\"");
        _ddl.Should().Contain("OLD.\"CodeValue\" IS DISTINCT FROM NEW.\"CodeValue\"");
        _ddl.Should().Contain("OLD.\"ShortDescription\" IS DISTINCT FROM NEW.\"ShortDescription\"");
        _ddl.Should().Contain("OLD.\"Description\" IS DISTINCT FROM NEW.\"Description\"");
        _ddl.Should().Contain("OLD.\"EffectiveBeginDate\" IS DISTINCT FROM NEW.\"EffectiveBeginDate\"");
        _ddl.Should().Contain("OLD.\"EffectiveEndDate\" IS DISTINCT FROM NEW.\"EffectiveEndDate\"");
        _ddl.Should().Contain("OLD.\"Discriminator\" IS DISTINCT FROM NEW.\"Discriminator\"");
        _ddl.Should().Contain("OLD.\"Uri\" IS DISTINCT FROM NEW.\"Uri\"");
    }

    [Test]
    public void It_should_stamp_the_descriptor_row_itself_for_descriptor_inserts()
    {
        // A new descriptor row has no prior stamp to preserve, so INSERT takes the identity stamp
        // alongside the content stamp — and writes dms.Descriptor's own columns, never dms.Document.
        var insertBranch = SliceBranch(_ddl, "IF TG_OP = 'INSERT' THEN", "ELSIF TG_OP = 'UPDATE' THEN");

        insertBranch.Should().Contain("UPDATE \"dms\".\"Descriptor\" r");
        insertBranch
            .Should()
            .Contain(
                "SET \"ContentVersion\" = nextval('\"dms\".\"ChangeVersionSequence\"'), "
                    + "\"ContentLastModifiedAt\" = now(), "
                    + "\"IdentityVersion\" = nextval('\"dms\".\"ChangeVersionSequence\"'), "
                    + "\"IdentityLastModifiedAt\" = now()"
            );
        insertBranch.Should().Contain("WHERE r.\"DocumentId\" = NEW.\"DocumentId\";");
        insertBranch.Should().NotContain("\"dms\".\"Document\"");
        insertBranch.Should().NotContain("WITH stamped AS (");
    }

    [Test]
    public void It_should_stamp_the_descriptor_row_itself_for_descriptor_updates()
    {
        // A changed UPDATE takes only the content stamp: the descriptor identity is immutable through
        // every supported write, so re-bumping IdentityVersion would be a false key change.
        var updateBranch = SliceBranch(_ddl, "ELSIF TG_OP = 'UPDATE' THEN", "ELSIF TG_OP = 'DELETE' THEN");

        updateBranch.Should().Contain("UPDATE \"dms\".\"Descriptor\" r");
        updateBranch
            .Should()
            .Contain(
                "SET \"ContentVersion\" = nextval('\"dms\".\"ChangeVersionSequence\"'), "
                    + "\"ContentLastModifiedAt\" = now()\n"
            );
        updateBranch.Should().Contain("WHERE r.\"DocumentId\" = NEW.\"DocumentId\";");
        updateBranch.Should().NotContain("\"IdentityVersion\"");
        updateBranch.Should().NotContain("\"dms\".\"Document\"");
        updateBranch.Should().NotContain("WITH stamped AS (");
    }

    [Test]
    public void It_should_leave_the_no_tombstone_descriptor_delete_branch_without_a_stamp()
    {
        // No-tombstone variant: the descriptor row is the stamp store and it is the row going away, so
        // the DELETE arm has nothing left to write. Its only surviving statement is RETURN OLD.
        var deleteStart = _ddl.IndexOf("ELSIF TG_OP = 'DELETE' THEN", StringComparison.Ordinal);
        deleteStart.Should().BeGreaterOrEqualTo(0);
        var deleteEnd = _ddl.IndexOf("END IF;", deleteStart, StringComparison.Ordinal);
        deleteEnd.Should().BeGreaterThan(deleteStart);
        var deleteBranch = _ddl[deleteStart..deleteEnd];

        deleteBranch.Should().NotContain("UPDATE ");
        deleteBranch.Should().NotContain("nextval");
        deleteBranch.Should().NotContain("WITH stamped AS (");
        deleteBranch.Should().NotContain("RETURNING");
        deleteBranch.Should().Contain("RETURN OLD;");
    }

    [Test]
    public void It_should_not_read_dms_document_from_the_descriptor_stamping_function()
    {
        // The descriptor row owns every stamp it reports, so no branch of the trigger touches
        // dms.Document any more.
        var function = SliceBranch(
            _ddl,
            "CREATE OR REPLACE FUNCTION \"dms\".\"TF_Descriptor_Stamp_Document\"()",
            "$func$ LANGUAGE plpgsql;"
        );

        function.Should().NotContain("\"dms\".\"Document\"");
        function.Should().NotContain("stamped.");
    }

    private static string SliceBranch(string ddl, string startMarker, string endMarker)
    {
        var start = ddl.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterOrEqualTo(0, $"'{startMarker}' must be emitted");
        var end = ddl.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"'{endMarker}' must follow '{startMarker}'");
        return ddl[start..end];
    }

    [Test]
    public void It_should_not_copy_created_by_ownership_token_id_onto_the_descriptor()
    {
        // dms.Document has no such column, so there is nothing to copy. The descriptor column is a
        // nullable placeholder and must not appear anywhere in the stamping trigger.
        var functionStart = _ddl.IndexOf(
            "CREATE OR REPLACE FUNCTION \"dms\".\"TF_Descriptor_Stamp_Document\"()",
            StringComparison.Ordinal
        );
        functionStart.Should().BeGreaterOrEqualTo(0);
        var functionEnd = _ddl.IndexOf("$func$ LANGUAGE plpgsql;", functionStart, StringComparison.Ordinal);
        functionEnd.Should().BeGreaterThan(functionStart);

        _ddl[functionStart..functionEnd].Should().NotContain("CreatedByOwnershipTokenId");
    }

    /// <summary>
    /// The mirror <c>SET</c> list both branches of the PostgreSQL descriptor stamping trigger share.
    /// </summary>
    [Test]
    public void It_should_diff_every_non_key_descriptor_column_in_stamping_trigger()
    {
        // Drift guard: the trigger's no-op predicate is built from a hand-maintained
        // column list (_descriptorStoredColumns in CoreDdlEmitter). If a future change
        // to EmitDescriptorTable adds or renames a column without updating that list,
        // real value changes to the new column will silently fail to bump stamps.
        // This test re-derives the column set from the emitted Descriptor table and
        // asserts each non-PK, non-mirror column appears as an IS DISTINCT FROM predicate.
        var columns = DescriptorTableColumnExtractor.ExtractPgColumns(_ddl);
        columns.Should().NotBeEmpty("Descriptor CREATE TABLE block must be parseable");
        columns.Should().Contain("DocumentId", "sanity check the extractor found PK column");

        foreach (
            var column in columns.Where(c =>
                c != "DocumentId" && !DescriptorTableColumnExtractor.NonDiffedColumns.Contains(c)
            )
        )
        {
            _ddl.Should()
                .Contain(
                    $"OLD.\"{column}\" IS DISTINCT FROM NEW.\"{column}\"",
                    $"descriptor stamping trigger must compare {column}; "
                        + "update _descriptorStoredColumns in CoreDdlEmitter when adding columns"
                );
        }

        foreach (var mirrorColumn in DescriptorTableColumnExtractor.TriggerMaintainedColumns)
        {
            _ddl.Should()
                .NotContain(
                    $"OLD.\"{mirrorColumn}\" IS DISTINCT FROM NEW.\"{mirrorColumn}\"",
                    "dms.Document mirror columns are trigger-maintained and must not appear in the "
                        + "no-op diff — that exclusion is what stops the mirror UPDATE from recursing"
                );
        }

        foreach (var placeholderColumn in DescriptorTableColumnExtractor.PlaceholderColumns)
        {
            _ddl.Should()
                .NotContain(
                    $"OLD.\"{placeholderColumn}\" IS DISTINCT FROM NEW.\"{placeholderColumn}\"",
                    "this column is a permanently-NULL placeholder no writer ever sets, so diffing it "
                        + "could never detect a change and must not be emitted"
                );
        }

        foreach (var computedColumn in DescriptorTableColumnExtractor.EngineComputedColumns)
        {
            _ddl.Should()
                .NotContain(
                    $"OLD.\"{computedColumn}\" IS DISTINCT FROM NEW.\"{computedColumn}\"",
                    "this column is derived from another column in the same row, so any change to it is "
                        + "already detected through its source column"
                );
        }
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
        var phase3 = _ddl.IndexOf("Phase 3: Sequences", StringComparison.Ordinal);
        var phase4 = _ddl.IndexOf("Phase 4: Functions", StringComparison.Ordinal);
        var phase5 = _ddl.IndexOf("Phase 5: Tables", StringComparison.Ordinal);
        var phase6 = _ddl.IndexOf("Phase 6: Foreign Keys", StringComparison.Ordinal);
        var phase7 = _ddl.IndexOf("Phase 7: Indexes", StringComparison.Ordinal);
        var phase8 = _ddl.IndexOf("Phase 8: Triggers", StringComparison.Ordinal);

        phase1.Should().BeGreaterOrEqualTo(0);
        phase3.Should().BeGreaterThan(phase1);
        phase4.Should().BeGreaterThan(phase3);
        phase5.Should().BeGreaterThan(phase4);
        phase6.Should().BeGreaterThan(phase5);
        phase7.Should().BeGreaterThan(phase6);
        phase8.Should().BeGreaterThan(phase7);
    }

    [Test]
    public void It_should_not_emit_extensions_phase()
    {
        _ddl.Should().NotContain("Phase 2: Extensions");
        _ddl.Should().NotContain("CREATE EXTENSION");
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

    [Test]
    public void It_should_create_collection_item_id_sequence_with_catalog_check()
    {
        _ddl.Should().Contain("sys.sequences");
        _ddl.Should().Contain("[dms].[CollectionItemIdSequence]");
    }

    [Test]
    public void It_should_emit_collection_item_id_sequence_before_tables()
    {
        var sequence = _ddl.IndexOf(
            "CREATE SEQUENCE [dms].[CollectionItemIdSequence]",
            StringComparison.Ordinal
        );
        var firstTable = _ddl.IndexOf("CREATE TABLE [dms].[Descriptor]", StringComparison.Ordinal);

        sequence.Should().BeGreaterThan(0);
        sequence.Should().BeLessThan(firstTable);
    }

    // ── Functions ───────────────────────────────────────────────────

    [Test]
    public void It_should_create_uuidv5_function()
    {
        _ddl.Should().Contain("CREATE OR ALTER FUNCTION [dms].[uuidv5]");
    }

    [Test]
    public void It_should_emit_go_batch_separator_after_uuidv5_function_before_tables()
    {
        var functionIndex = _ddl.IndexOf("CREATE OR ALTER FUNCTION", StringComparison.Ordinal);
        var phase5Index = _ddl.IndexOf("Phase 5: Tables", StringComparison.Ordinal);
        var goIndex = _ddl.LastIndexOf("GO\n", phase5Index);

        functionIndex.Should().BeGreaterThan(0);
        phase5Index.Should().BeGreaterThan(functionIndex);
        goIndex.Should().BeGreaterThan(functionIndex, "expected GO batch separator after function batch");
        goIndex.Should().BeLessThan(phase5Index);
    }

    [Test]
    public void It_should_emit_uuidv5_before_triggers()
    {
        var funcPos = _ddl.IndexOf("[dms].[uuidv5]", StringComparison.Ordinal);
        var triggerPos = _ddl.IndexOf("Phase 8: Triggers", StringComparison.Ordinal);
        funcPos.Should().BeGreaterThan(0);
        funcPos.Should().BeLessThan(triggerPos);
    }

    [Test]
    public void It_should_create_big_int_table_type()
    {
        _ddl.Should().Contain("CREATE TYPE [dms].[BigIntTable]");
    }

    [Test]
    public void It_should_create_unique_identifier_table_type()
    {
        _ddl.Should().Contain("CREATE TYPE [dms].[UniqueIdentifierTable]");
    }

    [Test]
    public void It_should_not_emit_throw_error_function()
    {
        _ddl.Should().NotContain("throw_error");
    }

    // ── Tables ──────────────────────────────────────────────────────

    [Test]
    public void It_should_emit_content_version_column_on_document_cache_table()
    {
        // Schema-only column added for the cached-vs-canonical freshness check.
        // Runtime cache reader/writer lands in a follow-on ticket.
        _ddl.Should().Contain("[ContentVersion] bigint NOT NULL");
    }

    [Test]
    public void It_should_create_all_seven_tables()
    {
        _ddl.Should().Contain("[dms].[Descriptor]");
        _ddl.Should().Contain("[dms].[Document]");
        _ddl.Should().Contain("[dms].[DocumentCache]");
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

    [Test]
    public void It_should_keep_document_defaults_compatible_with_later_same_transaction_restamping()
    {
        _ddl.Should()
            .Contain(
                "CONSTRAINT [DF_Document_ContentVersion] DEFAULT (NEXT VALUE FOR [dms].[ChangeVersionSequence])"
            );
        _ddl.Should()
            .Contain(
                "CONSTRAINT [DF_Document_IdentityVersion] DEFAULT (NEXT VALUE FOR [dms].[ChangeVersionSequence])"
            );
        _ddl.Should().Contain("CONSTRAINT [DF_Document_ContentLastModifiedAt] DEFAULT (sysutcdatetime())");
        _ddl.Should().Contain("CONSTRAINT [DF_Document_IdentityLastModifiedAt] DEFAULT (sysutcdatetime())");
    }

    [Test]
    public void It_should_default_descriptor_content_version_to_zero()
    {
        _ddl.Should().Contain("CONSTRAINT [DF_Descriptor_ContentVersion] DEFAULT 0");
    }

    [Test]
    public void It_should_default_every_descriptor_document_metadata_mirror_column()
    {
        // TR_Descriptor_Stamp_Document is AFTER INSERT and mirrors through a separate UPDATE, so the
        // client INSERT — which supplies none of these columns — must satisfy NOT NULL on its own.
        _ddl.Should()
            .Contain(
                "[DocumentUuid] uniqueidentifier NOT NULL CONSTRAINT [DF_Descriptor_DocumentUuid] DEFAULT newid()"
            );
        _ddl.Should().Contain("CONSTRAINT [DF_Descriptor_IdentityVersion] DEFAULT 0");
        _ddl.Should().Contain("CONSTRAINT [DF_Descriptor_IdentityLastModifiedAt] DEFAULT (sysutcdatetime())");
        _ddl.Should().Contain("CONSTRAINT [DF_Descriptor_CreatedAt] DEFAULT (sysutcdatetime())");
    }

    [Test]
    public void It_should_emit_descriptor_created_by_ownership_token_id_as_a_nullable_placeholder()
    {
        // dms.Document has no CreatedByOwnershipTokenId column, so nothing copies a value onto the
        // descriptor; the column exists only so the shape matches the root tables.
        DescriptorTableColumnExtractor
            .ExtractMssqlBlock(_ddl)
            .Should()
            .Contain("[CreatedByOwnershipTokenId] smallint NULL");
    }

    [Test]
    public void It_should_emit_a_persisted_lower_cased_descriptor_uri_column()
    {
        // Kept identical in shape to the PostgreSQL sibling so both dialects present the probe the
        // same column, even though SQL Server's default CI collation would already match case-blind.
        var block = DescriptorTableColumnExtractor.ExtractMssqlBlock(_ddl);

        block.Should().Contain("[UriLowered] AS (LOWER([Uri])) PERSISTED,");
        // The original-case column is the stored representation and stays exactly as it was.
        block.Should().Contain("[Uri] nvarchar(306) NOT NULL,");
    }

    [Test]
    public void It_should_carry_resource_key_id_on_the_descriptor_row()
    {
        // Descriptor reads root on dms.Descriptor and filter by the project-qualified descriptor type, so
        // the column lives here. Nullable with no default: a fabricated default would be silently-wrong
        // data on a column the read path filters by, and the descriptor write path binds it on every
        // supported insert, so a NULL can only come from an out-of-band write.
        DescriptorTableColumnExtractor
            .ExtractMssqlBlock(_ddl)
            .Should()
            // The trailing comma is load-bearing — it proves no DEFAULT constraint follows.
            .Contain("[ResourceKeyId] smallint NULL,");
        _ddl.Should().NotContain("DF_Descriptor_ResourceKeyId");
        _ddl.Should().Contain("[IX_Descriptor_ResourceKeyId_DocumentId]");
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
    public void It_should_have_unique_on_descriptor_document_uuid()
    {
        _ddl.Should().Contain("ADD CONSTRAINT [UX_Descriptor_DocumentUuid] UNIQUE ([DocumentUuid]);");
    }

    [Test]
    public void It_should_have_unique_on_descriptor_uri_lowered_discriminator()
    {
        // The probe's seek target. Case-insensitive uniqueness is not new behavior: the UUIDv5
        // ReferentialId it replaces was hashed over the lower-cased URI, so case-variant URIs
        // already collapsed onto one document.
        _ddl.Should()
            .Contain(
                "ADD CONSTRAINT [UX_Descriptor_UriLowered_Discriminator] UNIQUE ([UriLowered], [Discriminator]);"
            );
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
    public void It_should_have_non_blank_api_schema_format_version_check_on_effective_schema()
    {
        _ddl.Should().Contain("[CK_EffectiveSchema_ApiSchemaFormatVersion_NotBlank]");
        _ddl.Should().Contain("LEN(LTRIM(RTRIM([ApiSchemaFormatVersion]))) > 0");
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

    [Test]
    public void It_should_have_nullable_effective_begin_date_in_descriptor()
    {
        _ddl.Should().Contain("[EffectiveBeginDate] date NULL");
    }

    [Test]
    public void It_should_have_nullable_effective_end_date_in_descriptor()
    {
        _ddl.Should().Contain("[EffectiveEndDate] date NULL");
    }

    // ── Foreign keys ────────────────────────────────────────────────

    [Test]
    public void It_should_have_all_six_foreign_keys()
    {
        _ddl.Should().Contain("[FK_Descriptor_Document]");
        _ddl.Should().Contain("[FK_Document_ResourceKey]");
        _ddl.Should().Contain("[FK_DocumentCache_Document]");
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
    public void It_should_have_all_five_indexes()
    {
        _ddl.Should().Contain("[IX_Descriptor_ResourceKeyId_DocumentId]");
        _ddl.Should().Contain("[IX_Descriptor_Uri_Discriminator]");
        _ddl.Should().Contain("[IX_Document_ResourceKeyId_DocumentId]");
        _ddl.Should().Contain("[IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt]");
        _ddl.Should().Contain("[IX_ReferentialIdentity_DocumentId]");
    }

    // ── MSSQL descriptor stamping trigger ───────────────────────────

    [Test]
    public void It_should_create_or_alter_descriptor_stamping_trigger()
    {
        _ddl.Should().Contain("CREATE OR ALTER TRIGGER [dms].[TR_Descriptor_Stamp_Document]");
        _ddl.Should().Contain("ON [dms].[Descriptor]");
        _ddl.Should().Contain("AFTER INSERT, UPDATE, DELETE");
    }

    [Test]
    public void It_should_emit_go_batch_separator_around_descriptor_stamping_trigger()
    {
        var triggerIndex = _ddl.IndexOf(
            "CREATE OR ALTER TRIGGER [dms].[TR_Descriptor_Stamp_Document]",
            StringComparison.Ordinal
        );
        triggerIndex.Should().BeGreaterThan(0);

        var precedingGo = _ddl.LastIndexOf("GO\n", triggerIndex, StringComparison.Ordinal);
        precedingGo
            .Should()
            .BeGreaterOrEqualTo(0, "expected GO batch separator before descriptor stamping trigger");

        var trailingGo = _ddl.IndexOf("GO\n", triggerIndex, StringComparison.Ordinal);
        trailingGo
            .Should()
            .BeGreaterThan(triggerIndex, "expected GO batch separator after descriptor stamping trigger");
    }

    [Test]
    public void It_should_emit_affected_docs_cte_in_descriptor_stamping_trigger()
    {
        // The CTE carries changed UPDATE rows only. Pure inserts take their stamp through the separate
        // @insertedDocs workset, and pure deletes stamp nothing at all: the descriptor row is the stamp
        // store and it is the row going away. The null-safe diff predicate keeps no-op updates — including
        // the recursive stamp-only UPDATE — out of the workset.
        _ddl.Should().Contain(";WITH affectedDocs AS (");
        _ddl.Should().Contain("FROM inserted i");
        _ddl.Should().Contain("LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]");
        _ddl.Should().Contain("WHERE del.[DocumentId] IS NOT NULL AND (");
        _ddl.Should()
            .Contain("INSERT INTO @stamped ([DocumentId])\n    SELECT [DocumentId] FROM affectedDocs;");
        _ddl.Should().NotContain("UNION ALL");
        _ddl.Should().NotContain("LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]");
    }

    [Test]
    public void It_should_guard_the_descriptor_stamp_updates_against_empty_worksets()
    {
        // Without these guards the stamp self-UPDATE re-fires the trigger even when its workset is
        // empty, which recurses to the nesting limit on databases with RECURSIVE_TRIGGERS ON
        // (statement triggers fire on 0 affected rows).
        var insertGuard = _ddl.IndexOf("IF EXISTS (SELECT 1 FROM @insertedDocs)", StringComparison.Ordinal);
        insertGuard.Should().BeGreaterOrEqualTo(0);
        _ddl.IndexOf("UPDATE r", insertGuard, StringComparison.Ordinal).Should().BeGreaterThan(insertGuard);

        var updateGuard = _ddl.IndexOf("IF EXISTS (SELECT 1 FROM @stamped)", StringComparison.Ordinal);
        updateGuard.Should().BeGreaterThan(insertGuard);
        _ddl.IndexOf("UPDATE r", updateGuard, StringComparison.Ordinal).Should().BeGreaterThan(updateGuard);
    }

    [Test]
    public void It_should_emit_null_safe_per_column_diffs_for_descriptor_stamping_trigger()
    {
        // String columns must be wrapped in CAST(... AS varbinary(max)) for byte-level comparison.
        _ddl.Should()
            .Contain("(CAST(i.[Namespace] AS varbinary(max)) <> CAST(del.[Namespace] AS varbinary(max))");
        _ddl.Should()
            .Contain("(CAST(i.[CodeValue] AS varbinary(max)) <> CAST(del.[CodeValue] AS varbinary(max))");
        _ddl.Should()
            .Contain(
                "(CAST(i.[ShortDescription] AS varbinary(max)) <> CAST(del.[ShortDescription] AS varbinary(max))"
            );
        _ddl.Should()
            .Contain("(CAST(i.[Description] AS varbinary(max)) <> CAST(del.[Description] AS varbinary(max))");
        _ddl.Should()
            .Contain(
                "(CAST(i.[Discriminator] AS varbinary(max)) <> CAST(del.[Discriminator] AS varbinary(max))"
            );
        _ddl.Should().Contain("(CAST(i.[Uri] AS varbinary(max)) <> CAST(del.[Uri] AS varbinary(max))");
        // Date columns must use plain <> (no CAST).
        _ddl.Should().Contain("(i.[EffectiveBeginDate] <> del.[EffectiveBeginDate]");
        _ddl.Should().Contain("(i.[EffectiveEndDate] <> del.[EffectiveEndDate]");
    }

    [Test]
    public void It_should_stamp_the_descriptor_row_itself_from_the_stamping_trigger()
    {
        // The stamp writes dms.Descriptor's own columns; nothing in the trigger touches dms.Document.
        _ddl.Should().Contain("UPDATE r");
        _ddl.Should().Contain("SET r.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],");
        _ddl.Should().Contain("    r.[ContentLastModifiedAt] = sysutcdatetime()");
        _ddl.Should().Contain("FROM [dms].[Descriptor] r");
        _ddl.Should().Contain("INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];");

        var trigger = SliceMssqlDescriptorTrigger(_ddl);
        trigger.Should().NotContain("[dms].[Document]");
    }

    [Test]
    public void It_should_stamp_both_version_pairs_for_descriptor_inserts()
    {
        // A new descriptor row has no prior stamp to preserve, so the pure-insert workset takes the
        // identity stamp alongside the content stamp. CreatedAt stays on its DF_ default and DocumentUuid
        // is bound by the descriptor write path on the INSERT itself.
        _ddl.Should().Contain("DECLARE @insertedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);");
        _ddl.Should().Contain("INSERT INTO @insertedDocs ([DocumentId])");
        _ddl.Should().Contain("SELECT i.[DocumentId]\n    FROM inserted i");
        _ddl.Should().Contain("LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]");
        _ddl.Should().Contain("WHERE del.[DocumentId] IS NULL;");
        _ddl.Should()
            .Contain(
                "SET r.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],\n"
                    + "            r.[ContentLastModifiedAt] = sysutcdatetime(),\n"
                    + "            r.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],\n"
                    + "            r.[IdentityLastModifiedAt] = sysutcdatetime()"
            );
        _ddl.Should().Contain("INNER JOIN @insertedDocs s ON s.[DocumentId] = r.[DocumentId];");
    }

    [Test]
    public void It_should_declare_the_stamp_worksets_as_document_id_only_tables()
    {
        // Nothing is captured out of the stamp any more — the stamp writes the descriptor row directly —
        // so both worksets carry only the key they join on.
        _ddl.Should().Contain("DECLARE @insertedDocs TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);");
        _ddl.Should().Contain("DECLARE @stamped TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);");

        var trigger = SliceMssqlDescriptorTrigger(_ddl);
        trigger.Should().NotContain("OUTPUT ");
    }

    private static string SliceMssqlDescriptorTrigger(string ddl)
    {
        var start = ddl.IndexOf(
            "CREATE OR ALTER TRIGGER [dms].[TR_Descriptor_Stamp_Document]",
            StringComparison.Ordinal
        );
        start.Should().BeGreaterOrEqualTo(0);
        var end = ddl.IndexOf("\nEND;", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return ddl[start..end];
    }

    [Test]
    public void It_should_not_copy_created_by_ownership_token_id_onto_the_descriptor()
    {
        // dms.Document has no such column, so there is nothing to copy. The descriptor column is a
        // nullable placeholder and must not appear anywhere in the stamping trigger.
        var triggerStart = _ddl.IndexOf(
            "CREATE OR ALTER TRIGGER [dms].[TR_Descriptor_Stamp_Document]",
            StringComparison.Ordinal
        );
        triggerStart.Should().BeGreaterOrEqualTo(0);
        var triggerEnd = _ddl.IndexOf("END;", triggerStart, StringComparison.Ordinal);
        triggerEnd.Should().BeGreaterThan(triggerStart);

        _ddl[triggerStart..triggerEnd].Should().NotContain("CreatedByOwnershipTokenId");
    }

    [Test]
    public void It_should_diff_every_non_key_descriptor_column_in_stamping_trigger()
    {
        // Drift guard: same intent as the PG sibling test. Strings must be wrapped in
        // CAST(... AS varbinary(max)) so case-only / trailing-space-only changes are
        // detected under default CI collation; non-string columns use plain <>. If a
        // future column addition forgets to wire the right comparator, this test fails.
        var columns = DescriptorTableColumnExtractor.ExtractMssqlColumns(_ddl);
        columns.Should().NotBeEmpty("Descriptor CREATE TABLE block must be parseable");
        columns
            .Select(c => c.Name)
            .Should()
            .Contain("DocumentId", "sanity check the extractor found PK column");

        foreach (
            var (name, type) in columns.Where(c =>
                c.Name != "DocumentId" && !DescriptorTableColumnExtractor.NonDiffedColumns.Contains(c.Name)
            )
        )
        {
            var isStringType = type.Contains("char", StringComparison.OrdinalIgnoreCase);
            var expected = isStringType
                ? $"CAST(i.[{name}] AS varbinary(max)) <> CAST(del.[{name}] AS varbinary(max))"
                : $"i.[{name}] <> del.[{name}]";
            _ddl.Should()
                .Contain(
                    expected,
                    $"descriptor stamping trigger must compare {name} ({type}); "
                        + "update _descriptorStoredColumns in CoreDdlEmitter when adding columns"
                );
        }

        foreach (var mirrorColumn in DescriptorTableColumnExtractor.TriggerMaintainedColumns)
        {
            _ddl.Should()
                .NotContain(
                    $"i.[{mirrorColumn}] <> del.[{mirrorColumn}]",
                    "dms.Document mirror columns are trigger-maintained and must not appear in the "
                        + "no-op diff — that exclusion is what stops the mirror UPDATE from recursing"
                );
        }

        foreach (var placeholderColumn in DescriptorTableColumnExtractor.PlaceholderColumns)
        {
            _ddl.Should()
                .NotContain(
                    $"i.[{placeholderColumn}] <> del.[{placeholderColumn}]",
                    "this column is a permanently-NULL placeholder no writer ever sets, so diffing it "
                        + "could never detect a change and must not be emitted"
                );
        }

        foreach (var computedColumn in DescriptorTableColumnExtractor.EngineComputedColumns)
        {
            _ddl.Should()
                .NotContain(
                    $"CAST(i.[{computedColumn}] AS varbinary(max)) <> CAST(del.[{computedColumn}] AS varbinary(max))",
                    "this column is derived from another column in the same row, so any change to it is "
                        + "already detected through its source column"
                );
            // Both comparator forms are excluded: the loop above picks the cast form for string
            // columns, but a future change that emitted the plain form would otherwise slip past.
            _ddl.Should()
                .NotContain(
                    $"i.[{computedColumn}] <> del.[{computedColumn}]",
                    "this column is derived from another column in the same row, so any change to it is "
                        + "already detected through its source column"
                );
        }
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

    [Test]
    public void It_should_create_get_max_change_version_function()
    {
        _ddl.Should().Contain("CREATE OR ALTER FUNCTION [dms].[GetMaxChangeVersion]()");
    }

    [Test]
    public void It_should_emit_get_max_change_version_after_change_version_sequence()
    {
        var sequence = _ddl.IndexOf(
            "CREATE SEQUENCE [dms].[ChangeVersionSequence]",
            StringComparison.Ordinal
        );
        var function = _ddl.IndexOf("[dms].[GetMaxChangeVersion]", StringComparison.Ordinal);

        sequence.Should().BeGreaterThan(0);
        function.Should().BeGreaterThan(sequence);
    }

    [Test]
    public void It_should_emit_get_max_change_version_before_uuidv5()
    {
        var getMax = _ddl.IndexOf("[dms].[GetMaxChangeVersion]", StringComparison.Ordinal);
        var uuidv5 = _ddl.IndexOf("[dms].[uuidv5]", StringComparison.Ordinal);

        getMax.Should().BeGreaterThan(0);
        getMax.Should().BeLessThan(uuidv5);
    }

    [Test]
    public void It_should_emit_get_max_change_version_before_phase_5_tables()
    {
        var function = _ddl.IndexOf("[dms].[GetMaxChangeVersion]", StringComparison.Ordinal);
        var phase5 = _ddl.IndexOf("Phase 5: Tables", StringComparison.Ordinal);

        function.Should().BeGreaterThan(0);
        function.Should().BeLessThan(phase5);
    }

    [Test]
    public void It_should_isolate_each_function_in_its_own_go_batch()
    {
        var search = 0;
        var occurrences = 0;
        while (true)
        {
            var idx = _ddl.IndexOf("CREATE OR ALTER FUNCTION", search, StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }
            occurrences++;

            var goIdx = _ddl.LastIndexOf("GO\n", idx, StringComparison.Ordinal);
            goIdx
                .Should()
                .BeGreaterOrEqualTo(0, "every CREATE OR ALTER FUNCTION must be preceded by a GO line");
            var between = _ddl.Substring(goIdx + 3, idx - goIdx - 3);
            between.Trim().Should().BeEmpty("GO should immediately precede CREATE OR ALTER FUNCTION");

            search = idx + 1;
        }

        occurrences.Should().Be(2, "expected exactly two function batches: GetMaxChangeVersion and uuidv5");
    }

    [Test]
    public void It_should_bind_get_max_change_version_to_document_content_version_sequence()
    {
        _ddl.Should()
            .Contain(
                "CONSTRAINT [DF_Document_ContentVersion] DEFAULT (NEXT VALUE FOR [dms].[ChangeVersionSequence])"
            );
        _ddl.Should().Contain("seq.name = 'ChangeVersionSequence' AND sch.name = 'dms'");
    }
}

// ═══════════════════════════════════════════════════════════════════
// SharedDescriptor tombstone tests
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Builds a <see cref="TrackedChangeTableInfo"/> for the shared descriptor tombstone fixture used by
/// <see cref="Given_CoreDdlEmitter_With_SharedDescriptor_TrackedChange_Pgsql"/>,
/// <see cref="Given_CoreDdlEmitter_With_SharedDescriptor_TrackedChange_Mssql"/>, and
/// <see cref="Given_CoreDdlEmitter_With_NonDescriptor_TrackedChange_Kind"/>.
/// </summary>
internal static class SharedDescriptorTrackedChangeFixture
{
    /// <summary>
    /// Slices the descriptor tombstone <c>INSERT … ;</c> statement out of an emitted script, so
    /// assertions cannot accidentally match the surrounding stamp statements — which legitimately
    /// still read and write <c>dms.Document</c>.
    /// </summary>
    internal static string TombstoneInsert(string ddl, string insertPrefix)
    {
        var start = ddl.IndexOf(insertPrefix, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the descriptor tombstone INSERT must be present");
        var end = ddl.IndexOf(";\n", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the descriptor tombstone INSERT must be terminated");
        return ddl[start..(end + 2)];
    }

    internal static TrackedChangeTableInfo Build() =>
        new(
            new DbTableName(new DbSchemaName("tracked_changes_edfi"), "Descriptor"),
            TrackedChangeTableKind.SharedDescriptor,
            DmsTableNames.Descriptor,
            [
                new TrackedChangeColumnInfo(
                    new DbColumnName("OldNamespace"),
                    new DbColumnName("NewNamespace"),
                    "$.namespace",
                    null,
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    new RelationalScalarType(ScalarKind.String),
                    TrackedChangeColumnRole.Scalar,
                    TrackedChangeColumnOrigin.Identity
                ),
                new TrackedChangeColumnInfo(
                    new DbColumnName("OldCodeValue"),
                    new DbColumnName("NewCodeValue"),
                    "$.codeValue",
                    null,
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    new RelationalScalarType(ScalarKind.String),
                    TrackedChangeColumnRole.Scalar,
                    TrackedChangeColumnOrigin.Identity
                ),
            ],
            [
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.Discriminator,
                    new DbColumnName("Discriminator"),
                    new RelationalScalarType(ScalarKind.String),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.Id,
                    new DbColumnName("Id"),
                    null,
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.ChangeVersion,
                    new DbColumnName("ChangeVersion"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: true
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.CreatedAt,
                    new DbColumnName("CreatedAt"),
                    null,
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
            ],
            [new DbColumnName("ChangeVersion")],
            [],
            []
        );
}

[TestFixture]
public class Given_CoreDdlEmitter_With_SharedDescriptor_TrackedChange_Pgsql
{
    private string _ddl = default!;
    private static readonly ISqlDialect _pgsqlDialect = new PgsqlDialect(new PgsqlDialectRules());

    [SetUp]
    public void Setup()
    {
        _ddl = new CoreDdlEmitter(_pgsqlDialect, SharedDescriptorTrackedChangeFixture.Build()).Emit();
    }

    [Test]
    public void It_should_contain_insert_into_tracked_changes_descriptor()
    {
        _ddl.Should().Contain("INSERT INTO \"tracked_changes_edfi\".\"Descriptor\"");
    }

    [Test]
    public void It_should_read_discriminator_namespace_and_code_value_from_old_image()
    {
        _ddl.Should().Contain("OLD.\"Discriminator\"");
        _ddl.Should().Contain("OLD.\"Namespace\"");
        _ddl.Should().Contain("OLD.\"CodeValue\"");
    }

    [Test]
    public void It_should_read_document_uuid_from_the_old_image_and_change_version_from_the_stamp()
    {
        var tombstone = SharedDescriptorTrackedChangeFixture.TombstoneInsert(
            _ddl,
            "INSERT INTO \"tracked_changes_edfi\".\"Descriptor\""
        );

        tombstone.Should().Contain("OLD.\"DocumentUuid\",");
        tombstone.Should().Contain("_stampedContentVersion");
        tombstone.Should().NotContain("\"dms\".\"Document\"");
        // No joins on the shared descriptor tombstone, so the SELECT stands alone.
        tombstone.Should().NotContain("FROM");
    }

    [Test]
    public void It_should_declare_and_assign_the_change_version_for_the_tombstone()
    {
        // The descriptor trigger function had no locals before the tombstone needed a post-delete
        // ContentVersion; the DELETE branch now takes one straight from the change-version sequence
        // instead of reading a stamp back out of dms.Document.
        _ddl.Should().Contain("DECLARE\n    _stampedContentVersion bigint;\n");
        _ddl.Should().Contain("_stampedContentVersion := nextval('\"dms\".\"ChangeVersionSequence\"');");
    }

    [Test]
    public void It_should_not_stamp_the_document_row_in_the_delete_branch()
    {
        var deleteStart = _ddl.IndexOf("ELSIF TG_OP = 'DELETE' THEN", StringComparison.Ordinal);
        deleteStart.Should().BeGreaterOrEqualTo(0);
        var deleteEnd = _ddl.IndexOf("RETURN OLD;", deleteStart, StringComparison.Ordinal);
        deleteEnd.Should().BeGreaterThan(deleteStart);

        var deleteBranch = _ddl[deleteStart..deleteEnd];
        deleteBranch.Should().NotContain("UPDATE \"dms\".\"Document\"");
        deleteBranch.Should().NotContain("INTO STRICT _stampedContentVersion;");
    }

    [Test]
    public void It_should_place_tombstone_insert_after_the_change_version_and_before_return_old_in_delete_branch()
    {
        var deleteStart = _ddl.IndexOf("ELSIF TG_OP = 'DELETE' THEN", StringComparison.Ordinal);
        deleteStart.Should().BeGreaterOrEqualTo(0);

        var changeVersionAssignment = _ddl.IndexOf(
            "_stampedContentVersion := nextval('\"dms\".\"ChangeVersionSequence\"');",
            deleteStart,
            StringComparison.Ordinal
        );
        changeVersionAssignment.Should().BeGreaterThan(deleteStart);

        var tombstoneInsert = _ddl.IndexOf(
            "INSERT INTO \"tracked_changes_edfi\".\"Descriptor\"",
            deleteStart,
            StringComparison.Ordinal
        );
        tombstoneInsert.Should().BeGreaterThan(changeVersionAssignment);

        var returnOld = _ddl.IndexOf("RETURN OLD;", deleteStart, StringComparison.Ordinal);
        returnOld.Should().BeGreaterThan(tombstoneInsert);
    }

    [Test]
    public void It_should_not_contain_tracked_changes_when_no_inventory_provided()
    {
        var noInventoryDdl = new CoreDdlEmitter(_pgsqlDialect).Emit();
        noInventoryDdl.Should().NotContain("tracked_changes_edfi");
    }
}

[TestFixture]
public class Given_CoreDdlEmitter_With_SharedDescriptor_TrackedChange_Mssql
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _ddl = new CoreDdlEmitter(dialect, SharedDescriptorTrackedChangeFixture.Build()).Emit();
    }

    [Test]
    public void It_should_guard_tombstone_with_deleted_exists_and_not_inserted_exists()
    {
        _ddl.Should().Contain("IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)");
    }

    [Test]
    public void It_should_contain_insert_into_tracked_changes_descriptor()
    {
        _ddl.Should().Contain("INSERT INTO [tracked_changes_edfi].[Descriptor]");
    }

    [Test]
    public void It_should_read_discriminator_namespace_and_code_value_from_del_alias()
    {
        _ddl.Should().Contain("del.[Discriminator]");
        _ddl.Should().Contain("del.[Namespace]");
        _ddl.Should().Contain("del.[CodeValue]");
    }

    [Test]
    public void It_should_read_document_uuid_from_the_deleted_image_and_change_version_from_the_sequence()
    {
        var tombstone = SharedDescriptorTrackedChangeFixture.TombstoneInsert(
            _ddl,
            "INSERT INTO [tracked_changes_edfi].[Descriptor]"
        );

        tombstone.Should().Contain("del.[DocumentUuid],");
        tombstone.Should().Contain("NEXT VALUE FOR [dms].[ChangeVersionSequence]");
        tombstone.Should().NotContain("@stamped");
        tombstone.Should().NotContain("[dms].[Document]");
    }

    [Test]
    public void It_should_select_the_tombstone_from_deleted_del_alone()
    {
        _ddl.Should().Contain("FROM deleted del;");
        _ddl.Should().NotContain("INNER JOIN @stamped s ON s.[DocumentId] = del.[DocumentId];");
    }

    [Test]
    public void It_should_not_stamp_the_descriptor_row_for_pure_descriptor_deletes()
    {
        // The delete arm of affectedDocs is gone: a pure delete allocates its change version from
        // the sequence in the tombstone INSERT and stamps nothing.
        var affectedDocsIdx = _ddl.IndexOf(";WITH affectedDocs AS (", StringComparison.Ordinal);
        affectedDocsIdx.Should().BeGreaterOrEqualTo(0);

        var worksetIdx = _ddl.IndexOf(
            "INSERT INTO @stamped ([DocumentId])",
            affectedDocsIdx,
            StringComparison.Ordinal
        );
        worksetIdx.Should().BeGreaterThan(affectedDocsIdx);

        _ddl[affectedDocsIdx..worksetIdx].Should().NotContain("UNION");
    }

    [Test]
    public void It_should_place_the_tombstone_after_the_descriptor_stamp_update()
    {
        var descriptorStampUpdate = _ddl.IndexOf(
            "INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];",
            StringComparison.Ordinal
        );
        descriptorStampUpdate.Should().BeGreaterOrEqualTo(0);

        var tombstoneGuard = _ddl.IndexOf(
            "IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)",
            StringComparison.Ordinal
        );
        tombstoneGuard.Should().BeGreaterThan(descriptorStampUpdate);
    }

    [Test]
    public void It_should_not_have_standalone_semicolon_line_in_tombstone_block()
    {
        // House style: terminator must be on the last content line, not on a line by itself.
        var tombstoneStart = _ddl.IndexOf(
            "IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)",
            StringComparison.Ordinal
        );
        tombstoneStart.Should().BeGreaterOrEqualTo(0);
        var tombstoneEnd = _ddl.IndexOf("END", tombstoneStart + 1, StringComparison.Ordinal);
        tombstoneEnd.Should().BeGreaterThan(tombstoneStart);
        var tombstoneBlock = _ddl[tombstoneStart..tombstoneEnd];
        tombstoneBlock.Should().NotContain("\n;\n");
    }
}

[TestFixture]
public class Given_CoreDdlEmitter_With_NonDescriptor_TrackedChange_Kind
{
    [Test]
    public void It_should_throw_when_kind_is_not_shared_descriptor()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var fixture = SharedDescriptorTrackedChangeFixture.Build() with
        {
            Kind = TrackedChangeTableKind.Resource,
        };

        var act = () => new CoreDdlEmitter(dialect, fixture);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SharedDescriptor*");
    }
}

/// <summary>
/// Test fixture for the core-owned descriptor stamping trigger. The trigger is derived into
/// <c>DerivedRelationalModelSet.TriggersInCreateOrder</c> (by <c>DeriveTriggerInventoryPass</c>) so its
/// change-tracking attachment flows through manifests/planners, but <c>dms.Descriptor</c> is a core
/// table whose stamping trigger SQL is rendered by <see cref="CoreDdlEmitter"/>. These tests pin the
/// rendered name and target table; <c>DeriveTriggerInventoryPassTests</c> pins the derived entry to the
/// same literals, and the two together guard against drift between the derivation constants and the
/// rendered DDL.
/// </summary>
[TestFixture]
public class Given_CoreDdlEmitter_Descriptor_Stamping_Trigger_Metadata
{
    private string _pgsqlDdl = default!;

    [SetUp]
    public void Setup()
    {
        _pgsqlDdl = new CoreDdlEmitter(new PgsqlDialect(new PgsqlDialectRules())).Emit();
    }

    [Test]
    public void It_should_render_the_descriptor_stamping_trigger_targeting_dms_Descriptor()
    {
        _pgsqlDdl.Should().Contain("CREATE TRIGGER \"TR_Descriptor_Stamp_Document\"");
        _pgsqlDdl.Should().Contain("AFTER INSERT OR UPDATE OR DELETE ON \"dms\".\"Descriptor\"");
    }

    [Test]
    public void It_should_emit_exactly_one_descriptor_stamping_trigger()
    {
        CountOccurrences(_pgsqlDdl, "CREATE TRIGGER \"TR_Descriptor_Stamp_Document\"").Should().Be(1);
    }

    [Test]
    public void It_should_not_emit_the_derived_descriptor_change_version_index()
    {
        // IX_Descriptor_Discriminator_ContentVersion is derived-inventory-owned and rendered once by the
        // relational DDL emitter; the core emitter must not also emit it (which would duplicate it in the
        // full DDL). The core emitter still owns IX_Descriptor_Uri_Discriminator.
        _pgsqlDdl.Should().NotContain("IX_Descriptor_Discriminator_ContentVersion");
        _pgsqlDdl.Should().Contain("IX_Descriptor_Uri_Discriminator");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
