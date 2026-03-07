// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

// ═══════════════════════════════════════════════════════════════════
// Test data helpers
// ═══════════════════════════════════════════════════════════════════

internal static class SeedTestData
{
    internal const string ValidEffectiveSchemaHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static EffectiveSchemaInfo BuildEffectiveSchema() =>
        new(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: ValidEffectiveSchemaHash,
            ResourceKeyCount: 3,
            ResourceKeySeedHash:
            [
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
            ],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.1.0", false, "hash1"),
                new SchemaComponentInfo("tpdm", "TPDM", "1.0.0", true, "hash2"),
            ],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "AcademicWeek"), "5.1.0", false),
                new ResourceKeyEntry(2, new QualifiedResourceName("Ed-Fi", "School"), "5.1.0", false),
                new ResourceKeyEntry(3, new QualifiedResourceName("Ed-Fi", "Student"), "5.1.0", false),
            ]
        );

    internal static EffectiveSchemaInfo BuildEmptyResourceKeys() =>
        new(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: ValidEffectiveSchemaHash,
            ResourceKeyCount: 0,
            ResourceKeySeedHash:
            [
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
            ],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.1.0", false, "hash1"),
            ],
            ResourceKeysInIdOrder: []
        );

    internal static EffectiveSchemaInfo BuildSingleSchemaComponent() =>
        new(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: ValidEffectiveSchemaHash,
            ResourceKeyCount: 1,
            ResourceKeySeedHash:
            [
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
            ],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.1.0", false, "hash1"),
            ],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "Student"), "5.1.0", false),
            ]
        );
}

// ═══════════════════════════════════════════════════════════════════
// Determinism tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_SeedDmlEmitter_With_PgsqlDialect_And_SeedData_Emitting_Twice
{
    private string _first = default!;
    private string _second = default!;

    [SetUp]
    public void Setup()
    {
        var schema = SeedTestData.BuildEffectiveSchema();
        var emitter = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules()));
        _first = emitter.Emit(schema);
        _second = emitter.Emit(schema);
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
public class Given_SeedDmlEmitter_With_MssqlDialect_And_SeedData_Emitting_Twice
{
    private string _first = default!;
    private string _second = default!;

    [SetUp]
    public void Setup()
    {
        var schema = SeedTestData.BuildEffectiveSchema();
        var emitter = new SeedDmlEmitter(new MssqlDialect(new MssqlDialectRules()));
        _first = emitter.Emit(schema);
        _second = emitter.Emit(schema);
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
public class Given_SeedDmlEmitter_With_Empty_ApiSchemaFormatVersion
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules()));

        _exception = Assert.Catch<InvalidOperationException>(() =>
            emitter.Emit(SeedTestData.BuildEffectiveSchema() with { ApiSchemaFormatVersion = " " })
        );
    }

    [Test]
    public void It_rejects_runtime_invalid_effective_schema_metadata()
    {
        _exception!.Message.Should().Contain("ApiSchemaFormatVersion must not be empty.");
    }
}

[TestFixture]
public class Given_SeedDmlEmitter_With_A_Non_Lowercase_Hex_EffectiveSchemaHash
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules()));

        _exception = Assert.Catch<InvalidOperationException>(() =>
            emitter.Emit(
                SeedTestData.BuildEffectiveSchema() with
                {
                    EffectiveSchemaHash = $"{new string('a', 63)}G",
                }
            )
        );
    }

    [Test]
    public void It_rejects_runtime_invalid_effective_schema_metadata()
    {
        _exception!.Message.Should().Contain("EffectiveSchemaHash must be 64 lowercase hex characters.");
    }
}

[TestFixture]
public class Given_SeedDmlEmitter_With_A_Negative_ResourceKeyCount
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules()));

        _exception = Assert.Catch<InvalidOperationException>(() =>
            emitter.Emit(SeedTestData.BuildEffectiveSchema() with { ResourceKeyCount = -1 })
        );
    }

    [Test]
    public void It_rejects_runtime_invalid_effective_schema_metadata()
    {
        _exception!.Message.Should().Contain("ResourceKeyCount must be non-negative, but found -1.");
    }
}

[TestFixture]
public class Given_SeedDmlEmitter_With_A_Short_ResourceKeySeedHash
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules()));

        _exception = Assert.Catch<InvalidOperationException>(() =>
            emitter.Emit(SeedTestData.BuildEffectiveSchema() with { ResourceKeySeedHash = new byte[31] })
        );
    }

    [Test]
    public void It_rejects_runtime_invalid_effective_schema_metadata()
    {
        _exception!.Message.Should().Contain("ResourceKeySeedHash must be exactly 32 bytes, but found 31.");
    }
}

// ═══════════════════════════════════════════════════════════════════
// PostgreSQL snapshot tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_SeedDmlEmitter_With_PgsqlDialect_And_SeedData
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules()));
        _ddl = emitter.Emit(SeedTestData.BuildEffectiveSchema());
    }

    [Test]
    public void It_should_emit_phase_7_header()
    {
        _ddl.Should().Contain("Phase 7: Seed Data");
    }

    [Test]
    public void It_should_not_emit_preflight_check()
    {
        _ddl.Should().NotContain("Preflight: fail fast");
        _ddl.Should().NotContain("EffectiveSchemaHash mismatch");
    }

    [Test]
    public void It_should_emit_resource_key_inserts_with_on_conflict_do_nothing()
    {
        _ddl.Should().Contain("ON CONFLICT (\"ResourceKeyId\") DO NOTHING;");
    }

    [Test]
    public void It_should_emit_resource_key_inserts_in_id_order()
    {
        var insert1 = _ddl.IndexOf("'AcademicWeek'", StringComparison.Ordinal);
        var insert2 = _ddl.IndexOf("'School'", StringComparison.Ordinal);
        var insert3 = _ddl.IndexOf("'Student'", StringComparison.Ordinal);

        insert1.Should().BeGreaterOrEqualTo(0);
        insert2.Should().BeGreaterThan(insert1);
        insert3.Should().BeGreaterThan(insert2);
    }

    [Test]
    public void It_should_emit_resource_key_validation_block()
    {
        _ddl.Should().Contain("dms.ResourceKey count mismatch");
        _ddl.Should().Contain("dms.ResourceKey contents mismatch");
    }

    [Test]
    public void It_should_include_resource_key_ids_in_content_mismatch_error()
    {
        _ddl.Should().Contain("ResourceKeyIds: %");
        _ddl.Should().Contain("string_agg(sub.id, ', ' ORDER BY sub.id_num)");
        _ddl.Should().Contain("LIMIT 10");
    }

    [Test]
    public void It_should_emit_effective_schema_insert_with_on_conflict()
    {
        _ddl.Should().Contain("ON CONFLICT (\"EffectiveSchemaSingletonId\") DO NOTHING;");
    }

    [Test]
    public void It_should_emit_effective_schema_validation_block()
    {
        _ddl.Should().Contain("dms.EffectiveSchema ResourceKeyCount mismatch");
        _ddl.Should().Contain("dms.EffectiveSchema ResourceKeySeedHash mismatch");
    }

    [Test]
    public void It_should_emit_schema_component_inserts_in_endpoint_order()
    {
        var edfi = _ddl.IndexOf("'ed-fi'", StringComparison.Ordinal);
        var tpdm = _ddl.IndexOf("'tpdm'", StringComparison.Ordinal);

        edfi.Should().BeGreaterOrEqualTo(0);
        tpdm.Should().BeGreaterThan(edfi);
    }

    [Test]
    public void It_should_emit_schema_component_validation_block()
    {
        _ddl.Should().Contain("dms.SchemaComponent count mismatch");
        _ddl.Should().Contain("dms.SchemaComponent contents mismatch");
    }

    [Test]
    public void It_should_include_project_endpoint_names_in_schema_component_mismatch_error()
    {
        _ddl.Should().Contain("ProjectEndpointNames: %");
        _ddl.Should().Contain("string_agg(sub.name, ', ' ORDER BY sub.name)");
    }

    [Test]
    public void It_should_use_bytea_literal_for_seed_hash()
    {
        _ddl.Should().Contain("'\\xABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789'::bytea");
    }

    [Test]
    public void It_should_use_boolean_literals_for_is_extension()
    {
        _ddl.Should().Contain("false)");
        _ddl.Should().Contain("true)");
    }

    [Test]
    public void It_should_use_raise_exception_for_errors()
    {
        _ddl.Should().Contain("RAISE EXCEPTION");
        _ddl.Should().NotContain("THROW 50000");
    }

    [Test]
    public void It_should_use_unix_line_endings()
    {
        _ddl.Should().NotContain("\r\n");
    }

    [Test]
    public void It_should_not_have_trailing_whitespace()
    {
        var lines = _ddl.Split('\n');
        foreach (var line in lines)
        {
            line.Should().Be(line.TrimEnd(), $"line has trailing whitespace: [{line}]");
        }
    }

    [Test]
    public void It_should_not_contain_mssql_syntax()
    {
        _ddl.Should().NotContain("THROW 50000");
        _ddl.Should().NotContain("N'");
    }
}

// ═══════════════════════════════════════════════════════════════════
// SQL Server snapshot tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_SeedDmlEmitter_With_MssqlDialect_And_SeedData
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new MssqlDialect(new MssqlDialectRules()));
        _ddl = emitter.Emit(SeedTestData.BuildEffectiveSchema());
    }

    [Test]
    public void It_should_emit_phase_7_header()
    {
        _ddl.Should().Contain("Phase 7: Seed Data");
    }

    [Test]
    public void It_should_not_emit_preflight_check()
    {
        _ddl.Should().NotContain("Preflight: fail fast");
        _ddl.Should().NotContain("EffectiveSchemaHash mismatch");
    }

    [Test]
    public void It_should_emit_resource_key_inserts_with_if_not_exists()
    {
        _ddl.Should().Contain("IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 1)");
    }

    [Test]
    public void It_should_emit_resource_key_inserts_in_id_order()
    {
        var insert1 = _ddl.IndexOf("N'AcademicWeek'", StringComparison.Ordinal);
        var insert2 = _ddl.IndexOf("N'School'", StringComparison.Ordinal);
        var insert3 = _ddl.IndexOf("N'Student'", StringComparison.Ordinal);

        insert1.Should().BeGreaterOrEqualTo(0);
        insert2.Should().BeGreaterThan(insert1);
        insert3.Should().BeGreaterThan(insert2);
    }

    [Test]
    public void It_should_emit_resource_key_validation_block()
    {
        _ddl.Should().Contain("dms.ResourceKey count mismatch: expected 3, found");
        _ddl.Should().Contain("dms.ResourceKey contents mismatch:");
    }

    [Test]
    public void It_should_include_resource_key_ids_in_content_mismatch_error()
    {
        _ddl.Should().Contain("ResourceKeyIds: ");
        _ddl.Should()
            .Contain("STRING_AGG(sub.[ResourceKeyId], N', ') WITHIN GROUP (ORDER BY sub.[ResourceKeyIdNum])");
        _ddl.Should().Contain("TOP 10");
    }

    [Test]
    public void It_should_wrap_mssql_validation_throws_in_begin_end()
    {
        _ddl.Should().Contain("BEGIN");
        _ddl.Should().Contain("END");
    }

    [Test]
    public void It_should_emit_effective_schema_insert_with_if_not_exists()
    {
        _ddl.Should()
            .Contain(
                "IF NOT EXISTS (SELECT 1 FROM [dms].[EffectiveSchema] WHERE [EffectiveSchemaSingletonId] = 1)"
            );
    }

    [Test]
    public void It_should_emit_effective_schema_validation_block()
    {
        _ddl.Should().Contain("dms.EffectiveSchema ResourceKeyCount mismatch: expected 3, found");
        _ddl.Should().Contain("dms.EffectiveSchema ResourceKeySeedHash mismatch:");
    }

    [Test]
    public void It_should_emit_schema_component_inserts_in_endpoint_order()
    {
        var edfi = _ddl.IndexOf("N'ed-fi'", StringComparison.Ordinal);
        var tpdm = _ddl.IndexOf("N'tpdm'", StringComparison.Ordinal);

        edfi.Should().BeGreaterOrEqualTo(0);
        tpdm.Should().BeGreaterThan(edfi);
    }

    [Test]
    public void It_should_emit_schema_component_validation_block()
    {
        _ddl.Should().Contain("dms.SchemaComponent count mismatch: expected 2, found");
        _ddl.Should().Contain("dms.SchemaComponent contents mismatch:");
    }

    [Test]
    public void It_should_include_project_endpoint_names_in_schema_component_mismatch_error()
    {
        _ddl.Should().Contain("ProjectEndpointNames: ");
        _ddl.Should()
            .Contain(
                "STRING_AGG(sub.[ProjectEndpointName], N', ') WITHIN GROUP (ORDER BY sub.[ProjectEndpointName])"
            );
    }

    [Test]
    public void It_should_use_binary_hex_literal_for_seed_hash()
    {
        _ddl.Should().Contain("0xABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789");
    }

    [Test]
    public void It_should_use_bit_literals_for_is_extension()
    {
        _ddl.Should().Contain("0)");
        _ddl.Should().Contain("1)");
    }

    [Test]
    public void It_should_use_throw_for_errors()
    {
        _ddl.Should().Contain("THROW 50000");
        _ddl.Should().NotContain("RAISE EXCEPTION");
    }

    [Test]
    public void It_should_use_nvarchar_string_literals()
    {
        _ddl.Should().Contain("N'Ed-Fi'");
        _ddl.Should().Contain("N'Student'");
    }

    [Test]
    public void It_should_use_unix_line_endings()
    {
        _ddl.Should().NotContain("\r\n");
    }

    [Test]
    public void It_should_not_have_trailing_whitespace()
    {
        var lines = _ddl.Split('\n');
        foreach (var line in lines)
        {
            line.Should().Be(line.TrimEnd(), $"line has trailing whitespace: [{line}]");
        }
    }

    [Test]
    public void It_should_not_contain_pgsql_syntax()
    {
        _ddl.Should().NotContain("RAISE EXCEPTION");
        _ddl.Should().NotContain("::bytea");
        _ddl.Should().NotContain("DO $$");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Validation logic tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_SeedDmlEmitter_With_PgsqlDialect_And_ResourceKeys
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules()));
        _ddl = emitter.Emit(SeedTestData.BuildEffectiveSchema());
    }

    [Test]
    public void It_should_validate_resource_key_count()
    {
        _ddl.Should().Contain("_actual_count <> 3");
    }

    [Test]
    public void It_should_validate_resource_key_contents()
    {
        _ddl.Should().Contain("1::smallint, 'Ed-Fi', 'AcademicWeek', '5.1.0'");
        _ddl.Should().Contain("2::smallint, 'Ed-Fi', 'School', '5.1.0'");
        _ddl.Should().Contain("3::smallint, 'Ed-Fi', 'Student', '5.1.0'");
    }
}

[TestFixture]
public class Given_SeedDmlEmitter_With_MssqlDialect_And_ResourceKeys
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new MssqlDialect(new MssqlDialectRules()));
        _ddl = emitter.Emit(SeedTestData.BuildEffectiveSchema());
    }

    [Test]
    public void It_should_validate_resource_key_count()
    {
        _ddl.Should().Contain("@actual_count <> 3");
    }

    [Test]
    public void It_should_validate_resource_key_contents()
    {
        _ddl.Should().Contain("1, N'Ed-Fi', N'AcademicWeek', N'5.1.0'");
        _ddl.Should().Contain("2, N'Ed-Fi', N'School', N'5.1.0'");
        _ddl.Should().Contain("3, N'Ed-Fi', N'Student', N'5.1.0'");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Edge cases
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_SeedDmlEmitter_With_Empty_ResourceKeys
{
    private string _pgsqlDdl = default!;
    private string _mssqlDdl = default!;

    [SetUp]
    public void Setup()
    {
        var schema = SeedTestData.BuildEmptyResourceKeys();
        _pgsqlDdl = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules())).Emit(schema);
        _mssqlDdl = new SeedDmlEmitter(new MssqlDialect(new MssqlDialectRules())).Emit(schema);
    }

    [Test]
    public void It_should_still_emit_phase_7_header_for_pgsql()
    {
        _pgsqlDdl.Should().Contain("Phase 7: Seed Data");
    }

    [Test]
    public void It_should_still_emit_phase_7_header_for_mssql()
    {
        _mssqlDdl.Should().Contain("Phase 7: Seed Data");
    }

    [Test]
    public void It_should_not_emit_resource_key_inserts_for_pgsql()
    {
        _pgsqlDdl.Should().NotContain("INSERT INTO \"dms\".\"ResourceKey\"");
    }

    [Test]
    public void It_should_not_emit_resource_key_inserts_for_mssql()
    {
        _mssqlDdl.Should().NotContain("INSERT INTO [dms].[ResourceKey]");
    }

    [Test]
    public void It_should_still_emit_effective_schema_insert_for_pgsql()
    {
        _pgsqlDdl.Should().Contain("INSERT INTO \"dms\".\"EffectiveSchema\"");
    }

    [Test]
    public void It_should_still_emit_effective_schema_insert_for_mssql()
    {
        _mssqlDdl.Should().Contain("INSERT INTO [dms].[EffectiveSchema]");
    }
}

[TestFixture]
public class Given_SeedDmlEmitter_With_Single_SchemaComponent
{
    private string _pgsqlDdl = default!;
    private string _mssqlDdl = default!;

    [SetUp]
    public void Setup()
    {
        var schema = SeedTestData.BuildSingleSchemaComponent();
        _pgsqlDdl = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules())).Emit(schema);
        _mssqlDdl = new SeedDmlEmitter(new MssqlDialect(new MssqlDialectRules())).Emit(schema);
    }

    [Test]
    public void It_should_emit_single_schema_component_for_pgsql()
    {
        _pgsqlDdl.Should().Contain("'ed-fi'");
        _pgsqlDdl.Should().NotContain("'tpdm'");
    }

    [Test]
    public void It_should_emit_single_schema_component_for_mssql()
    {
        _mssqlDdl.Should().Contain("N'ed-fi'");
        _mssqlDdl.Should().NotContain("N'tpdm'");
    }

    [Test]
    public void It_should_validate_single_component_count_for_pgsql()
    {
        _pgsqlDdl.Should().Contain("_actual_count <> 1");
    }

    [Test]
    public void It_should_validate_single_component_count_for_mssql()
    {
        _mssqlDdl.Should().Contain("@sc_actual_count <> 1");
    }
}

// ═══════════════════════════════════════════════════════════════════
// EmitPreflightOnly tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_SeedDmlEmitter_EmitPreflightOnly_With_PgsqlDialect
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules()));
        _sql = emitter.EmitPreflightOnly(SeedTestData.ValidEffectiveSchemaHash);
    }

    [Test]
    public void It_should_emit_preflight_comment()
    {
        _sql.Should().Contain("Preflight: fail fast");
    }

    [Test]
    public void It_should_emit_hash_mismatch_check()
    {
        _sql.Should().Contain("EffectiveSchemaHash mismatch");
        _sql.Should().Contain("to_regclass");
        _sql.Should().Contain("RAISE EXCEPTION");
        _sql.Should().Contain("_stored_hash");
        _sql.Should().Contain("but expected");
        _sql.Should().Contain($"'{SeedTestData.ValidEffectiveSchemaHash}'");
    }

    [Test]
    public void It_should_not_contain_phase_7_header()
    {
        _sql.Should().NotContain("Phase 7");
    }

    [Test]
    public void It_should_use_unix_line_endings()
    {
        _sql.Should().NotContain("\r\n");
    }
}

[TestFixture]
public class Given_SeedDmlEmitter_EmitPreflightOnly_With_MssqlDialect
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new MssqlDialect(new MssqlDialectRules()));
        _sql = emitter.EmitPreflightOnly(SeedTestData.ValidEffectiveSchemaHash);
    }

    [Test]
    public void It_should_emit_preflight_comment()
    {
        _sql.Should().Contain("Preflight: fail fast");
    }

    [Test]
    public void It_should_emit_hash_mismatch_check()
    {
        _sql.Should().Contain("EffectiveSchemaHash mismatch");
        _sql.Should().Contain("OBJECT_ID");
        _sql.Should().Contain("THROW 50000");
        _sql.Should().Contain("@preflight_stored_hash");
        _sql.Should().Contain($"but expected ''', N'{SeedTestData.ValidEffectiveSchemaHash}'");
    }

    [Test]
    public void It_should_not_contain_phase_7_header()
    {
        _sql.Should().NotContain("Phase 7");
    }

    [Test]
    public void It_should_use_unix_line_endings()
    {
        _sql.Should().NotContain("\r\n");
    }
}

// ═══════════════════════════════════════════════════════════════════
// VALUES chunking tests (SQL Server 1000-row limit)
// ═══════════════════════════════════════════════════════════════════

internal static class ChunkingTestData
{
    internal static EffectiveSchemaInfo BuildLargeResourceKeySchema(int resourceKeyCount) =>
        new(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: SeedTestData.ValidEffectiveSchemaHash,
            ResourceKeyCount: resourceKeyCount,
            ResourceKeySeedHash:
            [
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
            ],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.1.0", false, "hash1"),
            ],
            ResourceKeysInIdOrder: Enumerable
                .Range(1, resourceKeyCount)
                .Select(i => new ResourceKeyEntry(
                    (short)i,
                    new QualifiedResourceName("Ed-Fi", $"Resource{i}"),
                    "5.1.0",
                    false
                ))
                .ToList()
        );
}

[TestFixture]
public class Given_SeedDmlEmitter_With_MssqlDialect_And_Over_999_ResourceKeys
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new MssqlDialect(new MssqlDialectRules()));
        _ddl = emitter.Emit(ChunkingTestData.BuildLargeResourceKeySchema(1001));
    }

    [Test]
    public void It_should_emit_union_all_between_chunks()
    {
        _ddl.Should().Contain("UNION ALL");
    }

    [Test]
    public void It_should_have_multiple_values_blocks()
    {
        var valuesCount = Regex.Matches(_ddl, @"\bVALUES\b").Count;
        // Each chunk emits a VALUES keyword; 1001 rows / 999 = 2 chunks per subquery invocation.
        // The subquery is used twice (count check + diagnostic), so expect at least 4 VALUES keywords.
        valuesCount.Should().BeGreaterThanOrEqualTo(4);
    }

    [Test]
    public void It_should_use_chunk_alias_for_inner_blocks()
    {
        _ddl.Should().Contain(") AS chunk(");
    }

    [Test]
    public void It_should_use_expected_alias_for_outer_wrapper()
    {
        _ddl.Should().Contain(") AS expected(");
    }

    [Test]
    public void It_should_still_contain_where_clause_with_expected_alias()
    {
        _ddl.Should().Contain("WHERE expected.[ResourceKeyId] = rk.[ResourceKeyId]");
    }
}

[TestFixture]
public class Given_SeedDmlEmitter_With_PgsqlDialect_And_Over_999_ResourceKeys
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new PgsqlDialect(new PgsqlDialectRules()));
        _ddl = emitter.Emit(ChunkingTestData.BuildLargeResourceKeySchema(1001));
    }

    [Test]
    public void It_should_emit_union_all_between_chunks()
    {
        _ddl.Should().Contain("UNION ALL");
    }

    [Test]
    public void It_should_use_chunk_alias_for_inner_blocks()
    {
        _ddl.Should().Contain(") AS chunk(");
    }

    [Test]
    public void It_should_use_expected_alias_for_outer_wrapper()
    {
        _ddl.Should().Contain(") AS expected(");
    }

    [Test]
    public void It_should_still_use_pgsql_smallint_cast()
    {
        _ddl.Should().Contain("::smallint");
    }
}

[TestFixture]
public class Given_SeedDmlEmitter_With_MssqlDialect_And_Exactly_999_ResourceKeys
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new MssqlDialect(new MssqlDialectRules()));
        _ddl = emitter.Emit(ChunkingTestData.BuildLargeResourceKeySchema(999));
    }

    [Test]
    public void It_should_not_emit_union_all()
    {
        _ddl.Should().NotContain("UNION ALL");
    }

    [Test]
    public void It_should_not_use_chunk_alias()
    {
        _ddl.Should().NotContain(") AS chunk(");
    }
}

[TestFixture]
public class Given_SeedDmlEmitter_With_MssqlDialect_And_1000_ResourceKeys
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var emitter = new SeedDmlEmitter(new MssqlDialect(new MssqlDialectRules()));
        _ddl = emitter.Emit(ChunkingTestData.BuildLargeResourceKeySchema(1000));
    }

    [Test]
    public void It_should_emit_union_all_at_the_boundary()
    {
        _ddl.Should().Contain("UNION ALL");
    }

    [Test]
    public void It_should_use_chunk_alias()
    {
        _ddl.Should().Contain(") AS chunk(");
    }
}
