// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_A_Compact_Descriptor_Root(SqlDialect dialect)
{
    private DerivedRelationalModelSet _modelSet = default!;
    private RelationalResourceModel _model = default!;

    [SetUp]
    public void Setup()
    {
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema(),
            false
        );
        _modelSet = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault()).Build(
            EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]),
            dialect,
            dialect is SqlDialect.Pgsql ? new PgsqlDialectRules() : new MssqlDialectRules()
        );
        _model = _modelSet.ConcreteResourcesInNameOrder.Single().RelationalModel;
    }

    [Test]
    public void It_should_separate_physical_row_identity_from_the_owning_document()
    {
        var root = _model.Root;
        root.Key.ConstraintName.Should().Be("PK_Descriptor");
        root.Key.Columns.Select(column => column.ColumnName.Value).Should().Equal("DescriptorId");
        root.IdentityMetadata.PhysicalRowIdentityColumns.Select(column => column.Value)
            .Should()
            .Equal("DescriptorId");
        root.IdentityMetadata.RootScopeLocatorColumns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId");
        root.IdentityMetadata.ImmediateParentScopeLocatorColumns.Should().BeEmpty();

        var descriptorId = root.Columns.Single(column => column.ColumnName.Value == "DescriptorId");
        descriptorId.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int32));
        descriptorId.IsNullable.Should().BeFalse();
        var documentId = root.Columns.Single(column => column.ColumnName.Value == "DocumentId");
        documentId.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));
        documentId.IsNullable.Should().BeFalse();
        var association = root.Constraints.OfType<TableConstraint.Unique>().Should().ContainSingle().Subject;
        association.Name.Should().Be("UX_Descriptor_DocumentId");
        association.Columns.Select(column => column.Value).Should().Equal("DocumentId");
        var documentFk = root
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Should()
            .ContainSingle()
            .Subject;
        documentFk.Columns.Select(column => column.Value).Should().Equal("DocumentId");
        documentFk.TargetTable.Should().Be(new DbTableName(new("dms"), "Document"));
        documentFk.TargetColumns.Select(column => column.Value).Should().Equal("DocumentId");
        documentFk.OnDelete.Should().Be(ReferentialAction.Restrict);
        root.Columns.Select(column => column.ColumnName.Value).Should().NotContain("Uri", "Discriminator");
    }

    [Test]
    public void It_should_emit_both_key_roles_in_resource_and_model_set_manifests()
    {
        var resourceManifest = JsonNode.Parse(RelationalModelManifestEmitter.Emit(_model, []))!;
        var setManifest = JsonNode.Parse(
            DerivedModelSetManifestEmitter.Emit(
                _modelSet,
                new HashSet<QualifiedResourceName> { _model.Resource }
            )
        )!;
        var detail = setManifest["resource_details"]!.AsArray().Single()!;

        foreach (var manifest in new[] { resourceManifest, detail })
        {
            manifest["tables"]!.AsArray().Should().BeEmpty();
            var sharedTable = manifest["shared_descriptor_table"]!;
            sharedTable["key_columns"]!.AsArray().Single()!["name"]!
                .GetValue<string>()
                .Should()
                .Be("DescriptorId");
            var identity = sharedTable["identity_metadata"]!;
            identity["physical_row_identity_columns"]!
                .AsArray()
                .Single()!
                .GetValue<string>()
                .Should()
                .Be("DescriptorId");
            identity["root_scope_locator_columns"]!
                .AsArray()
                .Single()!
                .GetValue<string>()
                .Should()
                .Be("DocumentId");
            var columns = sharedTable["columns"]!.AsArray();
            columns.Single(column => column!["name"]!.GetValue<string>() == "DescriptorId")!["type"]!["kind"]!
                .GetValue<string>()
                .Should()
                .Be("Int32");
            columns.Single(column => column!["name"]!.GetValue<string>() == "DocumentId")!["type"]!["kind"]!
                .GetValue<string>()
                .Should()
                .Be("Int64");
        }
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_Compact_Descriptor_References_In_Authoritative_Models(SqlDialect dialect)
{
    private DerivedRelationalModelSet _modelSet = default!;
    private DbTableModel[] _tables = [];

    [SetUp]
    public void Setup()
    {
        var root = BackendFixturePaths.GetAuthoritativeFixtureRoot(TestContext.CurrentContext.TestDirectory);
        var core = LoadProject(
            Path.Combine(root, "ds-5.2", "inputs", "ds-5.2-api-schema-authoritative.json"),
            false
        );
        var extension = LoadProject(
            Path.Combine(root, "sample", "inputs", "sample-api-schema-authoritative.json"),
            true
        );
        _modelSet = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault()).Build(
            EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([core, extension]),
            dialect,
            dialect is SqlDialect.Pgsql ? new PgsqlDialectRules() : new MssqlDialectRules()
        );
        _tables =
        [
            .. _modelSet
                .ConcreteResourcesInNameOrder.SelectMany(resource =>
                    resource.RelationalModel.TablesInDependencyOrder
                )
                .DistinctBy(table => table.Table),
            .. _modelSet.AbstractIdentityTablesInNameOrder.Select(identity => identity.TableModel),
        ];
    }

    [Test]
    public void It_should_keep_descriptor_bindings_compact_across_all_scopes_and_projections()
    {
        var descriptorColumns = _tables
            .SelectMany(table => table.Columns)
            .Where(column => column.Kind is ColumnKind.DescriptorFk)
            .ToArray();
        descriptorColumns.Should().NotBeEmpty();
        descriptorColumns
            .Should()
            .OnlyContain(column => column.ScalarType == new RelationalScalarType(ScalarKind.Int32));
        var views = _modelSet
            .AbstractUnionViewsInNameOrder.SelectMany(view => view.OutputColumnsInSelectOrder)
            .Where(column => column.IsDescriptorReference)
            .ToArray();
        views.Should().NotBeEmpty();
        views.Should().OnlyContain(column => column.ScalarType == new RelationalScalarType(ScalarKind.Int32));

        foreach (
            var foreignKey in _tables
                .SelectMany(table => table.Constraints)
                .OfType<TableConstraint.ForeignKey>()
                .Where(fk => fk.TargetTable == new DbTableName(new("dms"), "Descriptor"))
        )
        {
            foreignKey.TargetColumns.Select(column => column.Value).Should().Equal("DescriptorId");
            foreignKey.OnDelete.Should().Be(ReferentialAction.NoAction);
            foreignKey.OnUpdate.Should().Be(ReferentialAction.NoAction);
        }
    }

    [Test]
    public void It_should_preserve_wide_document_collection_and_change_version_columns()
    {
        var wideColumns = _tables
            .SelectMany(table => table.Columns)
            .Where(column =>
                RelationalNameConventions.IsDocumentIdColumn(column.ColumnName)
                || RelationalNameConventions.IsCollectionIdentityColumn(column.ColumnName)
                || column.Kind is ColumnKind.DocumentFk or ColumnKind.MirroredContentVersion
            )
            .ToArray();
        wideColumns.Should().NotBeEmpty();
        wideColumns
            .Should()
            .OnlyContain(column => column.ScalarType == new RelationalScalarType(ScalarKind.Int64));
    }

    private static EffectiveProjectSchema LoadProject(string path, bool isExtension)
    {
        var project = JsonNode.Parse(File.ReadAllText(path))!["projectSchema"]!.AsObject();
        return EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(project, isExtension);
    }
}

[TestFixture(true)]
[TestFixture(false)]
public class Given_An_Invalid_Descriptor_Storage_Contract(bool usesDocumentTarget)
{
    private Action _action = default!;

    [SetUp]
    public void Setup()
    {
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorForeignKeyConstraintPass(),
            new CorruptDescriptorStoragePass(usesDocumentTarget),
            new ValidateForeignKeyStorageInvariantPass(),
        ]);
        _action = () =>
            builder.Build(
                EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet(),
                SqlDialect.Pgsql,
                new PgsqlDialectRules()
            );
    }

    [Test]
    public void It_should_reject_the_invalid_descriptor_storage_contract()
    {
        _action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                usesDocumentTarget
                    ? "*must target only dms.Descriptor.DescriptorId*"
                    : "*must use Int32 storage*"
            );
    }

    private sealed class CorruptDescriptorStoragePass(bool usesDocumentTarget) : IRelationalModelSetPass
    {
        public void Execute(RelationalModelSetBuilderContext context)
        {
            var index = context.ConcreteResourcesInNameOrder.FindIndex(resource =>
                resource.RelationalModel.Root.Columns.Any(column => column.Kind is ColumnKind.DescriptorFk)
            );
            var resource = context.ConcreteResourcesInNameOrder[index];
            var root = resource.RelationalModel.Root;
            var updatedRoot = usesDocumentTarget
                ? root with
                {
                    Constraints = root
                        .Constraints.Select(constraint =>
                            constraint is TableConstraint.ForeignKey fk && fk.TargetTable.Name == "Descriptor"
                                ? fk with
                                {
                                    TargetColumns = [RelationalNameConventions.DocumentIdColumnName],
                                }
                                : constraint
                        )
                        .ToArray(),
                }
                : root with
                {
                    Columns = root
                        .Columns.Select(column =>
                            column.Kind is ColumnKind.DescriptorFk
                                ? column with
                                {
                                    ScalarType = new(ScalarKind.Int64),
                                }
                                : column
                        )
                        .ToArray(),
                };
            context.ConcreteResourcesInNameOrder[index] = resource with
            {
                RelationalModel = resource.RelationalModel with
                {
                    Root = updatedRoot,
                    TablesInDependencyOrder = resource
                        .RelationalModel.TablesInDependencyOrder.Select(table =>
                            table.Table == root.Table ? updatedRoot : table
                        )
                        .ToArray(),
                },
            };
        }
    }
}
