// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
public class Given_CdcProviderManifest_For_Ordinary_Ddl_Emit
{
    private int _exitCode;
    private string _outputDir = null!;

    [SetUp]
    public void SetUp()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"api-schema-tools-cdc-manifest-{Guid.NewGuid():N}");
        var fixturePath = CliTestHelper.GetMinimalSchemaPath();

        (_exitCode, _, _) = CliTestHelper.RunCli(
            "ddl",
            "emit",
            "--schema",
            fixturePath,
            "--output",
            _outputDir,
            "--dialect",
            "both",
            "--ddl-manifest"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_outputDir))
        {
            Directory.Delete(_outputDir, recursive: true);
        }
    }

    [Test]
    public void It_should_not_emit_standalone_cdc_provider_manifests_from_ordinary_ddl()
    {
        _exitCode.Should().Be(0);
        File.Exists(Path.Combine(_outputDir, "cdc-provider.pgsql.manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(_outputDir, "cdc-provider.mssql.manifest.json")).Should().BeFalse();
    }

    [Test]
    public void It_should_keep_cdc_provider_metadata_out_of_ordinary_manifests()
    {
        var ordinaryManifestNames = new[]
        {
            "ddl.manifest.json",
            "effective-schema.manifest.json",
            "relational-model.pgsql.manifest.json",
            "relational-model.mssql.manifest.json",
        };

        foreach (var manifestName in ordinaryManifestNames)
        {
            var manifestJson = File.ReadAllText(Path.Combine(_outputDir, manifestName));

            manifestJson.Should().NotContain("cdc_provider");
            manifestJson.Should().NotContain("provider_artifacts");
            manifestJson.Should().NotContain("heartbeat_action_query");
            manifestJson.Should().NotContain("observed_source_fingerprint");
        }
    }
}
