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
public class Given_A_Reference_Identity_Foreign_Key
{
    private string _manifest = default!;

    [SetUp]
    public void Setup()
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "Student");
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
            new DbColumnModel(
                new DbColumnName("School_DocumentId"),
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.schoolReference"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "School")
            ),
            new DbColumnModel(
                new DbColumnName("School_SchoolId"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                TargetResource: null
            ),
        };

        var foreignKey = new TableConstraint.ForeignKey(
            "FK_Student_SchoolIdentity",
            new[] { new DbColumnName("School_DocumentId"), new DbColumnName("School_SchoolId") },
            new DbTableName(schema, "SchoolIdentity"),
            new[] { RelationalNameConventions.DocumentIdColumnName, new DbColumnName("SchoolId") },
            OnDelete: ReferentialAction.NoAction,
            OnUpdate: ReferentialAction.Cascade
        );

        var table = new DbTableModel(
            tableName,
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey([keyColumn]),
            columns,
            new TableConstraint[] { foreignKey }
        );

        var resourceModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "Student"),
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            [table],
            [table],
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );

        var buildResult = new RelationalModelBuildResult(resourceModel, Array.Empty<ExtensionSite>());

        _manifest = RelationalModelManifestEmitter.Emit(buildResult);
    }

    [Test]
    public void It_should_emit_update_cascade_for_reference_identity_foreign_keys()
    {
        var root =
            JsonNode.Parse(_manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");

        var tables =
            root["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        var table =
            tables
                .Select(tableNode => tableNode as JsonObject)
                .Single(tableNode =>
                    string.Equals(tableNode?["name"]?.GetValue<string>(), "Student", StringComparison.Ordinal)
                )
            ?? throw new InvalidOperationException("Expected table 'Student'.");

        var constraints =
            table["constraints"] as JsonArray
            ?? throw new InvalidOperationException("Expected constraints to be a JSON array.");

        var foreignKey =
            constraints
                .Select(constraint => constraint as JsonObject)
                .Single(constraint =>
                    string.Equals(
                        constraint?["name"]?.GetValue<string>(),
                        "FK_Student_SchoolIdentity",
                        StringComparison.Ordinal
                    )
                )
            ?? throw new InvalidOperationException("Expected reference identity foreign key constraint.");

        foreignKey["on_update"]!.GetValue<string>().Should().Be("Cascade");
        foreignKey["on_delete"]!.GetValue<string>().Should().Be("NoAction");
    }
}
