// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_A_Complete_Relational_Model_Pipeline
{
    private DbTableModel _addressTable = default!;
    private DbTableModel _periodTable = default!;
    private RelationalModelBuildResult _result = default!;
    private DbTableModel _rootTable = default!;
    private RelationalResourceModel _resourceModel = default!;

    [SetUp]
    public void Setup()
    {
        var apiSchemaRoot = LoadApiSchema("relational-model-fixture.json");

        var context = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = "schools",
        };

        var pipeline = new RelationalModelBuilderPipeline(
            new IRelationalModelBuilderStep[]
            {
                new ExtractInputsStep(),
                new ValidateJsonSchemaStep(),
                new DiscoverExtensionSitesStep(),
                new DeriveTableScopesAndKeysStep(),
                new DeriveColumnsAndDescriptorEdgesStep(),
                new CanonicalizeOrderingStep(),
            }
        );

        _result = pipeline.Run(context);
        _resourceModel = _result.ResourceModel;
        _rootTable = _resourceModel.Root;
        _addressTable = _resourceModel.TablesInReadDependencyOrder.Single(table =>
            table.Table.Name == "SchoolAddress"
        );
        _periodTable = _resourceModel.TablesInReadDependencyOrder.Single(table =>
            table.Table.Name == "SchoolAddressPeriod"
        );
    }

    [Test]
    public void It_should_create_tables_for_root_and_collections()
    {
        _resourceModel.PhysicalSchema.Should().Be(new DbSchemaName("edfi"));
        _resourceModel
            .TablesInReadDependencyOrder.Select(table => table.Table.Name)
            .Should()
            .Equal("School", "SchoolAddress", "SchoolAddressPeriod");
    }

    [Test]
    public void It_should_inline_scalar_columns()
    {
        _rootTable.Columns.Select(column => column.ColumnName.Value).Should().Contain("SchoolId");
        _rootTable.Columns.Select(column => column.ColumnName.Value).Should().Contain("Name");
        _addressTable.Columns.Select(column => column.ColumnName.Value).Should().Contain("StreetNumberName");

        var periodColumn = _periodTable.Columns.Single(column => column.ColumnName.Value == "BeginDate");

        periodColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Date));
    }

    [Test]
    public void It_should_map_descriptor_columns_and_edges()
    {
        var descriptorColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "SchoolTypeDescriptor_DescriptorId"
        );

        descriptorColumn.Kind.Should().Be(ColumnKind.DescriptorFk);
        descriptorColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));
        _rootTable
            .Columns.Select(column => column.ColumnName.Value)
            .Should()
            .NotContain("SchoolTypeDescriptor");

        var edge = _resourceModel.DescriptorEdgeSources.Single();

        edge.IsIdentityComponent.Should().BeTrue();
        edge.DescriptorValuePath.Canonical.Should().Be("$.schoolTypeDescriptor");
        edge.DescriptorResource.Should().Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
    }

    [Test]
    public void It_should_capture_extension_sites()
    {
        _result.ExtensionSites.Should().ContainSingle();
        var site = _result.ExtensionSites.Single();

        site.OwningScope.Canonical.Should().Be("$");
        site.ExtensionPath.Canonical.Should().Be("$._ext");
        site.ProjectKeys.Should().Equal("sample");
    }

    private static JsonNode LoadApiSchema(string fileName)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture not found: {path}", path);
        }

        return JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Fixture {fileName} parsed null.");
    }
}
