// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for relational name overrides.
/// </summary>
[TestFixture]
public class Given_Relational_NameOverrides
{
    private RelationalResourceModel _model = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            "hand-authored-name-overrides-api-schema.json"
        );
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derived = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _model = derived
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "Person"
            )
            .RelationalModel;
    }

    /// <summary>
    /// It should apply scalar overrides for inlined object paths.
    /// </summary>
    [Test]
    public void It_should_apply_scalar_overrides_for_inlined_object_paths()
    {
        var columnNames = _model.Root.Columns.Select(column => column.ColumnName.Value).ToArray();

        columnNames.Should().Contain("StreetName");
    }

    /// <summary>
    /// It should apply reference overrides to derived reference columns.
    /// </summary>
    [Test]
    public void It_should_apply_reference_overrides_to_reference_columns()
    {
        var columnNames = _model.Root.Columns.Select(column => column.ColumnName.Value).ToArray();

        columnNames.Should().Contain("Campus_DocumentId");
        columnNames.Should().Contain("Campus_SchoolId");
    }

    /// <summary>
    /// It should apply collection overrides to table and parent ordinal names.
    /// </summary>
    [Test]
    public void It_should_apply_collection_overrides_to_table_and_parent_ordinal_names()
    {
        var tableNames = _model.TablesInDependencyOrder.Select(table => table.Table.Name).ToArray();

        tableNames.Should().Contain("PersonSite");
        tableNames.Should().Contain("PersonSiteWindow");

        var nestedTable = _model.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "PersonSiteWindow"
        );
        var keyColumns = nestedTable.Key.Columns.Select(column => column.ColumnName.Value).ToArray();

        keyColumns.Should().Contain("SiteOrdinal");
    }
}

/// <summary>
/// Test fixture for missing descendant collection overrides.
/// </summary>
[TestFixture]
public class Given_A_Collection_NameOverride_With_Missing_Descendant_Override
{
    private RelationalResourceModel _model = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            "hand-authored-name-override-descendant-missing-api-schema.json"
        );
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derived = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _model = derived
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "Person"
            )
            .RelationalModel;
    }

    /// <summary>
    /// It should build successfully when a descendant collection override is missing.
    /// </summary>
    [Test]
    public void It_should_build_when_descendant_override_is_missing()
    {
        var tableNames = _model.TablesInDependencyOrder.Select(table => table.Table.Name).ToArray();

        tableNames.Should().Contain("PersonSite");
        tableNames.Should().Contain("PersonSitePeriod");

        var nestedTable = _model.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "PersonSitePeriod"
        );
        var keyColumns = nestedTable.Key.Columns.Select(column => column.ColumnName.Value).ToArray();

        keyColumns.Should().Contain("SiteOrdinal");
    }
}

/// <summary>
/// Test fixture for nested collection overrides without parent prefixes.
/// </summary>
[TestFixture]
public class Given_A_Nested_Collection_NameOverride_Without_Parent_Prefix
{
    private DbTableModel _segmentTable = default!;
    private DbTableModel _qualifiedTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        var segmentSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            "hand-authored-name-override-nested-segment-api-schema.json"
        );
        var segmentDerived = builder.Build(segmentSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        var segmentModel = segmentDerived
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "Person"
            )
            .RelationalModel;

        var qualifiedSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            "hand-authored-name-overrides-api-schema.json"
        );
        var qualifiedDerived = builder.Build(qualifiedSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        var qualifiedModel = qualifiedDerived
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "Person"
            )
            .RelationalModel;

        _segmentTable = segmentModel.TablesInDependencyOrder.Single(table =>
            table.JsonScope.Canonical == "$.addresses[*].periods[*]"
        );
        _qualifiedTable = qualifiedModel.TablesInDependencyOrder.Single(table =>
            table.JsonScope.Canonical == "$.addresses[*].periods[*]"
        );
    }

    /// <summary>
    /// It should resolve segment-only and fully-qualified overrides consistently.
    /// </summary>
    [Test]
    public void It_should_resolve_segment_only_and_qualified_overrides_consistently()
    {
        _segmentTable.Table.Name.Should().Be("PersonSiteWindow");
        _qualifiedTable.Table.Name.Should().Be("PersonSiteWindow");
    }
}

/// <summary>
/// Test fixture for unknown relational name override keys.
/// </summary>
[TestFixture]
public class Given_A_Relational_NameOverride_With_Unknown_Key
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            "hand-authored-name-override-unknown-key-api-schema.json"
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
    /// It should fail fast with diagnostics that include raw and canonical keys.
    /// </summary>
    [Test]
    public void It_should_fail_fast_with_raw_and_canonical_keys()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("did not match any derived columns or collection scopes");
        _exception.Message.Should().Contain("$.unknownProperty");
        _exception.Message.Should().Contain("canonical");
        _exception.Message.Should().Contain("Ed-Fi:Person");
    }
}

/// <summary>
/// Test fixture for extension resource name overrides.
/// </summary>
[TestFixture]
public class Given_An_Extension_NameOverride_With_A_Relative_Key
{
    private RelationalResourceModel _model = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixtures(
            new (string FileName, bool IsExtensionProject)[]
            {
                ("hand-authored-name-override-extension-core-api-schema.json", false),
                ("hand-authored-name-override-extension-project-api-schema.json", true),
            }
        );
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derived = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _model = derived
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && resource.ResourceKey.Resource.ResourceName == "School"
            )
            .RelationalModel;
    }

    /// <summary>
    /// It should apply the extension override to the extension table column.
    /// </summary>
    [Test]
    public void It_should_apply_the_extension_override_to_extension_columns()
    {
        var extensionTable = _model.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "SchoolExtension"
        );
        var columnNames = extensionTable.Columns.Select(column => column.ColumnName.Value).ToArray();

        columnNames.Should().Contain("ExtensionFieldOverride");
    }
}

/// <summary>
/// Test fixture for base and extension override conflicts.
/// </summary>
[TestFixture]
public class Given_Base_And_Extension_NameOverrides_On_The_Same_Path
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixtures(
            new (string FileName, bool IsExtensionProject)[]
            {
                ("hand-authored-name-override-extension-core-conflict-api-schema.json", false),
                ("hand-authored-name-override-extension-project-api-schema.json", true),
            }
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
    /// It should fail fast when base and extension overrides collide.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_base_extension_override_collisions()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("target the same derived element");
        _exception.Message.Should().Contain("$._ext.sample.extensionField");
        _exception.Message.Should().Contain("Sample:School");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

/// <summary>
/// Test fixture for deterministic collisions across resource ordering.
/// </summary>
[TestFixture]
public class Given_A_Root_Table_NameOverride_Collision_With_Reversed_Resource_Order
{
    private Exception? _exception;
    private Exception? _reverseOrderException;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _exception = BuildCollisionException(reverseResourceOrder: false);
        _reverseOrderException = BuildCollisionException(reverseResourceOrder: true);
    }

    /// <summary>
    /// It should fail fast for the baseline resource ordering.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_the_baseline_resource_ordering()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier shortening collisions detected");
        _exception.Message.Should().Contain("table edfi.Person");
        _exception.Message.Should().Contain("resource 'Ed-Fi:Student'");
        _exception.Message.Should().Contain("resource 'Ed-Fi:Staff'");
    }

    /// <summary>
    /// It should report an identical collision message when resource order is reversed.
    /// </summary>
    [Test]
    public void It_should_report_the_same_message_when_resource_order_is_reversed()
    {
        _reverseOrderException.Should().BeOfType<InvalidOperationException>();
        _reverseOrderException!.Message.Should().Be(_exception!.Message);
    }

    private static Exception? BuildCollisionException(bool reverseResourceOrder)
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            "hand-authored-root-override-collision-api-schema.json",
            reverseResourceOrder: reverseResourceOrder
        );
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            return exception;
        }

        return null;
    }
}
