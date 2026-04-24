// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_RuntimePlanFixtureModelSetBuilder_FocusedStableKeyExtensionChildCollectionsFixture(
    SqlDialect dialect
)
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private IReadOnlyDictionary<string, DbTableModel> _tablesByScope = null!;

    [SetUp]
    public void Setup()
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(FixturePath, dialect);
        var schoolModel = modelSet.ConcreteResourcesInNameOrder.Single(resource =>
            resource.ResourceKey.Resource == _schoolResource
        );

        _tablesByScope = schoolModel.RelationalModel.TablesInDependencyOrder.ToDictionary(table =>
            table.JsonScope.Canonical
        );
    }

    [Test]
    public void It_should_load_positive_fixtures_covering_nested_collections_and_collection_aligned_extension_scopes()
    {
        var addressTable = RequireTable("$.addresses[*]");
        var periodTable = RequireTable("$.addresses[*].periods[*]");
        var alignedExtensionScope = RequireTable("$._ext.sample.addresses[*]._ext.sample");

        addressTable
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(static column => column.Value)
            .Should()
            .Equal("CollectionItemId");
        addressTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("School_DocumentId");
        periodTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("ParentCollectionItemId");

        alignedExtensionScope.IdentityMetadata.TableKind.Should().Be(DbTableKind.CollectionExtensionScope);
        alignedExtensionScope
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(static column => column.Value)
            .Should()
            .Equal("BaseCollectionItemId");
    }

    [Test]
    public void It_should_load_positive_fixtures_covering_root_level_and_nested_extension_child_collections()
    {
        var interventionTable = RequireTable("$._ext.sample.interventions[*]");
        var visitTable = RequireTable("$._ext.sample.interventions[*].visits[*]");

        interventionTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.ExtensionCollection);
        interventionTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("School_DocumentId");
        interventionTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding => binding.ColumnName.Value)
            .Should()
            .Equal("InterventionCode");

        visitTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.ExtensionCollection);
        visitTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("ParentCollectionItemId");
        visitTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding => binding.ColumnName.Value)
            .Should()
            .Equal("VisitCode");
    }

    [Test]
    public void It_should_load_positive_fixtures_covering_collection_aligned_extension_child_collections()
    {
        var sponsorReferenceTable = RequireTable(
            "$._ext.sample.addresses[*]._ext.sample.sponsorReferences[*]"
        );

        sponsorReferenceTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.ExtensionCollection);
        sponsorReferenceTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("BaseCollectionItemId");
        sponsorReferenceTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding =>
                binding.RelativePath.Canonical
            )
            .Should()
            .Equal("$.programReference.programName");
        sponsorReferenceTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding => binding.ColumnName.Value)
            .Should()
            .Equal("Program_ProgramName");
    }

    private DbTableModel RequireTable(string scope)
    {
        _tablesByScope.Should().ContainKey(scope);
        return _tablesByScope[scope];
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_RuntimePlanFixtureModelSetBuilder_FocusedStableKeyNegativeMissingSemanticIdentityFixture(
    SqlDialect dialect
)
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/focused-stable-key/negative/missing-semantic-identity/fixture.manifest.json";

    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private DerivedRelationalModelSet _modelSet = null!;

    [SetUp]
    public void Setup()
    {
        _modelSet = RuntimePlanFixtureModelSetBuilder.Build(FixturePath, dialect);
    }

    [Test]
    public void It_should_compile_on_the_default_permissive_pipeline()
    {
        var schoolModel = _modelSet.ConcreteResourcesInNameOrder.Single(resource =>
            resource.ResourceKey.Resource == _schoolResource
        );
        var addressTable = schoolModel.RelationalModel.TablesInDependencyOrder.Single(table =>
            table.JsonScope.Canonical == "$.addresses[*]"
        );

        addressTable.IdentityMetadata.SemanticIdentityBindings.Should().BeEmpty();
    }

    [Test]
    public void It_should_compile_a_permissive_collection_merge_plan_without_inventing_fallback_semantic_keys()
    {
        var mappingSet = new MappingSetCompiler().Compile(_modelSet);
        var schoolWritePlan = mappingSet.WritePlansByResource[_schoolResource];
        var addressPlan = schoolWritePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            tablePlan.TableModel.JsonScope.Canonical == "$.addresses[*]"
        );

        addressPlan.DeleteByParentSql.Should().BeNull();
        addressPlan.CollectionMergePlan.Should().NotBeNull();
        addressPlan.CollectionMergePlan!.SemanticIdentityBindings.Should().BeEmpty();
    }
}
