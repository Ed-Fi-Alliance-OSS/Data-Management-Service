// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Descriptor_Only_Model_Set_When_Emitting_Manifest
{
    private string _manifest = default!;

    [SetUp]
    public void Setup()
    {
        var projectSchema = CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derivedSet = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _manifest = DerivedModelSetManifestEmitter.Emit(derivedSet);
    }

    [Test]
    public void It_should_produce_deterministic_output()
    {
        var projectSchema = CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derivedSet = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var firstEmission = DerivedModelSetManifestEmitter.Emit(derivedSet);
        var secondEmission = DerivedModelSetManifestEmitter.Emit(derivedSet);

        secondEmission.Should().Be(firstEmission);
    }

    [Test]
    public void It_should_use_unix_line_endings()
    {
        _manifest.Should().NotContain("\r");
        _manifest.Should().Contain("\n");
    }

    [Test]
    public void It_should_end_with_a_trailing_newline()
    {
        _manifest.Should().EndWith("\n");
    }

    [Test]
    public void It_should_not_contain_trailing_whitespace_on_any_line()
    {
        foreach (var line in _manifest.Split('\n'))
        {
            if (line.Length > 0)
            {
                line.Should().NotMatchRegex(@"\s$", "no trailing whitespace expected");
            }
        }
    }

    [Test]
    public void It_should_include_dialect()
    {
        _manifest.Should().Contain("\"dialect\": \"Pgsql\"");
    }

    [Test]
    public void It_should_include_projects_section()
    {
        _manifest.Should().Contain("\"projects\":");
        _manifest.Should().Contain("\"project_name\": \"Ed-Fi\"");
    }

    [Test]
    public void It_should_include_resources_section()
    {
        _manifest.Should().Contain("\"resources\":");
        _manifest.Should().Contain("\"resource_name\": \"GradeLevelDescriptor\"");
        _manifest.Should().Contain("\"storage_kind\": \"SharedDescriptorTable\"");
    }

    [Test]
    public void It_should_include_abstract_identity_tables_section()
    {
        _manifest.Should().Contain("\"abstract_identity_tables\":");
    }

    [Test]
    public void It_should_include_abstract_union_views_section()
    {
        _manifest.Should().Contain("\"abstract_union_views\":");
    }

    [Test]
    public void It_should_include_indexes_section()
    {
        _manifest.Should().Contain("\"indexes\":");
    }

    [Test]
    public void It_should_include_triggers_section()
    {
        _manifest.Should().Contain("\"triggers\":");
    }

    [Test]
    public void It_should_not_include_resource_details_when_not_requested()
    {
        _manifest.Should().NotContain("\"resource_details\":");
    }
}

[TestFixture]
public class Given_A_Contact_Model_Set_With_Extension_When_Emitting_Manifest
{
    private string _manifest = default!;

    [SetUp]
    public void Setup()
    {
        var coreSchema = CommonInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema();
        var extensionSchema = CommonInventoryTestSchemaBuilder.BuildExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derivedSet = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _manifest = DerivedModelSetManifestEmitter.Emit(derivedSet);
    }

    [Test]
    public void It_should_include_both_projects()
    {
        _manifest.Should().Contain("\"project_name\": \"Ed-Fi\"");
        _manifest.Should().Contain("\"project_name\": \"Sample\"");
    }

    [Test]
    public void It_should_include_contact_resource()
    {
        _manifest.Should().Contain("\"resource_name\": \"Contact\"");
        _manifest.Should().Contain("\"storage_kind\": \"RelationalTables\"");
    }

    [Test]
    public void It_should_include_indexes_for_relational_tables()
    {
        _manifest.Should().Contain("\"indexes\":");
    }

    [Test]
    public void It_should_include_triggers_for_relational_tables()
    {
        _manifest.Should().Contain("\"triggers\":");
    }
}
