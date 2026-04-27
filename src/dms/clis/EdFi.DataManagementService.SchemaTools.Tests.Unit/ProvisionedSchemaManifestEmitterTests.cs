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
