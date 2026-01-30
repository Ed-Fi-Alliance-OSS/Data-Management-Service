// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_JsonSchema_With_Nested_Collections
{
    private DbTableModel _addressTable = default!;
    private DbTableModel _periodTable = default!;
    private DbTableModel _rootTable = default!;
    private DbSchemaName _schemaName = default!;

    [SetUp]
    public void Setup()
    {
        var schema = CreateSchema();
        var context = new RelationalModelBuilderContext
        {
            ProjectName = "Ed-Fi",
            ProjectEndpointName = "ed-fi",
            ResourceName = "School",
            JsonSchemaForInsert = schema,
        };

        var step = new DeriveTableScopesAndKeysStep();

        step.Execute(context);

        context.ResourceModel.Should().NotBeNull();

        _schemaName = context.ResourceModel!.PhysicalSchema;
        _rootTable = context.ResourceModel.Root;
        _addressTable = context.ResourceModel.TablesInReadDependencyOrder.Single(table =>
            table.Table.Name == "SchoolAddress"
        );
        _periodTable = context.ResourceModel.TablesInReadDependencyOrder.Single(table =>
            table.Table.Name == "SchoolAddressPeriod"
        );
    }

    [Test]
    public void It_should_define_root_key_as_parent_key_part()
    {
        _rootTable
            .Key.Columns.Select(column => (column.ColumnName.Value, column.Kind))
            .Should()
            .Equal((RelationalNameConventions.DocumentIdColumnName.Value, ColumnKind.ParentKeyPart));
    }

    [Test]
    public void It_should_create_collection_keys()
    {
        _addressTable
            .Key.Columns.Select(column => (column.ColumnName.Value, column.Kind))
            .Should()
            .Equal(
                (
                    RelationalNameConventions.RootDocumentIdColumnName("School").Value,
                    ColumnKind.ParentKeyPart
                ),
                (RelationalNameConventions.OrdinalColumnName.Value, ColumnKind.Ordinal)
            );

        _periodTable
            .Key.Columns.Select(column => (column.ColumnName.Value, column.Kind))
            .Should()
            .Equal(
                (
                    RelationalNameConventions.RootDocumentIdColumnName("School").Value,
                    ColumnKind.ParentKeyPart
                ),
                (
                    RelationalNameConventions.ParentCollectionOrdinalColumnName("Address").Value,
                    ColumnKind.ParentKeyPart
                ),
                (RelationalNameConventions.OrdinalColumnName.Value, ColumnKind.Ordinal)
            );
    }

    [Test]
    public void It_should_seed_key_columns_in_column_inventory()
    {
        _rootTable
            .Columns.Select(column => (column.ColumnName.Value, column.Kind))
            .Should()
            .Equal((RelationalNameConventions.DocumentIdColumnName.Value, ColumnKind.ParentKeyPart));

        _addressTable
            .Columns.Select(column => (column.ColumnName.Value, column.Kind))
            .Should()
            .Equal(
                (
                    RelationalNameConventions.RootDocumentIdColumnName("School").Value,
                    ColumnKind.ParentKeyPart
                ),
                (RelationalNameConventions.OrdinalColumnName.Value, ColumnKind.Ordinal)
            );

        _periodTable
            .Columns.Select(column => (column.ColumnName.Value, column.Kind))
            .Should()
            .Equal(
                (
                    RelationalNameConventions.RootDocumentIdColumnName("School").Value,
                    ColumnKind.ParentKeyPart
                ),
                (
                    RelationalNameConventions.ParentCollectionOrdinalColumnName("Address").Value,
                    ColumnKind.ParentKeyPart
                ),
                (RelationalNameConventions.OrdinalColumnName.Value, ColumnKind.Ordinal)
            );
    }

    [Test]
    public void It_should_create_parent_child_foreign_keys()
    {
        var periodFk = _periodTable.Constraints.OfType<TableConstraint.ForeignKey>().Single();

        periodFk
            .Columns.Select(column => column.Value)
            .Should()
            .Equal(
                RelationalNameConventions.RootDocumentIdColumnName("School").Value,
                RelationalNameConventions.ParentCollectionOrdinalColumnName("Address").Value
            );

        periodFk.TargetTable.Should().Be(new DbTableName(_schemaName, "SchoolAddress"));
        periodFk
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal(
                RelationalNameConventions.RootDocumentIdColumnName("School").Value,
                RelationalNameConventions.OrdinalColumnName.Value
            );
        periodFk.OnDelete.Should().Be(ReferentialAction.Cascade);
        periodFk.OnUpdate.Should().Be(ReferentialAction.NoAction);

        var addressFk = _addressTable.Constraints.OfType<TableConstraint.ForeignKey>().Single();

        addressFk
            .Columns.Select(column => column.Value)
            .Should()
            .Equal(RelationalNameConventions.RootDocumentIdColumnName("School").Value);

        addressFk.TargetTable.Should().Be(new DbTableName(_schemaName, "School"));
        addressFk
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal(RelationalNameConventions.DocumentIdColumnName.Value);
        addressFk.OnDelete.Should().Be(ReferentialAction.Cascade);
        addressFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }

    [Test]
    public void It_should_create_the_root_document_foreign_key()
    {
        var rootFk = _rootTable.Constraints.OfType<TableConstraint.ForeignKey>().Single();

        rootFk
            .Columns.Select(column => column.Value)
            .Should()
            .Equal(RelationalNameConventions.DocumentIdColumnName.Value);

        rootFk.TargetTable.Should().Be(new DbTableName(new DbSchemaName("dms"), "Document"));
        rootFk
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal(RelationalNameConventions.DocumentIdColumnName.Value);
        rootFk.OnDelete.Should().Be(ReferentialAction.Cascade);
        rootFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }

    private static JsonObject CreateSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["periods"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject(),
                                },
                            },
                        },
                    },
                },
            },
        };
    }
}

[TestFixture]
public class Given_A_Descriptor_Resource
{
    private RelationalResourceModel _resourceModel = default!;

    [SetUp]
    public void Setup()
    {
        var schema = CreateDescriptorSchema();
        var context = new RelationalModelBuilderContext
        {
            ProjectName = "Ed-Fi",
            ProjectEndpointName = "ed-fi",
            ResourceName = "AcademicSubjectDescriptor",
            JsonSchemaForInsert = schema,
            IsDescriptorResource = true,
        };

        var step = new DeriveTableScopesAndKeysStep();
        step.Execute(context);

        _resourceModel = context.ResourceModel!;
    }

    [Test]
    public void It_should_mark_storage_kind_as_shared_descriptor_table()
    {
        _resourceModel.StorageKind.Should().Be(ResourceStorageKind.SharedDescriptorTable);
    }

    [Test]
    public void It_should_use_the_shared_descriptor_table_as_root()
    {
        _resourceModel.Root.Table.Should().Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
    }

    [Test]
    public void It_should_preserve_project_schema_ownership_for_shared_descriptor_storage()
    {
        _resourceModel.StorageKind.Should().Be(ResourceStorageKind.SharedDescriptorTable);
        _resourceModel.PhysicalSchema.Should().Be(new DbSchemaName("edfi"));
        _resourceModel.Root.Table.Should().Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
        _resourceModel.PhysicalSchema.Should().NotBe(_resourceModel.Root.Table.Schema);
    }

    [Test]
    public void It_should_not_create_per_descriptor_tables()
    {
        _resourceModel.TablesInReadDependencyOrder.Should().ContainSingle();
        _resourceModel
            .TablesInReadDependencyOrder.Select(table => table.Table.Name)
            .Should()
            .NotContain("AcademicSubjectDescriptor");
    }

    private static JsonObject CreateDescriptorSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                ["shortDescription"] = new JsonObject { ["type"] = "string", ["maxLength"] = 75 },
            },
            ["required"] = new JsonArray("namespace", "codeValue", "shortDescription"),
        };
    }
}
