// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal static class SmallFixtureEffectiveSchemaSetLoader
{
    public static EffectiveSchemaSet Load(string fixtureName)
    {
        var fixtureDirectory = Path.Combine(
            FixtureTestHelper.FindProjectRoot(),
            "Fixtures",
            "small",
            fixtureName
        );
        var config = FixtureConfigReader.Read(fixtureDirectory);
        var inputsDirectory = Path.Combine(fixtureDirectory, "inputs");
        var builder = new EffectiveSchemaSetBuilder(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );

        return builder.Build(LoadSchemaNodes(config, inputsDirectory));
    }

    private static ApiSchemaDocumentNodes LoadSchemaNodes(FixtureConfig config, string inputsDirectory)
    {
        var corePath = Path.Combine(inputsDirectory, config.ApiSchemaFiles[0]);
        var coreNode =
            JsonNode.Parse(File.ReadAllText(corePath))
            ?? throw new InvalidOperationException($"Core schema parsed to null: {corePath}");

        var extensionNodes = config
            .ApiSchemaFiles.Skip(1)
            .Select(relativePath =>
            {
                var fullPath = Path.Combine(inputsDirectory, relativePath);

                return JsonNode.Parse(File.ReadAllText(fullPath))
                    ?? throw new InvalidOperationException($"Extension schema parsed to null: {fullPath}");
            })
            .ToArray();

        var rawNodes = new ApiSchemaDocumentNodes(coreNode, extensionNodes);
        var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
        var normalizationResult = normalizer.Normalize(rawNodes);

        return normalizationResult is ApiSchemaNormalizationResult.SuccessResult success
            ? success.NormalizedNodes
            : throw new InvalidOperationException(
                $"ApiSchema normalization failed for fixture inputs: {normalizationResult}"
            );
    }
}

internal static class DdlPipelineHelperTestModelLookup
{
    public static DbTableModel FindTable(DerivedRelationalModelSet modelSet, string tableName)
    {
        return modelSet
            .ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.TablesInDependencyOrder
            )
            .Single(table => table.Table.Name.Equals(tableName, StringComparison.Ordinal));
    }
}

internal static class DdlPipelineHelperConstraintLookup
{
    public static TableConstraint.Unique FindUniqueConstraint(DbTableModel table, params string[] columns)
    {
        return table
            .Constraints.OfType<TableConstraint.Unique>()
            .Single(constraint => constraint.Columns.Select(column => column.Value).SequenceEqual(columns));
    }
}

[TestFixture]
public class Given_DdlPipelineHelpers_With_Small_Ext_Fixture_Without_Semantic_Identity
{
    private DerivedRelationalModelSet _modelSet = default!;
    private string _combinedSql = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        var effectiveSchemaSet = SmallFixtureEffectiveSchemaSetLoader.Load("ext");
        (_modelSet, _combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Pgsql
        );
    }

    [Test]
    public void It_should_keep_the_base_collection_permissive()
    {
        var schoolAddress = DdlPipelineHelperTestModelLookup.FindTable(_modelSet, "SchoolAddress");

        schoolAddress.IdentityMetadata.TableKind.Should().Be(DbTableKind.Collection);
        schoolAddress
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(column => column.Value)
            .Should()
            .Equal("CollectionItemId");
        schoolAddress.IdentityMetadata.SemanticIdentityBindings.Should().BeEmpty();
    }

    [Test]
    public void It_should_keep_collection_aligned_extension_scopes_bound_to_stable_base_identity()
    {
        var schoolExtensionAddress = DdlPipelineHelperTestModelLookup.FindTable(
            _modelSet,
            "SchoolExtensionAddress"
        );
        var foreignKey = schoolExtensionAddress
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint =>
                constraint.TargetTable.Name.Equals("SchoolAddress", StringComparison.Ordinal)
            );

        schoolExtensionAddress
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(column => column.Value)
            .Should()
            .Equal("BaseCollectionItemId");
        schoolExtensionAddress.IdentityMetadata.SemanticIdentityBindings.Should().BeEmpty();
        foreignKey
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BaseCollectionItemId", "School_DocumentId");
        foreignKey
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal("CollectionItemId", "School_DocumentId");
        _combinedSql.Should().Contain("\"BaseCollectionItemId\" bigint NOT NULL");
    }
}

[TestFixture]
public class Given_DdlPipelineHelpers_With_Small_Nested_Fixture_Without_Semantic_Identity
{
    private DerivedRelationalModelSet _modelSet = default!;
    private string _combinedSql = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        var effectiveSchemaSet = SmallFixtureEffectiveSchemaSetLoader.Load("nested");
        (_modelSet, _combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Pgsql
        );
    }

    [Test]
    public void It_should_keep_the_parent_collection_permissive()
    {
        var schoolAddress = DdlPipelineHelperTestModelLookup.FindTable(_modelSet, "SchoolAddress");

        schoolAddress.IdentityMetadata.TableKind.Should().Be(DbTableKind.Collection);
        schoolAddress
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(column => column.Value)
            .Should()
            .Equal("CollectionItemId");
        schoolAddress.IdentityMetadata.SemanticIdentityBindings.Should().BeEmpty();
    }

    [Test]
    public void It_should_anchor_nested_collection_foreign_keys_on_parent_collection_identity()
    {
        var schoolAddressPhoneNumber = DdlPipelineHelperTestModelLookup.FindTable(
            _modelSet,
            "SchoolAddressPhoneNumber"
        );
        var foreignKey = schoolAddressPhoneNumber
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint =>
                constraint.TargetTable.Name.Equals("SchoolAddress", StringComparison.Ordinal)
            );

        schoolAddressPhoneNumber
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(column => column.Value)
            .Should()
            .Equal("ParentCollectionItemId");
        schoolAddressPhoneNumber
            .IdentityMetadata.PhysicalRowIdentityColumns.Select(column => column.Value)
            .Should()
            .Equal("CollectionItemId");
        schoolAddressPhoneNumber.IdentityMetadata.SemanticIdentityBindings.Should().BeEmpty();
        foreignKey
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("ParentCollectionItemId", "School_DocumentId");
        foreignKey
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal("CollectionItemId", "School_DocumentId");
        _combinedSql.Should().Contain("\"ParentCollectionItemId\" bigint NOT NULL");
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_DdlPipelineHelpers_With_Focused_Stable_Key_Negative_Fixture_Without_Semantic_Identity(
    SqlDialect dialect
)
{
    private const string FixturePath =
        "Fixtures/focused-stable-key/negative/missing-semantic-identity/fixture.manifest.json";

    private DerivedRelationalModelSet _modelSet = null!;
    private string _combinedSql = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var effectiveSchemaSet = FocusedStableKeyFixtureEffectiveSchemaSetLoader.Load(FixturePath);
        (_modelSet, _combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, dialect);
    }

    [Test]
    public void It_should_build_ddl_without_reintroducing_global_semantic_identity_fail_fast()
    {
        var schoolAddress = DdlPipelineHelperTestModelLookup.FindTable(_modelSet, "SchoolAddress");

        schoolAddress.IdentityMetadata.TableKind.Should().Be(DbTableKind.Collection);
        schoolAddress.IdentityMetadata.SemanticIdentityBindings.Should().BeEmpty();
        schoolAddress.Constraints.OfType<TableConstraint.Unique>().Should().HaveCount(1);
        DdlPipelineHelperConstraintLookup.FindUniqueConstraint(schoolAddress, "School_DocumentId", "Ordinal");
        _combinedSql.Should().Contain("SchoolAddress");
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_DdlPipelineHelpers_With_Focused_Stable_Key_Positive_Fixture(SqlDialect dialect)
{
    private const string FixturePath =
        "Fixtures/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";

    private DerivedRelationalModelSet _modelSet = null!;
    private string _combinedSql = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var effectiveSchemaSet = FocusedStableKeyFixtureEffectiveSchemaSetLoader.Load(FixturePath);
        (_modelSet, _combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, dialect);
    }

    [Test]
    public void It_should_emit_unique_constraints_from_compiled_semantic_identity_metadata()
    {
        AssertSemanticIdentityUnique(
            "SchoolAddressPeriod",
            ["PeriodName"],
            "ParentCollectionItemId",
            "PeriodName"
        );
        AssertSemanticIdentityUnique(
            "SchoolExtensionIntervention",
            ["InterventionCode"],
            "School_DocumentId",
            "InterventionCode"
        );
        AssertSemanticIdentityUnique(
            "SchoolExtensionAddressSponsorReference",
            ["Program_DocumentId"],
            "BaseCollectionItemId",
            "Program_DocumentId"
        );
    }

    private void AssertSemanticIdentityUnique(
        string tableName,
        IReadOnlyList<string> expectedBindingColumns,
        params string[] expectedUniqueColumns
    )
    {
        var table = DdlPipelineHelperTestModelLookup.FindTable(_modelSet, tableName);
        var uniqueConstraint = DdlPipelineHelperConstraintLookup.FindUniqueConstraint(
            table,
            expectedUniqueColumns
        );

        table
            .IdentityMetadata.SemanticIdentityBindings.Select(static binding => binding.ColumnName.Value)
            .Should()
            .Equal(expectedBindingColumns);
        _combinedSql.Should().Contain(uniqueConstraint.Name);
    }
}
