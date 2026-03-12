// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_RuntimePlanFixtureModelSetBuilder_MultiProjectFixture(SqlDialect dialect)
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/multi-project-builder/fixture.manifest.json";
    private DerivedRelationalModelSet _modelSet = null!;
    private DerivedRelationalModelSet _modelSetWithReversedFixtureInputOrder = null!;

    [SetUp]
    public void Setup()
    {
        _modelSet = RuntimePlanFixtureModelSetBuilder.Build(
            FixturePath,
            dialect,
            reverseResourceSchemaOrder: false,
            reverseFixtureInputOrder: false
        );
        _modelSetWithReversedFixtureInputOrder = RuntimePlanFixtureModelSetBuilder.Build(
            FixturePath,
            dialect,
            reverseResourceSchemaOrder: false,
            reverseFixtureInputOrder: true
        );
    }

    [Test]
    public void It_should_load_fixture_projects_from_manifest_inputs()
    {
        _modelSet
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Select(component =>
                component.ProjectEndpointName
            )
            .Should()
            .Equal("ed-fi", "sample");
        _modelSet
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Single(component =>
                component.ProjectEndpointName == "ed-fi"
            )
            .IsExtensionProject.Should()
            .BeFalse();
        _modelSet
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Single(component =>
                component.ProjectEndpointName == "sample"
            )
            .IsExtensionProject.Should()
            .BeTrue();
        _modelSet.EffectiveSchema.ResourceKeysInIdOrder.Should().HaveCount(2);
    }

    [Test]
    public void It_should_derive_resources_for_all_manifest_inputs()
    {
        var resources = _modelSet
            .ConcreteResourcesInNameOrder.Select(resource => resource.ResourceKey.Resource)
            .ToArray();

        resources.Should().Contain(new QualifiedResourceName("Ed-Fi", "School"));
        resources.Should().Contain(new QualifiedResourceName("Sample", "Section"));
    }

    [Test]
    public void It_should_be_deterministic_when_fixture_input_order_is_reversed()
    {
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.EffectiveSchemaHash.Should()
            .Be(_modelSet.EffectiveSchema.EffectiveSchemaHash);
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.ResourceKeyCount.Should()
            .Be(_modelSet.EffectiveSchema.ResourceKeyCount);
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.ResourceKeySeedHash.Should()
            .Equal(_modelSet.EffectiveSchema.ResourceKeySeedHash);
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Select(component =>
                $"{component.ProjectEndpointName}|{component.ProjectName}|{component.ProjectVersion}|{component.IsExtensionProject}|{component.ProjectHash}"
            )
            .Should()
            .Equal(
                _modelSet.EffectiveSchema.SchemaComponentsInEndpointOrder.Select(component =>
                    $"{component.ProjectEndpointName}|{component.ProjectName}|{component.ProjectVersion}|{component.IsExtensionProject}|{component.ProjectHash}"
                )
            );
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.ResourceKeysInIdOrder.Select(resourceKey =>
                $"{resourceKey.ResourceKeyId}|{resourceKey.Resource.ProjectName}|{resourceKey.Resource.ResourceName}|{resourceKey.ResourceVersion}|{resourceKey.IsAbstractResource}"
            )
            .Should()
            .Equal(
                _modelSet.EffectiveSchema.ResourceKeysInIdOrder.Select(resourceKey =>
                    $"{resourceKey.ResourceKeyId}|{resourceKey.Resource.ProjectName}|{resourceKey.Resource.ResourceName}|{resourceKey.ResourceVersion}|{resourceKey.IsAbstractResource}"
                )
            );
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_RuntimePlanFixtureModelSetBuilder_CollectionsNestedExtensionFixture(SqlDialect dialect)
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/collections-nested-extension/fixture.manifest.json";
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private string[] _resourceKeys = null!;
    private IReadOnlyDictionary<string, DbTableModel> _tablesByScope = null!;

    [SetUp]
    public void Setup()
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(
            FixturePath,
            dialect,
            reverseResourceSchemaOrder: false,
            reverseFixtureInputOrder: false
        );

        _resourceKeys = modelSet
            .EffectiveSchema.ResourceKeysInIdOrder.Select(resourceKey =>
                $"{resourceKey.Resource.ProjectName}:{resourceKey.Resource.ResourceName}"
            )
            .ToArray();

        var resource = modelSet.ConcreteResourcesInNameOrder.Single(resource =>
            resource.ResourceKey.Resource == _schoolResource
        );
        _tablesByScope = resource.RelationalModel.TablesInDependencyOrder.ToDictionary(table =>
            table.JsonScope.Canonical
        );
    }

    [Test]
    public void It_should_exclude_resource_extension_entries_from_resource_keys()
    {
        _resourceKeys.Should().Contain("Ed-Fi:School");
        _resourceKeys.Should().NotContain("Sample:School");
    }

    [Test]
    public void It_should_derive_extension_tables_for_root_and_nested_collection_scopes()
    {
        _tablesByScope.Should().ContainKey("$");
        _tablesByScope.Should().ContainKey("$.addresses[*]");
        _tablesByScope.Should().ContainKey("$.addresses[*].periods[*]");
        _tablesByScope.Should().ContainKey("$._ext.sample");
        _tablesByScope.Should().ContainKey("$._ext.sample.addresses[*]._ext.sample");
        _tablesByScope.Should().ContainKey("$._ext.sample.addresses[*].periods[*]._ext.sample");
    }

    [Test]
    public void It_should_align_extension_table_keys_to_base_table_scopes()
    {
        AssertScopeKeyAlignment("$", "$._ext.sample");
        AssertScopeKeyAlignment("$.addresses[*]", "$._ext.sample.addresses[*]._ext.sample");
        AssertScopeKeyAlignment(
            "$.addresses[*].periods[*]",
            "$._ext.sample.addresses[*].periods[*]._ext.sample"
        );
    }

    [Test]
    public void It_should_map_extension_scalar_columns_to_expected_source_paths()
    {
        AssertExtensionScalarPath("$._ext.sample", "$._ext.sample.campusCode");
        AssertExtensionScalarPath(
            "$._ext.sample.addresses[*]._ext.sample",
            "$._ext.sample.addresses[*]._ext.sample.zone"
        );
        AssertExtensionScalarPath(
            "$._ext.sample.addresses[*].periods[*]._ext.sample",
            "$._ext.sample.addresses[*].periods[*]._ext.sample.track"
        );
    }

    private void AssertScopeKeyAlignment(string baseScope, string extensionScope)
    {
        _tablesByScope.Should().ContainKey(baseScope);
        _tablesByScope.Should().ContainKey(extensionScope);
        var baseTable = _tablesByScope[baseScope];
        var extensionTable = _tablesByScope[extensionScope];
        var baseKeyColumns = baseTable
            .Key.Columns.Select(column => $"{column.ColumnName.Value}:{column.Kind}")
            .ToArray();
        var extensionKeyColumns = extensionTable
            .Key.Columns.Select(column => $"{column.ColumnName.Value}:{column.Kind}")
            .ToArray();

        extensionKeyColumns.Should().Equal(baseKeyColumns);
    }

    private void AssertExtensionScalarPath(string extensionScope, string expectedSourcePath)
    {
        _tablesByScope.Should().ContainKey(extensionScope);

        var scalarPaths = _tablesByScope[extensionScope]
            .Columns.Where(column =>
                column.Storage is ColumnStorage.Stored && column.SourceJsonPath is not null
            )
            .Select(column => column.SourceJsonPath?.Canonical)
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();

        scalarPaths.Should().Contain(expectedSourcePath);
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_RuntimePlanFixtureModelSetBuilder_KeyUnificationPresenceGatingFixture(SqlDialect dialect)
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/key-unification-presence-gating/fixture.manifest.json";
    private static readonly QualifiedResourceName _presenceGateResource = new("Ed-Fi", "PresenceGateExample");
    private DbTableModel _rootTable = null!;

    [SetUp]
    public void Setup()
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(
            FixturePath,
            dialect,
            reverseResourceSchemaOrder: false,
            reverseFixtureInputOrder: false
        );
        _rootTable = modelSet
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource == _presenceGateResource
            )
            .RelationalModel.Root;
    }

    [Test]
    public void It_should_derive_source_less_canonical_storage_column_for_descriptor_unification()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;
        var canonicalColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Equals(keyUnificationClass.CanonicalColumn)
        );

        canonicalColumn.SourceJsonPath.Should().BeNull();
        canonicalColumn.Storage.Should().BeOfType<ColumnStorage.Stored>();
    }

    [Test]
    public void It_should_derive_synthetic_presence_columns_and_null_or_true_constraints()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;
        var presenceColumns = keyUnificationClass
            .MemberPathColumns.Select(ResolveSyntheticPresenceColumn)
            .ToArray();
        var expectedPresenceColumnNames = presenceColumns
            .Select(column => column.ColumnName.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var nullOrTrueColumns = _rootTable
            .Constraints.OfType<TableConstraint.NullOrTrue>()
            .Select(constraint => constraint.Column.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        expectedPresenceColumnNames.Should().HaveCount(2);
        nullOrTrueColumns.Should().Equal(expectedPresenceColumnNames);

        foreach (var presenceColumn in presenceColumns)
        {
            presenceColumn.Kind.Should().Be(ColumnKind.Scalar);
            presenceColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Boolean));
            presenceColumn.IsNullable.Should().BeTrue();
            presenceColumn.SourceJsonPath.Should().BeNull();
            presenceColumn.Storage.Should().BeOfType<ColumnStorage.Stored>();
        }
    }

    private DbColumnModel ResolveSyntheticPresenceColumn(DbColumnName memberPathColumnName)
    {
        var memberPathColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Equals(memberPathColumnName)
        );
        var aliasStorage = memberPathColumn.Storage.Should().BeOfType<ColumnStorage.UnifiedAlias>().Subject;

        if (aliasStorage.PresenceColumn is not DbColumnName presenceColumnName)
        {
            throw new AssertionException(
                $"Expected '{memberPathColumnName.Value}' to have a synthetic presence column."
            );
        }

        return _rootTable.Columns.Single(column => column.ColumnName.Equals(presenceColumnName));
    }
}
