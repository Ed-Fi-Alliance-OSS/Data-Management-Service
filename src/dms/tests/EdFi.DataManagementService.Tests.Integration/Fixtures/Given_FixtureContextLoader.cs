// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Fixtures;

public sealed class Given_FixtureContextLoader
{
    [Test]
    public void It_materializes_authoritative_ds52_tpdm_with_survey_response_resources()
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.AuthoritativeDs52Tpdm);

        File.Exists(Path.Combine(fixture.ApiSchemaDirectory, "fixture.json")).Should().BeTrue();
        fixture
            .ProfileXmlDirectory.Should()
            .EndWith(Path.Combine("Fixtures", "Profiles", nameof(FixtureKey.AuthoritativeDs52Tpdm)));
        fixture
            .Resources.Should()
            .Contain([
                new("Ed-Fi", "SurveyResponse"),
                new("TPDM", "SurveyResponse"),
                new("Ed-Fi", "Survey"),
                new("Ed-Fi", "Contact"),
                new("Ed-Fi", "Staff"),
                new("Ed-Fi", "Student"),
                new("Ed-Fi", "School"),
                new("Ed-Fi", "Session"),
                new("Ed-Fi", "SchoolYearType"),
            ]);
    }

    [Test]
    public void It_writes_a_runtime_bootstrap_manifest_for_materialized_schema_files()
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.AuthoritativeDs52Tpdm);

        JsonObject manifest = ReadJsonObject(
            Path.Combine(fixture.ApiSchemaDirectory, "bootstrap-api-schema-manifest.json")
        );

        manifest["version"]?.GetValue<int>().Should().Be(1);
        JsonArray projects = manifest["projects"]?.AsArray() ?? [];
        projects
            .Select(project => new
            {
                ProjectName = project?["projectName"]?.GetValue<string>(),
                ProjectEndpointName = project?["projectEndpointName"]?.GetValue<string>(),
                IsExtensionProject = project?["isExtensionProject"]?.GetValue<bool>(),
                SchemaPath = project?["schemaPath"]?.GetValue<string>(),
            })
            .Should()
            .BeEquivalentTo(
                [
                    new
                    {
                        ProjectName = "Ed-Fi",
                        ProjectEndpointName = "ed-fi",
                        IsExtensionProject = false,
                        SchemaPath = "inputs/ds-5.2-api-schema-authoritative.json",
                    },
                    new
                    {
                        ProjectName = "TPDM",
                        ProjectEndpointName = "tpdm",
                        IsExtensionProject = true,
                        SchemaPath = "inputs/tpdm-api-schema-authoritative.json",
                    },
                ],
                options => options.WithStrictOrdering()
            );

        foreach (JsonNode? project in projects)
        {
            string schemaPath = project?["schemaPath"]?.GetValue<string>() ?? string.Empty;
            File.Exists(Path.Combine(fixture.ApiSchemaDirectory, schemaPath)).Should().BeTrue();
        }
    }

    [Test]
    public void It_preserves_the_baseline_fixture_manifest_contract()
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.AuthoritativeDs52Tpdm);

        JsonObject manifest = ReadJsonObject(Path.Combine(fixture.ApiSchemaDirectory, "fixture.json"));

        manifest["apiSchemaFiles"]
            ?.AsArray()
            .Select(file => file?.GetValue<string>())
            .Should()
            .Equal("ds-5.2-api-schema-authoritative.json", "tpdm-api-schema-authoritative.json");

        manifest["dialects"]
            ?.AsArray()
            .Select(dialect => dialect?.GetValue<string>())
            .Should()
            .Equal("pgsql", "mssql");
    }

    private static JsonObject ReadJsonObject(string path)
    {
        JsonNode node =
            JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"JSON document at '{path}' parsed to null.");

        return node.AsObject();
    }
}
