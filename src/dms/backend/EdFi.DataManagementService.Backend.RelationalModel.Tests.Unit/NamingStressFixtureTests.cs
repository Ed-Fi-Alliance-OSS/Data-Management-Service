// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for the naming stress hand-authored schema.
/// </summary>
[TestFixture]
public class Given_A_Naming_Stress_Fixture
{
    private DerivedRelationalModelSet _derived = default!;
    private RelationalResourceModel _personModel = default!;
    private RelationalResourceModel _longNameModel = default!;
    private PgsqlDialectRules _dialectRules = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _dialectRules = new PgsqlDialectRules();

        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixtures(
            new (string FileName, bool IsExtensionProject)[]
            {
                ("hand-authored-naming-stress-core-api-schema.json", false),
                ("hand-authored-naming-stress-extension-api-schema.json", true),
            }
        );
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        _derived = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, _dialectRules);

        _personModel = _derived
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && resource.ResourceKey.Resource.ResourceName == "Person"
            )
            .RelationalModel;

        _longNameModel = _derived
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && resource.ResourceKey.Resource.ResourceName == "LongNameResource"
            )
            .RelationalModel;
    }

    /// <summary>
    /// It should normalize schema names with leading digits.
    /// </summary>
    [Test]
    public void It_should_normalize_schema_names_with_leading_digits()
    {
        var schema = _derived.ProjectSchemasInEndpointOrder.Single(project =>
            project.ProjectEndpointName == "99-select"
        );

        schema.PhysicalSchema.Value.Should().Be("p99select");
    }

    /// <summary>
    /// It should apply scalar, reference, and array overrides with reference-identity overrides.
    /// </summary>
    [Test]
    public void It_should_apply_overrides_and_reference_identity_overrides()
    {
        var rootColumnNames = _personModel.Root.Columns.Select(column => column.ColumnName.Value).ToArray();

        rootColumnNames.Should().Contain("StreetName");
        rootColumnNames.Should().Contain("Campus_DocumentId");
        rootColumnNames.Should().Contain("Campus_Id");
        rootColumnNames.Should().Contain("Select");

        var binding = _personModel.DocumentReferenceBindings.Single(reference =>
            reference.ReferenceObjectPath.Canonical == "$.schoolReference"
        );
        binding.FkColumn.Value.Should().Be("Campus_DocumentId");
        binding.IdentityBindings.Select(identity => identity.Column.Value).Should().Contain("Campus_Id");

        var tableNames = _personModel.TablesInDependencyOrder.Select(table => table.Table.Name).ToArray();

        tableNames.Should().Contain("PersonSite");
        tableNames.Should().Contain("PersonSiteWindow");

        var nestedTable = _personModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "PersonSiteWindow"
        );
        var keyColumns = nestedTable.Key.Columns.Select(column => column.ColumnName.Value).ToArray();

        keyColumns.Should().Contain("SiteOrdinal");
    }

    /// <summary>
    /// It should apply extension overrides with relative keys.
    /// </summary>
    [Test]
    public void It_should_apply_extension_overrides_with_relative_keys()
    {
        var extensionTable = _personModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "PersonExtension"
        );
        var columnNames = extensionTable.Columns.Select(column => column.ColumnName.Value).ToArray();

        columnNames.Should().Contain("ExtensionFieldOverride");
    }

    /// <summary>
    /// It should shorten long table and column names.
    /// </summary>
    [Test]
    public void It_should_shorten_long_table_and_column_names()
    {
        const string rootOverride =
            "very_long_root_table_name_for_naming_stress_fixture_exceeds_postgres_limit_again";
        var rootBaseName = RelationalNameConventions.ToPascalCase(rootOverride);

        rootBaseName.Length.Should().BeGreaterThan(_dialectRules.MaxIdentifierLength);
        _dialectRules.ShortenIdentifier(rootBaseName).Should().Be(_longNameModel.Root.Table.Name);

        const string longPropertyName =
            "another_ridiculously_long_property_name_to_force_column_shortening_because_it_exceeds_the_postgresql_identifier_limit";
        var columnBaseName = RelationalNameConventions.ToPascalCase(longPropertyName);

        columnBaseName.Length.Should().BeGreaterThan(_dialectRules.MaxIdentifierLength);

        var expectedColumnName = _dialectRules.ShortenIdentifier(columnBaseName);
        _longNameModel
            .Root.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Contain(expectedColumnName);
    }
}

/// <summary>
/// Test fixture for naming stress override collisions.
/// </summary>
[TestFixture]
public class Given_A_Naming_Stress_Fixture_With_Override_Collisions
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            "hand-authored-naming-stress-collision-api-schema.json"
        );
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should report override collision diagnostics.
    /// </summary>
    [Test]
    public void It_should_report_override_collision_diagnostics()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier override collisions detected");
        _exception.Message.Should().Contain("AfterOverrideNormalization");
        _exception.Message.Should().Contain("column name collision");
        _exception.Message.Should().Contain("table 'edfi.CollisionTest'");
        _exception.Message.Should().Contain("$.firstName");
        _exception.Message.Should().Contain("$.lastName");
    }
}
