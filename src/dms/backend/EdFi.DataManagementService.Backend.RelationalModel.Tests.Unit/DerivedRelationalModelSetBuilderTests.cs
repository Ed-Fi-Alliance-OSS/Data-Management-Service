// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for a multi project effective schema set with unordered inputs.
/// </summary>
[TestFixture]
public class Given_A_MultiProject_EffectiveSchemaSet_With_Unordered_Inputs
{
    private DerivedRelationalModelSet _orderedResult = default!;
    private DerivedRelationalModelSet _unorderedResult = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var orderedEffectiveSchemaSet =
            EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var unorderedEffectiveSchemaSet =
            EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet(
                reverseProjectOrder: true,
                reverseResourceOrder: true
            );

        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        _orderedResult = builder.Build(orderedEffectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _unorderedResult = builder.Build(
            unorderedEffectiveSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );
    }

    /// <summary>
    /// It should produce consistent project ordering.
    /// </summary>
    [Test]
    public void It_should_produce_consistent_project_ordering()
    {
        var orderedProjects = _orderedResult
            .ProjectSchemasInEndpointOrder.Select(project => project.ProjectEndpointName)
            .ToArray();
        var unorderedProjects = _unorderedResult
            .ProjectSchemasInEndpointOrder.Select(project => project.ProjectEndpointName)
            .ToArray();

        orderedProjects.Should().Equal(unorderedProjects);
        orderedProjects.Should().Equal("ed-fi", "sample");
    }

    /// <summary>
    /// It should produce consistent resource ordering.
    /// </summary>
    [Test]
    public void It_should_produce_consistent_resource_ordering()
    {
        var orderedResources = _orderedResult
            .ConcreteResourcesInNameOrder.Select(resource => resource.ResourceKey.Resource)
            .ToArray();
        var unorderedResources = _unorderedResult
            .ConcreteResourcesInNameOrder.Select(resource => resource.ResourceKey.Resource)
            .ToArray();

        orderedResources.Should().Equal(unorderedResources);
        orderedResources
            .Should()
            .Equal(
                new QualifiedResourceName("Ed-Fi", "School"),
                new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"),
                new QualifiedResourceName("Sample", "ExtensionDescriptor"),
                new QualifiedResourceName("Sample", "Section")
            );
    }
}

/// <summary>
/// Test fixture for an effective schema set with a physical schema collision.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_A_Physical_Schema_Collision
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));

        var projects = new[]
        {
            new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, projectSchema),
            new EffectiveProjectSchema("edfi", "EdFi", "5.0.0", false, projectSchema),
        };

        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(projects);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with a physical schema name collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_a_physical_schema_name_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("physical schema");
        _exception.Message.Should().Contain("ed-fi");
        _exception.Message.Should().Contain("edfi");
    }
}

/// <summary>
/// Test fixture for key-unification defaults on schemas with no unification classes.
/// </summary>
[TestFixture]
public class Given_A_Derived_Relational_Model_Set_Without_Key_Unification
{
    private DerivedRelationalModelSet _derived = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        _derived = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should default all columns to stored storage metadata.
    /// </summary>
    [Test]
    public void It_should_default_all_columns_to_stored_storage_metadata()
    {
        var columns = EnumerateTables(_derived).SelectMany(table => table.Columns).ToArray();

        columns.Should().NotBeEmpty();
        columns.Should().OnlyContain(column => column.Storage is ColumnStorage.Stored);
    }

    /// <summary>
    /// It should default all tables to empty key-unification class inventories.
    /// </summary>
    [Test]
    public void It_should_default_all_tables_to_empty_key_unification_class_inventories()
    {
        var tables = EnumerateTables(_derived).ToArray();

        tables.Should().NotBeEmpty();
        tables.Should().OnlyContain(table => table.KeyUnificationClasses.Count == 0);
    }

    /// <summary>
    /// Enumerates all table models in the derived set.
    /// </summary>
    private static IEnumerable<DbTableModel> EnumerateTables(DerivedRelationalModelSet derived)
    {
        ArgumentNullException.ThrowIfNull(derived);

        return derived
            .ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.TablesInDependencyOrder
            )
            .Concat(derived.AbstractIdentityTablesInNameOrder.Select(table => table.TableModel));
    }
}
