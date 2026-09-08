// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Shared helpers for content-version mirror derivation pass tests.
/// </summary>
internal static class MirrorDerivationTestHelpers
{
    /// <summary>
    /// The standard pass list run through content-version mirror derivation for resources without
    /// stable-key collections.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughMirrorDerivation()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new DeriveContentVersionMirrorPass(),
        ];
    }

    /// <summary>
    /// The stable-key pass list run through content-version mirror derivation for resources with
    /// collection tables.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildStableKeyPassesThroughMirrorDerivation()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new ValidateUnifiedAliasMetadataPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new SemanticIdentityCompilationPass(),
            new ValidateCollectionSemanticIdentityPass(),
            new ArrayUniquenessConstraintPass(),
            new StableCollectionConstraintPass(),
            new DescriptorForeignKeyConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new ValidateForeignKeyStorageInvariantPass(),
            new DeriveContentVersionMirrorPass(),
        ];
    }

    /// <summary>
    /// Returns the root table for the resource whose root table has the supplied name.
    /// </summary>
    internal static DbTableModel RootByTableName(DerivedRelationalModelSet set, string tableName)
    {
        return set
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.RelationalModel.Root.Table.Name == tableName
            )
            .RelationalModel.Root;
    }

    /// <summary>
    /// Returns the single derived table (any kind) across all concrete resources with the supplied name.
    /// </summary>
    internal static DbTableModel TableByName(DerivedRelationalModelSet set, string tableName)
    {
        return set
            .ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.TablesInDependencyOrder
            )
            .Single(table => table.Table.Name == tableName);
    }

    /// <summary>
    /// Asserts the supplied table carries no mirror or identity-mirror columns.
    /// </summary>
    internal static void ShouldHaveNoMirrorColumns(DbTableModel table)
    {
        table
            .Columns.Where(column =>
                column.Kind is ColumnKind.MirroredContentVersion or ColumnKind.MirroredContentLastModifiedAt
            )
            .Should()
            .BeEmpty();

        table
            .Columns.Select(column => column.ColumnName.Value)
            .Should()
            .NotContain("ContentVersion")
            .And.NotContain("ContentLastModifiedAt");
    }
}

/// <summary>
/// Test fixture for content-version mirror columns on a concrete resource root with collection tables.
/// </summary>
[TestFixture]
public class Given_A_Core_Resource_With_Collections_For_Mirror_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildNestedArrayUniquenessProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            MirrorDerivationTestHelpers.BuildStableKeyPassesThroughMirrorDerivation()
        );

        _set = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should add a ContentVersion mirror column to the resource root table.
    /// </summary>
    [Test]
    public void It_should_add_ContentVersion_mirror_column_to_root()
    {
        var root = MirrorDerivationTestHelpers.RootByTableName(_set, "BusRoute");
        var column = root.Columns.Single(c => c.ColumnName.Value == "ContentVersion");

        column.Kind.Should().Be(ColumnKind.MirroredContentVersion);
        column.ScalarType!.Kind.Should().Be(ScalarKind.Int64);
        column.IsNullable.Should().BeFalse();
        column.SourceJsonPath.Should().BeNull();
        column.TargetResource.Should().BeNull();
        column.IsWritable.Should().BeFalse();
        column.Storage.Should().BeOfType<ColumnStorage.Stored>();
    }

    /// <summary>
    /// It should add a ContentLastModifiedAt mirror column to the resource root table.
    /// </summary>
    [Test]
    public void It_should_add_ContentLastModifiedAt_mirror_column_to_root()
    {
        var root = MirrorDerivationTestHelpers.RootByTableName(_set, "BusRoute");
        var column = root.Columns.Single(c => c.ColumnName.Value == "ContentLastModifiedAt");

        column.Kind.Should().Be(ColumnKind.MirroredContentLastModifiedAt);
        column.ScalarType!.Kind.Should().Be(ScalarKind.DateTime);
        column.IsNullable.Should().BeFalse();
        column.SourceJsonPath.Should().BeNull();
        column.TargetResource.Should().BeNull();
        column.IsWritable.Should().BeFalse();
        column.Storage.Should().BeOfType<ColumnStorage.Stored>();
    }

    /// <summary>
    /// It should not add mirror columns to collection tables.
    /// </summary>
    [Test]
    public void It_should_not_add_mirror_columns_to_collection_tables()
    {
        MirrorDerivationTestHelpers.ShouldHaveNoMirrorColumns(
            MirrorDerivationTestHelpers.TableByName(_set, "BusRouteAddress")
        );
        MirrorDerivationTestHelpers.ShouldHaveNoMirrorColumns(
            MirrorDerivationTestHelpers.TableByName(_set, "BusRouteAddressPeriod")
        );
    }

    /// <summary>
    /// It should not add identity-mirror columns to the resource root table.
    /// </summary>
    [Test]
    public void It_should_not_add_identity_mirror_columns_to_root()
    {
        var root = MirrorDerivationTestHelpers.RootByTableName(_set, "BusRoute");

        root.Columns.Select(c => c.ColumnName.Value)
            .Should()
            .NotContain("IdentityVersion")
            .And.NotContain("IdentityLastModifiedAt");
    }
}

/// <summary>
/// Test fixture for content-version mirror derivation across base resource roots and resource-extension
/// (<c>_ext</c>) tables.
/// </summary>
[TestFixture]
public class Given_Resource_Extension_Tables_For_Mirror_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = CommonInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProjectSchema = CommonInventoryTestSchemaBuilder.BuildExtensionProjectSchema();
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(
            MirrorDerivationTestHelpers.BuildPassesThroughMirrorDerivation()
        );

        _set = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should add mirror columns to the base resource root table.
    /// </summary>
    [Test]
    public void It_should_add_mirror_columns_to_base_resource_root()
    {
        var root = MirrorDerivationTestHelpers.RootByTableName(_set, "Contact");

        root.Columns.Should().ContainSingle(c => c.Kind == ColumnKind.MirroredContentVersion);
        root.Columns.Should().ContainSingle(c => c.Kind == ColumnKind.MirroredContentLastModifiedAt);
    }

    /// <summary>
    /// It should not add mirror columns to resource-extension tables.
    /// </summary>
    [Test]
    public void It_should_not_add_mirror_columns_to_extension_tables()
    {
        MirrorDerivationTestHelpers.ShouldHaveNoMirrorColumns(
            MirrorDerivationTestHelpers.TableByName(_set, "ContactExtension")
        );
    }
}

/// <summary>
/// Test fixture for content-version mirror derivation on extension-project resource roots.
/// </summary>
[TestFixture]
public class Given_An_Extension_Project_Resource_For_Mirror_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            CommonInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema(),
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            MirrorPassTestSchemaBuilder.BuildExtensionProjectWithNewResourceSchema(),
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(
            MirrorDerivationTestHelpers.BuildPassesThroughMirrorDerivation()
        );

        _set = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should add mirror columns to the extension-project resource root table.
    /// </summary>
    [Test]
    public void It_should_add_mirror_columns_to_extension_project_resource_root()
    {
        var root = MirrorDerivationTestHelpers.RootByTableName(_set, "Candidate");

        root.Table.Schema.Value.Should().Be("sample");
        root.Columns.Should().ContainSingle(c => c.Kind == ColumnKind.MirroredContentVersion);
        root.Columns.Should().ContainSingle(c => c.Kind == ColumnKind.MirroredContentLastModifiedAt);
    }
}

/// <summary>
/// Test fixture for content-version mirror exclusion on descriptor resources.
/// </summary>
[TestFixture]
public class Given_Descriptor_Resources_For_Mirror_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            MirrorDerivationTestHelpers.BuildPassesThroughMirrorDerivation()
        );

        _set = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should not add mirror columns to descriptor resource root tables.
    /// </summary>
    [Test]
    public void It_should_not_add_mirror_columns_to_descriptor_resources()
    {
        var descriptorResource = _set.ConcreteResourcesInNameOrder.Single(resource =>
            resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
        );

        MirrorDerivationTestHelpers.ShouldHaveNoMirrorColumns(descriptorResource.RelationalModel.Root);
    }
}

/// <summary>
/// Schema builder for extension-project resource scenarios specific to mirror derivation tests.
/// </summary>
file static class MirrorPassTestSchemaBuilder
{
    /// <summary>
    /// Builds an extension project schema that defines a brand-new (non-extension) root resource.
    /// </summary>
    internal static JsonObject BuildExtensionProjectWithNewResourceSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["candidateIdentifier"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
            },
            ["required"] = new JsonArray("candidateIdentifier"),
        };

        var candidate = new JsonObject
        {
            ["resourceName"] = "Candidate",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.candidateIdentifier" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["CandidateIdentifier"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.candidateIdentifier",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };

        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["candidates"] = candidate },
        };
    }
}
