// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Shared helpers for document-metadata column derivation pass tests.
/// </summary>
internal static class DocumentMetadataDerivationTestHelpers
{
    /// <summary>
    /// The synthesized document-metadata column names.
    /// </summary>
    internal static readonly string[] MetadataColumnNames =
    [
        "DocumentUuid",
        "IdentityVersion",
        "IdentityLastModifiedAt",
        "CreatedAt",
        "CreatedByOwnershipTokenId",
    ];

    /// <summary>
    /// The standard pass list run through document-metadata derivation for resources without stable-key
    /// collections. The derivation pass sits immediately before constraint dialect hashing, matching the
    /// production pass order, so that the synthesized <c>UX_&lt;Table&gt;_DocumentUuid</c> unique constraint
    /// participates in hashing.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughDocumentMetadataDerivation()
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
            new DeriveDocumentMetadataColumnsPass(),
            new ApplyConstraintDialectHashingPass(),
        ];
    }

    /// <summary>
    /// The stable-key pass list run through document-metadata derivation for resources with collection tables.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildStableKeyPassesThroughDocumentMetadataDerivation()
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
            new DeriveDocumentMetadataColumnsPass(),
            new ApplyConstraintDialectHashingPass(),
            new ValidateForeignKeyStorageInvariantPass(),
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
    /// Asserts the supplied table carries no document-metadata columns and no <c>DocumentUuid</c> unique
    /// constraint.
    /// </summary>
    internal static void ShouldHaveNoDocumentMetadata(DbTableModel table)
    {
        table.Columns.Select(column => column.ColumnName.Value).Should().NotContain(MetadataColumnNames);

        table
            .Columns.Where(column =>
                column.Kind
                    is ColumnKind.DocumentUuid
                        or ColumnKind.MirroredIdentityVersion
                        or ColumnKind.MirroredIdentityLastModifiedAt
                        or ColumnKind.CreatedAt
                        or ColumnKind.CreatedByOwnershipTokenId
            )
            .Should()
            .BeEmpty();

        table
            .Constraints.OfType<TableConstraint.Unique>()
            .Where(unique => unique.Columns.Any(column => column.Value == "DocumentUuid"))
            .Should()
            .BeEmpty();
    }
}

/// <summary>
/// Test fixture for document-metadata columns on a concrete resource root with collection tables.
/// </summary>
[TestFixture]
public class Given_A_Core_Resource_With_Collections_For_Document_Metadata_Derivation
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
            DocumentMetadataDerivationTestHelpers.BuildStableKeyPassesThroughDocumentMetadataDerivation()
        );

        _set = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should add every document-metadata column to the resource root table as a stored, non-writable
    /// column with no source JSONPath and no target resource, keeping them out of write-plan compilation and
    /// JSON reconstitution.
    /// </summary>
    [Test]
    public void It_should_add_non_writable_metadata_columns_to_root()
    {
        var root = DocumentMetadataDerivationTestHelpers.RootByTableName(_set, "BusRoute");

        foreach (var columnName in DocumentMetadataDerivationTestHelpers.MetadataColumnNames)
        {
            var column = root.Columns.Single(c => c.ColumnName.Value == columnName);

            column.IsWritable.Should().BeFalse();
            column.SourceJsonPath.Should().BeNull();
            column.TargetResource.Should().BeNull();
            column.Storage.Should().BeOfType<ColumnStorage.Stored>();
        }
    }

    /// <summary>
    /// It should classify the DocumentUuid column and leave its storage type to the DDL emitter.
    /// </summary>
    [Test]
    public void It_should_add_DocumentUuid_column_to_root()
    {
        var root = DocumentMetadataDerivationTestHelpers.RootByTableName(_set, "BusRoute");
        var column = root.Columns.Single(c => c.ColumnName.Value == "DocumentUuid");

        column.Kind.Should().Be(ColumnKind.DocumentUuid);
        column.ScalarType.Should().BeNull();
        column.IsNullable.Should().BeFalse();
    }

    /// <summary>
    /// It should classify the IdentityVersion mirror column as a non-nullable 64-bit integer.
    /// </summary>
    [Test]
    public void It_should_add_IdentityVersion_column_to_root()
    {
        var root = DocumentMetadataDerivationTestHelpers.RootByTableName(_set, "BusRoute");
        var column = root.Columns.Single(c => c.ColumnName.Value == "IdentityVersion");

        column.Kind.Should().Be(ColumnKind.MirroredIdentityVersion);
        column.ScalarType!.Kind.Should().Be(ScalarKind.Int64);
        column.IsNullable.Should().BeFalse();
    }

    /// <summary>
    /// It should classify the IdentityLastModifiedAt mirror column as a non-nullable date-time.
    /// </summary>
    [Test]
    public void It_should_add_IdentityLastModifiedAt_column_to_root()
    {
        var root = DocumentMetadataDerivationTestHelpers.RootByTableName(_set, "BusRoute");
        var column = root.Columns.Single(c => c.ColumnName.Value == "IdentityLastModifiedAt");

        column.Kind.Should().Be(ColumnKind.MirroredIdentityLastModifiedAt);
        column.ScalarType!.Kind.Should().Be(ScalarKind.DateTime);
        column.IsNullable.Should().BeFalse();
    }

    /// <summary>
    /// It should classify the CreatedAt mirror column as a non-nullable date-time.
    /// </summary>
    [Test]
    public void It_should_add_CreatedAt_column_to_root()
    {
        var root = DocumentMetadataDerivationTestHelpers.RootByTableName(_set, "BusRoute");
        var column = root.Columns.Single(c => c.ColumnName.Value == "CreatedAt");

        column.Kind.Should().Be(ColumnKind.CreatedAt);
        column.ScalarType!.Kind.Should().Be(ScalarKind.DateTime);
        column.IsNullable.Should().BeFalse();
    }

    /// <summary>
    /// It should classify the CreatedByOwnershipTokenId mirror column as nullable, leaving its storage type to
    /// the DDL emitter.
    /// </summary>
    [Test]
    public void It_should_add_nullable_CreatedByOwnershipTokenId_column_to_root()
    {
        var root = DocumentMetadataDerivationTestHelpers.RootByTableName(_set, "BusRoute");
        var column = root.Columns.Single(c => c.ColumnName.Value == "CreatedByOwnershipTokenId");

        column.Kind.Should().Be(ColumnKind.CreatedByOwnershipTokenId);
        column.ScalarType.Should().BeNull();
        column.IsNullable.Should().BeTrue();
    }

    /// <summary>
    /// It should add the per-root DocumentUuid unique constraint.
    /// </summary>
    [Test]
    public void It_should_add_DocumentUuid_unique_constraint_to_root()
    {
        var root = DocumentMetadataDerivationTestHelpers.RootByTableName(_set, "BusRoute");

        var unique = root
            .Constraints.OfType<TableConstraint.Unique>()
            .Single(u => u.Name == "UX_BusRoute_DocumentUuid");

        unique.Columns.Select(column => column.Value).Should().Equal("DocumentUuid");
    }

    /// <summary>
    /// It should not add document-metadata columns to collection tables.
    /// </summary>
    [Test]
    public void It_should_not_add_metadata_columns_to_collection_tables()
    {
        DocumentMetadataDerivationTestHelpers.ShouldHaveNoDocumentMetadata(
            DocumentMetadataDerivationTestHelpers.TableByName(_set, "BusRouteAddress")
        );
        DocumentMetadataDerivationTestHelpers.ShouldHaveNoDocumentMetadata(
            DocumentMetadataDerivationTestHelpers.TableByName(_set, "BusRouteAddressPeriod")
        );
    }
}

/// <summary>
/// Test fixture for document-metadata derivation across base resource roots and resource-extension
/// (<c>_ext</c>) tables.
/// </summary>
[TestFixture]
public class Given_Resource_Extension_Tables_For_Document_Metadata_Derivation
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
            CommonInventoryTestSchemaBuilder.BuildExtensionProjectSchema(),
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(
            DocumentMetadataDerivationTestHelpers.BuildPassesThroughDocumentMetadataDerivation()
        );

        _set = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should add the metadata columns and the DocumentUuid unique to the base resource root table.
    /// </summary>
    [Test]
    public void It_should_add_metadata_to_base_resource_root()
    {
        var root = DocumentMetadataDerivationTestHelpers.RootByTableName(_set, "Contact");

        root.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Contain(DocumentMetadataDerivationTestHelpers.MetadataColumnNames);
        root.Constraints.OfType<TableConstraint.Unique>()
            .Should()
            .ContainSingle(unique => unique.Name == "UX_Contact_DocumentUuid");
    }

    /// <summary>
    /// It should not add document-metadata columns to resource-extension tables.
    /// </summary>
    [Test]
    public void It_should_not_add_metadata_columns_to_extension_tables()
    {
        DocumentMetadataDerivationTestHelpers.ShouldHaveNoDocumentMetadata(
            DocumentMetadataDerivationTestHelpers.TableByName(_set, "ContactExtension")
        );
    }
}

/// <summary>
/// Test fixture for document-metadata exclusion on descriptor resources, whose rows live in the shared
/// <c>dms.Descriptor</c> table emitted by the core DDL pass.
/// </summary>
[TestFixture]
public class Given_Descriptor_Resources_For_Document_Metadata_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema(),
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            DocumentMetadataDerivationTestHelpers.BuildPassesThroughDocumentMetadataDerivation()
        );

        _set = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should not add document-metadata columns or the DocumentUuid unique to descriptor resource roots.
    /// </summary>
    [Test]
    public void It_should_not_add_metadata_to_descriptor_resources()
    {
        var descriptorResource = _set.ConcreteResourcesInNameOrder.Single(resource =>
            resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
        );

        DocumentMetadataDerivationTestHelpers.ShouldHaveNoDocumentMetadata(
            descriptorResource.RelationalModel.Root
        );
    }
}
