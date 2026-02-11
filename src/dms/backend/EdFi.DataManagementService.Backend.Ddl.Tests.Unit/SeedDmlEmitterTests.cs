// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

// ═══════════════════════════════════════════════════════════════════
// Test data helpers
// ═══════════════════════════════════════════════════════════════════

internal static class SeedTestData
{
    internal static EffectiveSchemaInfo BuildEffectiveSchema() =>
        new(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "abc123def456",
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
            EffectiveSchemaHash: "abc123def456",
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
            EffectiveSchemaHash: "abc123def456",
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
    public void It_should_emit_effective_schema_hash_preflight_check()
    {
        _ddl.Should().Contain("EffectiveSchemaHash mismatch");
        _ddl.Should().Contain("RAISE EXCEPTION");
        _ddl.Should().Contain("'abc123def456'");
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
    public void It_should_emit_effective_schema_insert_with_on_conflict()
    {
        _ddl.Should().Contain("ON CONFLICT (\"EffectiveSchemaSingletonId\") DO NOTHING;");
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
    public void It_should_emit_effective_schema_hash_preflight_check()
    {
        _ddl.Should().Contain("EffectiveSchemaHash mismatch");
        _ddl.Should().Contain("THROW 50000");
        _ddl.Should().Contain("(expected: ', N'abc123def456'");
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
