// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Focused_Stable_Key_Positive_Fixture_For_Extension_Child_Collections
{
    private const string FixturePath =
        "Fixtures/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private IReadOnlyDictionary<string, DbTableModel> _tablesByScope = null!;

    [SetUp]
    public void Setup()
    {
        var modelSet = FocusedStableKeyFixtureDerivedModelSetBuilder.Build(FixturePath, SqlDialect.Pgsql);

        var schoolModel = modelSet.ConcreteResourcesInNameOrder.Single(resource =>
            resource.ResourceKey.Resource == _schoolResource
        );

        _tablesByScope = schoolModel.RelationalModel.TablesInDependencyOrder.ToDictionary(table =>
            table.JsonScope.Canonical
        );
    }

    [Test]
    public void It_should_derive_stable_identity_for_nested_core_collections_and_collection_aligned_extension_scopes()
    {
        var addressTable = RequireTable("$.addresses[*]");
        var periodTable = RequireTable("$.addresses[*].periods[*]");
        var alignedExtensionScope = RequireTable("$._ext.sample.addresses[*]._ext.sample");

        addressTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.Collection);
        addressTable
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(static column => column.Value)
            .Should()
            .Equal("CollectionItemId");
        addressTable
            .IdentityMetadata.RootScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("School_DocumentId");
        addressTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("School_DocumentId");
        addressTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding => binding.ColumnName.Value)
            .Should()
            .Equal("City");

        periodTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.Collection);
        periodTable
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(static column => column.Value)
            .Should()
            .Equal("CollectionItemId");
        periodTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("ParentCollectionItemId");
        periodTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding => binding.ColumnName.Value)
            .Should()
            .Equal("PeriodName");

        alignedExtensionScope.IdentityMetadata.TableKind.Should().Be(DbTableKind.CollectionExtensionScope);
        alignedExtensionScope
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(static column => column.Value)
            .Should()
            .Equal("BaseCollectionItemId");
        alignedExtensionScope
            .IdentityMetadata.RootScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("School_DocumentId");
        alignedExtensionScope
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("BaseCollectionItemId");
    }

    [Test]
    public void It_should_derive_stable_identity_for_root_level_extension_child_collections()
    {
        var interventionTable = RequireTable("$._ext.sample.interventions[*]");

        interventionTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.ExtensionCollection);
        interventionTable
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(static column => column.Value)
            .Should()
            .Equal("CollectionItemId");
        interventionTable
            .IdentityMetadata.RootScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("School_DocumentId");
        interventionTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("School_DocumentId");
        interventionTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding =>
                binding.RelativePath.Canonical
            )
            .Should()
            .Equal("$.interventionCode");
        interventionTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding => binding.ColumnName.Value)
            .Should()
            .Equal("InterventionCode");
    }

    [Test]
    public void It_should_derive_stable_identity_for_nested_extension_child_collections()
    {
        var visitTable = RequireTable("$._ext.sample.interventions[*].visits[*]");

        visitTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.ExtensionCollection);
        visitTable
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(static column => column.Value)
            .Should()
            .Equal("CollectionItemId");
        visitTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("ParentCollectionItemId");
        visitTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding =>
                binding.RelativePath.Canonical
            )
            .Should()
            .Equal("$.visitCode");
        visitTable
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding => binding.ColumnName.Value)
            .Should()
            .Equal("VisitCode");
    }

    [Test]
    public void It_should_derive_stable_identity_for_collection_aligned_extension_child_collections()
    {
        var sponsorReferenceTable = RequireTable(
            "$._ext.sample.addresses[*]._ext.sample.sponsorReferences[*]"
        );

        sponsorReferenceTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.ExtensionCollection);
        sponsorReferenceTable
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(static column => column.Value)
            .Should()
            .Equal("CollectionItemId");
        sponsorReferenceTable
            .IdentityMetadata.RootScopeLocatorColumns.Select(static column => column.Value)
            .Should()
            .Equal("School_DocumentId");
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
            .Equal("Program_DocumentId");
    }

    private DbTableModel RequireTable(string scope)
    {
        _tablesByScope.Should().ContainKey(scope);
        return _tablesByScope[scope];
    }
}

[TestFixture]
public class Given_A_Focused_Stable_Key_Negative_Fixture_Without_Semantic_Identity
{
    private const string FixturePath =
        "Fixtures/focused-stable-key/negative/missing-semantic-identity/fixture.manifest.json";

    private Action _build = null!;

    [SetUp]
    public void Setup()
    {
        _build = () => FocusedStableKeyFixtureDerivedModelSetBuilder.Build(FixturePath, SqlDialect.Pgsql);
    }

    [Test]
    public void It_should_fail_with_the_missing_semantic_identity_diagnostic()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("Persisted multi-item scope");
        exception.Message.Should().Contain("$.addresses[*]");
        exception.Message.Should().Contain("Ed-Fi:School");
        exception.Message.Should().Contain("arrayUniquenessConstraints");
    }
}
