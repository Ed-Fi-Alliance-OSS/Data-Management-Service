// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaTools.Introspection;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_ProvisionedSchemaManifestEmitter_With_Platform_Line_Endings_In_Definitions
{
    private string _manifestJson = null!;

    [SetUp]
    public void SetUp()
    {
        var manifest = new ProvisionedSchemaManifest(
            ManifestVersion: "1",
            Dialect: "mssql",
            Schemas: [],
            Tables: [],
            Columns: [],
            Constraints: [],
            Indexes: [],
            Views: [new ViewEntry("edfi", "SampleView", "CREATE VIEW [edfi].[SampleView] AS\r\nSELECT 1;")],
            Triggers:
            [
                new TriggerEntry(
                    "edfi",
                    "Sample",
                    "TR_Sample",
                    "INSERT",
                    "AFTER",
                    "CREATE TRIGGER [edfi].[TR_Sample]\rAS\rBEGIN\r\nSELECT 1;\rEND;",
                    null
                ),
            ],
            Sequences: [],
            TableTypes: [],
            Functions:
            [
                new FunctionEntry(
                    "dms",
                    "SampleFunction",
                    "scalar",
                    [],
                    "CREATE FUNCTION [dms].[SampleFunction]()\r\nRETURNS int"
                ),
            ],
            SeedData: new SeedData(new EffectiveSchemaEntry(1, "1.0.0", "hash", 0, "00"), [], [])
        );

        _manifestJson = ProvisionedSchemaManifestEmitter.Emit(manifest);
    }

    [Test]
    public void It_normalizes_definition_line_endings()
    {
        _manifestJson.Should().NotContain("\\r");
        _manifestJson.Should().Contain("CREATE VIEW [edfi].[SampleView] AS\\nSELECT 1;");
        _manifestJson.Should().Contain("CREATE TRIGGER [edfi].[TR_Sample]\\nAS\\nBEGIN\\nSELECT 1;\\nEND;");
        _manifestJson.Should().Contain("CREATE FUNCTION [dms].[SampleFunction]()\\nRETURNS int");
    }
}

[TestFixture]
public class Given_ProvisionedSchemaManifestEmitter_With_An_Index_Filter
{
    private string _manifestJson = null!;

    [SetUp]
    public void SetUp()
    {
        var manifest = new ProvisionedSchemaManifest(
            ManifestVersion: "1",
            Dialect: "mssql",
            Schemas: [],
            Tables: [],
            Columns: [],
            Constraints: [],
            Indexes:
            [
                new IndexEntry(
                    "dms",
                    "Document",
                    "IX_Document_CreatedByOwnershipTokenId",
                    false,
                    "([CreatedByOwnershipTokenId] IS NOT NULL)",
                    ["CreatedByOwnershipTokenId"]
                ),
            ],
            Views: [],
            Triggers: [],
            Sequences: [],
            TableTypes: [],
            Functions: [],
            SeedData: new SeedData(new EffectiveSchemaEntry(1, "1.0.0", "hash", 0, "00"), [], [])
        );

        _manifestJson = ProvisionedSchemaManifestEmitter.Emit(manifest);
    }

    [Test]
    public void It_writes_the_filter_between_is_unique_and_columns_in_key_order()
    {
        _manifestJson
            .Should()
            .Be(
                "{\n"
                    + "  \"manifest_version\": \"1\",\n"
                    + "  \"dialect\": \"mssql\",\n"
                    + "  \"schemas\": [],\n"
                    + "  \"tables\": [],\n"
                    + "  \"columns\": [],\n"
                    + "  \"constraints\": [],\n"
                    + "  \"indexes\": [\n"
                    + "    {\n"
                    + "      \"schema_name\": \"dms\",\n"
                    + "      \"table_name\": \"Document\",\n"
                    + "      \"index_name\": \"IX_Document_CreatedByOwnershipTokenId\",\n"
                    + "      \"is_unique\": false,\n"
                    + "      \"filter\": \"([CreatedByOwnershipTokenId] IS NOT NULL)\",\n"
                    + "      \"columns\": [\n"
                    + "        \"CreatedByOwnershipTokenId\"\n"
                    + "      ]\n"
                    + "    }\n"
                    + "  ],\n"
                    + "  \"views\": [],\n"
                    + "  \"triggers\": [],\n"
                    + "  \"sequences\": [],\n"
                    + "  \"table_types\": [],\n"
                    + "  \"functions\": [],\n"
                    + "  \"seed_data\": {\n"
                    + "    \"effective_schema\": {\n"
                    + "      \"effective_schema_singleton_id\": 1,\n"
                    + "      \"api_schema_format_version\": \"1.0.0\",\n"
                    + "      \"effective_schema_hash\": \"hash\",\n"
                    + "      \"resource_key_count\": 0,\n"
                    + "      \"resource_key_seed_hash\": \"00\"\n"
                    + "    },\n"
                    + "    \"schema_components\": [],\n"
                    + "    \"resource_keys\": []\n"
                    + "  }\n"
                    + "}\n"
            );
    }
}

[TestFixture]
public class Given_ProvisionedSchemaManifestEmitter_With_An_Index_Without_A_Filter
{
    private string _manifestJson = null!;

    [SetUp]
    public void SetUp()
    {
        var manifest = new ProvisionedSchemaManifest(
            ManifestVersion: "1",
            Dialect: "mssql",
            Schemas: [],
            Tables: [],
            Columns: [],
            Constraints: [],
            Indexes: [new IndexEntry("dms", "Document", "IX_Test", false, null, ["Col1"])],
            Views: [],
            Triggers: [],
            Sequences: [],
            TableTypes: [],
            Functions: [],
            SeedData: new SeedData(new EffectiveSchemaEntry(1, "1.0.0", "hash", 0, "00"), [], [])
        );

        _manifestJson = ProvisionedSchemaManifestEmitter.Emit(manifest);
    }

    [Test]
    public void It_omits_the_filter_key_and_matches_the_unfiltered_shape_byte_for_byte()
    {
        _manifestJson.Should().NotContain("\"filter\"");
        _manifestJson
            .Should()
            .Be(
                "{\n"
                    + "  \"manifest_version\": \"1\",\n"
                    + "  \"dialect\": \"mssql\",\n"
                    + "  \"schemas\": [],\n"
                    + "  \"tables\": [],\n"
                    + "  \"columns\": [],\n"
                    + "  \"constraints\": [],\n"
                    + "  \"indexes\": [\n"
                    + "    {\n"
                    + "      \"schema_name\": \"dms\",\n"
                    + "      \"table_name\": \"Document\",\n"
                    + "      \"index_name\": \"IX_Test\",\n"
                    + "      \"is_unique\": false,\n"
                    + "      \"columns\": [\n"
                    + "        \"Col1\"\n"
                    + "      ]\n"
                    + "    }\n"
                    + "  ],\n"
                    + "  \"views\": [],\n"
                    + "  \"triggers\": [],\n"
                    + "  \"sequences\": [],\n"
                    + "  \"table_types\": [],\n"
                    + "  \"functions\": [],\n"
                    + "  \"seed_data\": {\n"
                    + "    \"effective_schema\": {\n"
                    + "      \"effective_schema_singleton_id\": 1,\n"
                    + "      \"api_schema_format_version\": \"1.0.0\",\n"
                    + "      \"effective_schema_hash\": \"hash\",\n"
                    + "      \"resource_key_count\": 0,\n"
                    + "      \"resource_key_seed_hash\": \"00\"\n"
                    + "    },\n"
                    + "    \"schema_components\": [],\n"
                    + "    \"resource_keys\": []\n"
                    + "  }\n"
                    + "}\n"
            );
    }
}
