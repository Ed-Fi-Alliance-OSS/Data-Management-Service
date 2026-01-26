// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_A_Relational_Model_Pipeline_With_Two_Steps
{
    private List<int> _executionOrder = default!;
    private RelationalModelBuildResult _result = default!;
    private RelationalResourceModel _resourceModel = default!;

    [SetUp]
    public void Setup()
    {
        _executionOrder = [];
        _resourceModel = CreateMinimalModel();

        var steps = new IRelationalModelBuilderStep[]
        {
            new TrackingStep(1, _executionOrder),
            new TrackingStep(2, _executionOrder),
        };

        var pipeline = new RelationalModelBuilderPipeline(steps);
        var context = new RelationalModelBuilderContext { ResourceModel = _resourceModel };

        _result = pipeline.Run(context);
    }

    [Test]
    public void It_should_run_steps_in_order()
    {
        _executionOrder.Should().Equal(1, 2);
    }

    [Test]
    public void It_should_return_the_resource_model()
    {
        _result.ResourceModel.Should().BeSameAs(_resourceModel);
    }

    private static RelationalResourceModel CreateMinimalModel()
    {
        var schema = new DbSchemaName("edfi");
        var keyColumn = new DbKeyColumn(
            RelationalNameConventions.DocumentIdColumnName,
            ColumnKind.ParentKeyPart
        );
        var columns = new[]
        {
            new DbColumnModel(
                RelationalNameConventions.DocumentIdColumnName,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
        };
        var table = new DbTableModel(
            new DbTableName(schema, "School"),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey([keyColumn]),
            columns,
            Array.Empty<TableConstraint>()
        );

        return new RelationalResourceModel(
            new QualifiedResourceName("edfi", "School"),
            schema,
            table,
            [table],
            [table],
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );
    }

    private sealed class TrackingStep(int order, List<int> executionOrder) : IRelationalModelBuilderStep
    {
        public void Execute(RelationalModelBuilderContext context)
        {
            executionOrder.Add(order);
        }
    }
}
