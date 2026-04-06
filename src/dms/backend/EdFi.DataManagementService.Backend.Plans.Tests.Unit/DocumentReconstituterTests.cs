// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_DocumentReconstituter_With_Root_Scalars_Only
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "School");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolId",
        [new JsonPathSegment.Property("schoolId")]
    );

    private static readonly JsonPathExpression _nameOfInstitutionPath = new(
        "$.nameOfInstitution",
        [new JsonPathSegment.Property("nameOfInstitution")]
    );

    [SetUp]
    public void SetUp()
    {
        var columns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("SchoolId"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: _schoolIdPath,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("NameOfInstitution"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                IsNullable: true,
                SourceJsonPath: _nameOfInstitutionPath,
                TargetResource: null
            ),
        };

        var tableModel = new DbTableModel(
            Table: _tableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        object?[] row = [1L, 255901, "Grand Bend High"];

        var tableRows = new HydratedTableRows(tableModel, [row]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: [tableRows],
            referenceProjectionPlans: [],
            descriptorProjectionSources: [],
            descriptorUriLookup: new Dictionary<long, string>()
        );
    }

    [Test]
    public void It_should_return_a_json_object()
    {
        _result.Should().BeOfType<JsonObject>();
    }

    [Test]
    public void It_should_emit_schoolId()
    {
        _result["schoolId"]!.GetValue<int>().Should().Be(255901);
    }

    [Test]
    public void It_should_emit_nameOfInstitution()
    {
        _result["nameOfInstitution"]!.GetValue<string>().Should().Be("Grand Bend High");
    }

    [Test]
    public void It_should_not_emit_DocumentId()
    {
        _result["DocumentId"].Should().BeNull();
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Null_Scalar
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "School");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolId",
        [new JsonPathSegment.Property("schoolId")]
    );

    private static readonly JsonPathExpression _webSitePath = new(
        "$.webSite",
        [new JsonPathSegment.Property("webSite")]
    );

    [SetUp]
    public void SetUp()
    {
        var columns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("SchoolId"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: _schoolIdPath,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("WebSite"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 255),
                IsNullable: true,
                SourceJsonPath: _webSitePath,
                TargetResource: null
            ),
        };

        var tableModel = new DbTableModel(
            Table: _tableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        object?[] row = [1L, 255901, null];

        var tableRows = new HydratedTableRows(tableModel, [row]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: [tableRows],
            referenceProjectionPlans: [],
            descriptorProjectionSources: [],
            descriptorUriLookup: new Dictionary<long, string>()
        );
    }

    [Test]
    public void It_should_return_a_json_object()
    {
        _result.Should().BeOfType<JsonObject>();
    }

    [Test]
    public void It_should_emit_schoolId()
    {
        _result["schoolId"]!.GetValue<int>().Should().Be(255901);
    }

    [Test]
    public void It_should_not_emit_null_webSite()
    {
        _result["webSite"].Should().BeNull();
    }

    [Test]
    public void It_should_not_emit_DocumentId()
    {
        _result["DocumentId"].Should().BeNull();
    }
}
