// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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

[TestFixture]
public class Given_DocumentReconstituter_With_A_Date_Scalar_Read_As_DateTime
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "StudentSchoolAssociation");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _entryDatePath = new(
        "$.entryDate",
        [new JsonPathSegment.Property("entryDate")]
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
                ColumnName: new DbColumnName("EntryDate"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.Date),
                IsNullable: false,
                SourceJsonPath: _entryDatePath,
                TargetResource: null
            ),
        };

        var tableModel = new DbTableModel(
            Table: _tableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_StudentSchoolAssociation",
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

        object?[] row = [1L, new DateTime(2024, 8, 20, 0, 0, 0, DateTimeKind.Unspecified)];

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
    public void It_should_emit_the_date_in_date_only_format()
    {
        _result["entryDate"]!.GetValue<string>().Should().Be("2024-08-20");
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Collection
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "School");
    private static readonly DbTableName _addressTableName = new(_schema, "SchoolAddress");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _addressesScope = new(
        "$.addresses[*]",
        [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
    );

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolId",
        [new JsonPathSegment.Property("schoolId")]
    );

    private static readonly JsonPathExpression _cityPath = new(
        "$.addresses[*].city",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("city"),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        var rootColumns = new List<DbColumnModel>
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
        };

        var rootTableModel = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: rootColumns,
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

        var addressColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("CollectionItemId"),
                Kind: ColumnKind.CollectionKey,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("School_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Ordinal"),
                Kind: ColumnKind.Ordinal,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("City"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                IsNullable: false,
                SourceJsonPath: _cityPath,
                TargetResource: null
            ),
        };

        var addressTableModel = new DbTableModel(
            Table: _addressTableName,
            JsonScope: _addressesScope,
            Key: new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: addressColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        object?[] rootRow = [1L, 255901];
        object?[] addressRow1 = [10L, 1L, 0, "Grand Bend"];
        object?[] addressRow2 = [11L, 1L, 1, "Austin"];

        var rootTableRows = new HydratedTableRows(rootTableModel, [rootRow]);
        var addressTableRows = new HydratedTableRows(addressTableModel, [addressRow1, addressRow2]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: [rootTableRows, addressTableRows],
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
    public void It_should_emit_addresses_array_with_two_items()
    {
        _result["addresses"]!.AsArray().Should().HaveCount(2);
    }

    [Test]
    public void It_should_emit_first_address_city_as_Grand_Bend()
    {
        _result["addresses"]![0]!["city"]!.GetValue<string>().Should().Be("Grand Bend");
    }

    [Test]
    public void It_should_emit_second_address_city_as_Austin()
    {
        _result["addresses"]![1]!["city"]!.GetValue<string>().Should().Be("Austin");
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Nested_Collection
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "School");
    private static readonly DbTableName _addressTableName = new(_schema, "SchoolAddress");
    private static readonly DbTableName _periodTableName = new(_schema, "SchoolAddressPeriod");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _addressesScope = new(
        "$.addresses[*]",
        [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
    );

    private static readonly JsonPathExpression _periodsScope = new(
        "$.addresses[*].periods[*]",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("periods"),
            new JsonPathSegment.AnyArrayElement(),
        ]
    );

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolId",
        [new JsonPathSegment.Property("schoolId")]
    );

    private static readonly JsonPathExpression _cityPath = new(
        "$.addresses[*].city",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("city"),
        ]
    );

    private static readonly JsonPathExpression _beginDatePath = new(
        "$.addresses[*].periods[*].beginDate",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("periods"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("beginDate"),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        var rootColumns = new List<DbColumnModel>
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
        };

        var rootTableModel = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: rootColumns,
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

        var addressColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("CollectionItemId"),
                Kind: ColumnKind.CollectionKey,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("School_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Ordinal"),
                Kind: ColumnKind.Ordinal,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("City"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                IsNullable: false,
                SourceJsonPath: _cityPath,
                TargetResource: null
            ),
        };

        var addressTableModel = new DbTableModel(
            Table: _addressTableName,
            JsonScope: _addressesScope,
            Key: new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: addressColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var periodColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("CollectionItemId"),
                Kind: ColumnKind.CollectionKey,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("School_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("ParentCollectionItemId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Ordinal"),
                Kind: ColumnKind.Ordinal,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("BeginDate"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 10),
                IsNullable: false,
                SourceJsonPath: _beginDatePath,
                TargetResource: null
            ),
        };

        var periodTableModel = new DbTableModel(
            Table: _periodTableName,
            JsonScope: _periodsScope,
            Key: new TableKey(
                "PK_SchoolAddressPeriod",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: periodColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentCollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };

        object?[] rootRow = [1L, 255901];
        object?[] addressRow = [10L, 1L, 0, "Grand Bend"];
        object?[] periodRow1 = [100L, 1L, 10L, 0, "2024-01-01"];
        object?[] periodRow2 = [101L, 1L, 10L, 1, "2024-06-01"];

        var rootTableRows = new HydratedTableRows(rootTableModel, [rootRow]);
        var addressTableRows = new HydratedTableRows(addressTableModel, [addressRow]);
        var periodTableRows = new HydratedTableRows(periodTableModel, [periodRow1, periodRow2]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: [rootTableRows, addressTableRows, periodTableRows],
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
    public void It_should_emit_addresses_array_with_one_item()
    {
        _result["addresses"]!.AsArray().Should().HaveCount(1);
    }

    [Test]
    public void It_should_emit_periods_array_with_two_items()
    {
        _result["addresses"]![0]!["periods"]!.AsArray().Should().HaveCount(2);
    }

    [Test]
    public void It_should_emit_first_period_beginDate()
    {
        _result["addresses"]![0]!["periods"]![0]!["beginDate"]!.GetValue<string>().Should().Be("2024-01-01");
    }

    [Test]
    public void It_should_emit_second_period_beginDate()
    {
        _result["addresses"]![0]!["periods"]![1]!["beginDate"]!.GetValue<string>().Should().Be("2024-06-01");
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Reference
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "StudentSchoolAssociation");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _schoolReferencePath = new(
        "$.schoolReference",
        [new JsonPathSegment.Property("schoolReference")]
    );

    private static readonly JsonPathExpression _schoolReferenceSchoolIdPath = new(
        "$.schoolReference.schoolId",
        [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
    );

    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

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
                ColumnName: new DbColumnName("School_DocumentId"),
                Kind: ColumnKind.DocumentFk,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: _schoolResource
            ),
            new(
                ColumnName: new DbColumnName("SchoolReference_SchoolId"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: _schoolReferenceSchoolIdPath,
                TargetResource: null
            ),
        };

        var tableModel = new DbTableModel(
            Table: _tableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_StudentSchoolAssociation",
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

        var refPlan = new ReferenceIdentityProjectionTablePlan(
            Table: _tableName,
            BindingsInOrder:
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: _schoolReferencePath,
                    TargetResource: _schoolResource,
                    FkColumnOrdinal: 1,
                    IdentityFieldOrdinalsInOrder:
                    [
                        new ReferenceIdentityProjectionFieldOrdinal(
                            ReferenceJsonPath: _schoolReferenceSchoolIdPath,
                            ColumnOrdinal: 2
                        ),
                    ]
                ),
            ]
        );

        object?[] row = [1L, 10L, 255901];

        var tableRows = new HydratedTableRows(tableModel, [row]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: [tableRows],
            referenceProjectionPlans: [refPlan],
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
    public void It_should_emit_schoolReference_object()
    {
        _result["schoolReference"].Should().BeOfType<JsonObject>();
    }

    [Test]
    public void It_should_emit_schoolReference_schoolId()
    {
        _result["schoolReference"]!["schoolId"]!.GetValue<int>().Should().Be(255901);
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Descriptor
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "StudentSchoolAssociation");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _entryGradeLevelDescriptorPath = new(
        "$.entryGradeLevelDescriptor",
        [new JsonPathSegment.Property("entryGradeLevelDescriptor")]
    );

    private static readonly QualifiedResourceName _gradeLevelDescriptorResource = new(
        "Ed-Fi",
        "GradeLevelDescriptor"
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
                ColumnName: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                Kind: ColumnKind.DescriptorFk,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: _gradeLevelDescriptorResource
            ),
        };

        var tableModel = new DbTableModel(
            Table: _tableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_StudentSchoolAssociation",
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

        var descriptorSource = new DescriptorEdgeSource(
            IsIdentityComponent: true,
            DescriptorValuePath: _entryGradeLevelDescriptorPath,
            Table: _tableName,
            FkColumn: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
            DescriptorResource: _gradeLevelDescriptorResource
        );

        object?[] row = [1L, 100L];

        var tableRows = new HydratedTableRows(tableModel, [row]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: [tableRows],
            referenceProjectionPlans: [],
            descriptorProjectionSources: [descriptorSource],
            descriptorUriLookup: new Dictionary<long, string>
            {
                { 100L, "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade" },
            }
        );
    }

    [Test]
    public void It_should_return_a_json_object()
    {
        _result.Should().BeOfType<JsonObject>();
    }

    [Test]
    public void It_should_emit_entryGradeLevelDescriptor_uri()
    {
        _result["entryGradeLevelDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be("uri://ed-fi.org/GradeLevelDescriptor#Ninth grade");
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Nested_Collection_And_Root_Extension
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "School");
    private static readonly DbTableName _addressTableName = new(_schema, "SchoolAddress");
    private static readonly DbTableName _periodTableName = new(_schema, "SchoolAddressPeriod");
    private static readonly DbTableName _extensionTableName = new(_schema, "SchoolExtension");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _addressesScope = new(
        "$.addresses[*]",
        [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
    );

    private static readonly JsonPathExpression _periodsScope = new(
        "$.addresses[*].periods[*]",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("periods"),
            new JsonPathSegment.AnyArrayElement(),
        ]
    );

    private static readonly JsonPathExpression _extensionScope = new(
        "$._ext.sample",
        [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
    );

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolId",
        [new JsonPathSegment.Property("schoolId")]
    );

    private static readonly JsonPathExpression _cityPath = new(
        "$.addresses[*].city",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("city"),
        ]
    );

    private static readonly JsonPathExpression _beginDatePath = new(
        "$.addresses[*].periods[*].beginDate",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("periods"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("beginDate"),
        ]
    );

    private static readonly JsonPathExpression _isExemplaryPath = new(
        "$._ext.sample.isExemplary",
        [
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
            new JsonPathSegment.Property("isExemplary"),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        var rootColumns = new List<DbColumnModel>
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
        };

        var rootTableModel = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: rootColumns,
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

        var addressColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("CollectionItemId"),
                Kind: ColumnKind.CollectionKey,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("School_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Ordinal"),
                Kind: ColumnKind.Ordinal,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("City"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                IsNullable: false,
                SourceJsonPath: _cityPath,
                TargetResource: null
            ),
        };

        var addressTableModel = new DbTableModel(
            Table: _addressTableName,
            JsonScope: _addressesScope,
            Key: new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: addressColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var periodColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("CollectionItemId"),
                Kind: ColumnKind.CollectionKey,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("School_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("ParentCollectionItemId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Ordinal"),
                Kind: ColumnKind.Ordinal,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("BeginDate"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 10),
                IsNullable: false,
                SourceJsonPath: _beginDatePath,
                TargetResource: null
            ),
        };

        var periodTableModel = new DbTableModel(
            Table: _periodTableName,
            JsonScope: _periodsScope,
            Key: new TableKey(
                "PK_SchoolAddressPeriod",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: periodColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentCollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };

        var extensionColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("School_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("IsExemplary"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.Boolean),
                IsNullable: false,
                SourceJsonPath: _isExemplaryPath,
                TargetResource: null
            ),
        };

        var extensionTableModel = new DbTableModel(
            Table: _extensionTableName,
            JsonScope: _extensionScope,
            Key: new TableKey(
                "PK_SchoolExtension",
                [new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: extensionColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.RootExtension,
                PhysicalRowIdentityColumns: [new DbColumnName("School_DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        object?[] rootRow = [1L, 255901];
        object?[] addressRow = [10L, 1L, 0, "Grand Bend"];
        object?[] periodRow = [100L, 1L, 10L, 0, "2024-01-01"];
        object?[] extensionRow = [1L, true];

        var rootTableRows = new HydratedTableRows(rootTableModel, [rootRow]);
        var addressTableRows = new HydratedTableRows(addressTableModel, [addressRow]);
        var periodTableRows = new HydratedTableRows(periodTableModel, [periodRow]);
        var extensionTableRows = new HydratedTableRows(extensionTableModel, [extensionRow]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder:
            [
                rootTableRows,
                addressTableRows,
                periodTableRows,
                extensionTableRows,
            ],
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
    public void It_should_emit_ext_sample_isExemplary_as_true()
    {
        _result["_ext"]!["sample"]!["isExemplary"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void It_should_emit_addresses_with_one_item()
    {
        _result["addresses"]!.AsArray().Should().HaveCount(1);
    }

    [Test]
    public void It_should_emit_addresses_0_periods_with_one_item()
    {
        _result["addresses"]![0]!["periods"]!.AsArray().Should().HaveCount(1);
    }

    [Test]
    public void It_should_emit_addresses_0_periods_0_beginDate()
    {
        _result["addresses"]![0]!["periods"]![0]!["beginDate"]!.GetValue<string>().Should().Be("2024-01-01");
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Collection_Extension_Scope_And_Child_Extension_Collection
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "Contact");
    private static readonly DbTableName _addressTableName = new(_schema, "ContactAddress");
    private static readonly DbTableName _extensionScopeTableName = new(_schema, "ContactExtensionAddress");
    private static readonly DbTableName _extCollectionTableName = new(
        _schema,
        "ContactExtensionAddressDeliveryNote"
    );

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _addressesScope = new(
        "$.addresses[*]",
        [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
    );

    private static readonly JsonPathExpression _extensionScope = new(
        "$.addresses[*]._ext.sample",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
        ]
    );

    private static readonly JsonPathExpression _deliveryNotesScope = new(
        "$.addresses[*]._ext.sample.deliveryNotes[*]",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
            new JsonPathSegment.Property("deliveryNotes"),
            new JsonPathSegment.AnyArrayElement(),
        ]
    );

    private static readonly JsonPathExpression _contactIdPath = new(
        "$.contactId",
        [new JsonPathSegment.Property("contactId")]
    );

    private static readonly JsonPathExpression _cityPath = new(
        "$.addresses[*].city",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("city"),
        ]
    );

    private static readonly JsonPathExpression _isUrbanPath = new(
        "$.addresses[*]._ext.sample.isUrban",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
            new JsonPathSegment.Property("isUrban"),
        ]
    );

    private static readonly JsonPathExpression _notePath = new(
        "$.addresses[*]._ext.sample.deliveryNotes[*].note",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
            new JsonPathSegment.Property("deliveryNotes"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("note"),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        // Root table: Contact
        var rootColumns = new List<DbColumnModel>
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
                ColumnName: new DbColumnName("ContactId"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: _contactIdPath,
                TargetResource: null
            ),
        };

        var rootTableModel = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_Contact",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: rootColumns,
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

        // Collection table: ContactAddress
        var addressColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("CollectionItemId"),
                Kind: ColumnKind.CollectionKey,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Contact_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Ordinal"),
                Kind: ColumnKind.Ordinal,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("City"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                IsNullable: false,
                SourceJsonPath: _cityPath,
                TargetResource: null
            ),
        };

        var addressTableModel = new DbTableModel(
            Table: _addressTableName,
            JsonScope: _addressesScope,
            Key: new TableKey(
                "PK_ContactAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: addressColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("Contact_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("Contact_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        // CollectionExtensionScope table: ContactExtensionAddress
        var extensionScopeColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("BaseCollectionItemId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Contact_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("IsUrban"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.Boolean),
                IsNullable: false,
                SourceJsonPath: _isUrbanPath,
                TargetResource: null
            ),
        };

        var extensionScopeTableModel = new DbTableModel(
            Table: _extensionScopeTableName,
            JsonScope: _extensionScope,
            Key: new TableKey(
                "PK_ContactExtensionAddress",
                [new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: extensionScopeColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.CollectionExtensionScope,
                PhysicalRowIdentityColumns: [new DbColumnName("BaseCollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("Contact_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("BaseCollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };

        // ExtensionCollection table: ContactExtensionAddressDeliveryNote
        var extCollectionColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("CollectionItemId"),
                Kind: ColumnKind.CollectionKey,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Contact_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("BaseCollectionItemId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Ordinal"),
                Kind: ColumnKind.Ordinal,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Note"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                IsNullable: false,
                SourceJsonPath: _notePath,
                TargetResource: null
            ),
        };

        var extCollectionTableModel = new DbTableModel(
            Table: _extCollectionTableName,
            JsonScope: _deliveryNotesScope,
            Key: new TableKey(
                "PK_ContactExtensionAddressDeliveryNote",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: extCollectionColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.ExtensionCollection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("Contact_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("BaseCollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };

        // Row data:
        // Root: DocumentId=1, ContactId=42
        object?[] rootRow = [1L, 42];
        // Address 1: CollectionItemId=10, Contact_DocumentId=1, Ordinal=0, City="Austin"
        object?[] addressRow1 = [10L, 1L, 0, "Austin"];
        // Address 2: CollectionItemId=20, Contact_DocumentId=1, Ordinal=1, City="Dallas"
        object?[] addressRow2 = [20L, 1L, 1, "Dallas"];
        // Extension scope for address 1: BaseCollectionItemId=10, Contact_DocumentId=1, IsUrban=true
        object?[] extScopeRow1 = [10L, 1L, true];
        // Extension scope for address 2: BaseCollectionItemId=20, Contact_DocumentId=1, IsUrban=false
        object?[] extScopeRow2 = [20L, 1L, false];
        // Delivery note under address 1 ext scope: CollectionItemId=100, Contact_DocumentId=1, BaseCollectionItemId=10, Ordinal=0, Note="Ring bell"
        object?[] noteRow1 = [100L, 1L, 10L, 0, "Ring bell"];
        // Delivery note under address 2 ext scope: CollectionItemId=200, Contact_DocumentId=1, BaseCollectionItemId=20, Ordinal=0, Note="Leave at door"
        object?[] noteRow2 = [200L, 1L, 20L, 0, "Leave at door"];

        var rootTableRows = new HydratedTableRows(rootTableModel, [rootRow]);
        var addressTableRows = new HydratedTableRows(addressTableModel, [addressRow1, addressRow2]);
        var extScopeTableRows = new HydratedTableRows(extensionScopeTableModel, [extScopeRow1, extScopeRow2]);
        var extCollectionTableRows = new HydratedTableRows(extCollectionTableModel, [noteRow1, noteRow2]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder:
            [
                rootTableRows,
                addressTableRows,
                extScopeTableRows,
                extCollectionTableRows,
            ],
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
    public void It_should_emit_contactId()
    {
        _result["contactId"]!.GetValue<int>().Should().Be(42);
    }

    [Test]
    public void It_should_emit_two_addresses()
    {
        _result["addresses"]!.AsArray().Should().HaveCount(2);
    }

    [Test]
    public void It_should_emit_address_0_city()
    {
        _result["addresses"]![0]!["city"]!.GetValue<string>().Should().Be("Austin");
    }

    [Test]
    public void It_should_emit_address_1_city()
    {
        _result["addresses"]![1]!["city"]!.GetValue<string>().Should().Be("Dallas");
    }

    [Test]
    public void It_should_emit_address_0_ext_sample_isUrban_as_true()
    {
        _result["addresses"]![0]!["_ext"]!["sample"]!["isUrban"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void It_should_emit_address_1_ext_sample_isUrban_as_false()
    {
        _result["addresses"]![1]!["_ext"]!["sample"]!["isUrban"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    public void It_should_emit_address_0_ext_sample_deliveryNotes_with_one_item()
    {
        _result["addresses"]![0]!["_ext"]!["sample"]!["deliveryNotes"]!.AsArray().Should().HaveCount(1);
    }

    [Test]
    public void It_should_emit_address_0_ext_sample_deliveryNotes_0_note()
    {
        _result["addresses"]![0]!["_ext"]!["sample"]!["deliveryNotes"]![0]!["note"]!
            .GetValue<string>()
            .Should()
            .Be("Ring bell");
    }

    [Test]
    public void It_should_emit_address_1_ext_sample_deliveryNotes_with_one_item()
    {
        _result["addresses"]![1]!["_ext"]!["sample"]!["deliveryNotes"]!.AsArray().Should().HaveCount(1);
    }

    [Test]
    public void It_should_emit_address_1_ext_sample_deliveryNotes_0_note()
    {
        _result["addresses"]![1]!["_ext"]!["sample"]!["deliveryNotes"]![0]!["note"]!
            .GetValue<string>()
            .Should()
            .Be("Leave at door");
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Null_Descriptor_FK
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "StudentSchoolAssociation");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _entryGradeLevelDescriptorPath = new(
        "$.entryGradeLevelDescriptor",
        [new JsonPathSegment.Property("entryGradeLevelDescriptor")]
    );

    private static readonly QualifiedResourceName _gradeLevelDescriptorResource = new(
        "Ed-Fi",
        "GradeLevelDescriptor"
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
                ColumnName: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                Kind: ColumnKind.DescriptorFk,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: true,
                SourceJsonPath: null,
                TargetResource: _gradeLevelDescriptorResource
            ),
        };

        var tableModel = new DbTableModel(
            Table: _tableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_StudentSchoolAssociation",
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

        var descriptorSource = new DescriptorEdgeSource(
            IsIdentityComponent: false,
            DescriptorValuePath: _entryGradeLevelDescriptorPath,
            Table: _tableName,
            FkColumn: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
            DescriptorResource: _gradeLevelDescriptorResource
        );

        // FK value is null — descriptor is absent
        object?[] row = [1L, null];

        var tableRows = new HydratedTableRows(tableModel, [row]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: [tableRows],
            referenceProjectionPlans: [],
            descriptorProjectionSources: [descriptorSource],
            descriptorUriLookup: new Dictionary<long, string>
            {
                { 100L, "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade" },
            }
        );
    }

    [Test]
    public void It_should_not_emit_the_descriptor_property()
    {
        _result["entryGradeLevelDescriptor"].Should().BeNull();
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Unresolved_Descriptor_Id
{
    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "StudentSchoolAssociation");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _entryGradeLevelDescriptorPath = new(
        "$.entryGradeLevelDescriptor",
        [new JsonPathSegment.Property("entryGradeLevelDescriptor")]
    );

    private static readonly QualifiedResourceName _gradeLevelDescriptorResource = new(
        "Ed-Fi",
        "GradeLevelDescriptor"
    );

    [Test]
    public void It_should_throw_for_non_null_descriptor_fk_with_no_resolved_uri()
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
                ColumnName: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                Kind: ColumnKind.DescriptorFk,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: _gradeLevelDescriptorResource
            ),
        };

        var tableModel = new DbTableModel(
            Table: _tableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_StudentSchoolAssociation",
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

        var descriptorSource = new DescriptorEdgeSource(
            IsIdentityComponent: true,
            DescriptorValuePath: _entryGradeLevelDescriptorPath,
            Table: _tableName,
            FkColumn: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
            DescriptorResource: _gradeLevelDescriptorResource
        );

        // FK value 999 is not in the lookup — simulates a projection plan or executor defect
        object?[] row = [1L, 999L];

        var tableRows = new HydratedTableRows(tableModel, [row]);

        var act = () =>
            DocumentReconstituter.Reconstitute(
                documentId: 1L,
                tableRowsInDependencyOrder: [tableRows],
                referenceProjectionPlans: [],
                descriptorProjectionSources: [descriptorSource],
                descriptorUriLookup: new Dictionary<long, string>
                {
                    { 100L, "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade" },
                }
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*999*EntryGradeLevelDescriptor_DescriptorId*");
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Inlined_Nested_Scalars
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "EducationContent");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _contentIdPath = new(
        "$.contentIdentifier",
        [new JsonPathSegment.Property("contentIdentifier")]
    );

    private static readonly JsonPathExpression _contentStandardTitlePath = new(
        "$.contentStandard.title",
        [new JsonPathSegment.Property("contentStandard"), new JsonPathSegment.Property("title")]
    );

    private static readonly JsonPathExpression _contentStandardBeginDatePath = new(
        "$.contentStandard.beginDate",
        [new JsonPathSegment.Property("contentStandard"), new JsonPathSegment.Property("beginDate")]
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
                ColumnName: new DbColumnName("ContentIdentifier"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 225),
                IsNullable: false,
                SourceJsonPath: _contentIdPath,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("ContentStandard_Title"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                IsNullable: true,
                SourceJsonPath: _contentStandardTitlePath,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("ContentStandard_BeginDate"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 10),
                IsNullable: true,
                SourceJsonPath: _contentStandardBeginDatePath,
                TargetResource: null
            ),
        };

        var tableModel = new DbTableModel(
            Table: _tableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_EducationContent",
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

        object?[] row = [1L, "content-123", "State Standards", "2024-01-01"];

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
    public void It_should_emit_root_scalar()
    {
        _result["contentIdentifier"]!.GetValue<string>().Should().Be("content-123");
    }

    [Test]
    public void It_should_create_intermediate_contentStandard_object()
    {
        _result["contentStandard"].Should().BeOfType<JsonObject>();
    }

    [Test]
    public void It_should_emit_contentStandard_title()
    {
        _result["contentStandard"]!["title"]!.GetValue<string>().Should().Be("State Standards");
    }

    [Test]
    public void It_should_emit_contentStandard_beginDate()
    {
        _result["contentStandard"]!["beginDate"]!.GetValue<string>().Should().Be("2024-01-01");
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Collection_Under_Inlined_Object
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "EducationContent");
    private static readonly DbTableName _authorTableName = new(_schema, "EducationContentAuthor");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _contentIdPath = new(
        "$.contentIdentifier",
        [new JsonPathSegment.Property("contentIdentifier")]
    );

    private static readonly JsonPathExpression _contentStandardTitlePath = new(
        "$.contentStandard.title",
        [new JsonPathSegment.Property("contentStandard"), new JsonPathSegment.Property("title")]
    );

    private static readonly JsonPathExpression _authorScope = new(
        "$.contentStandard.authors[*]",
        [
            new JsonPathSegment.Property("contentStandard"),
            new JsonPathSegment.Property("authors"),
            new JsonPathSegment.AnyArrayElement(),
        ]
    );

    private static readonly JsonPathExpression _authorNamePath = new(
        "$.contentStandard.authors[*].author",
        [
            new JsonPathSegment.Property("contentStandard"),
            new JsonPathSegment.Property("authors"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("author"),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        var rootTableModel = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_EducationContent",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("ContentIdentifier"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 225),
                    false,
                    _contentIdPath,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("ContentStandard_Title"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    true,
                    _contentStandardTitlePath,
                    null
                ),
            ],
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

        var authorTableModel = new DbTableModel(
            Table: _authorTableName,
            JsonScope: _authorScope,
            Key: new TableKey(
                "PK_EducationContentAuthor",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Author"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 150),
                    false,
                    _authorNamePath,
                    null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        // Root row: DocumentId=1, ContentIdentifier, ContentStandard_Title
        object?[] rootRow = [1L, "content-123", "State Standards"];
        // Two author rows under DocumentId=1
        object?[] authorRow1 = [1L, 10L, 0, "Smith, J."];
        object?[] authorRow2 = [1L, 11L, 1, "Jones, A."];

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder:
            [
                new HydratedTableRows(rootTableModel, [rootRow]),
                new HydratedTableRows(authorTableModel, [authorRow1, authorRow2]),
            ],
            referenceProjectionPlans: [],
            descriptorProjectionSources: [],
            descriptorUriLookup: new Dictionary<long, string>()
        );
    }

    [Test]
    public void It_should_emit_root_scalar()
    {
        _result["contentIdentifier"]!.GetValue<string>().Should().Be("content-123");
    }

    [Test]
    public void It_should_create_intermediate_contentStandard_object()
    {
        _result["contentStandard"].Should().BeOfType<JsonObject>();
    }

    [Test]
    public void It_should_emit_contentStandard_title()
    {
        _result["contentStandard"]!["title"]!.GetValue<string>().Should().Be("State Standards");
    }

    [Test]
    public void It_should_emit_authors_array_inside_contentStandard()
    {
        _result["contentStandard"]!["authors"].Should().NotBeNull();
        _result["contentStandard"]!["authors"]!.AsArray().Count.Should().Be(2);
    }

    [Test]
    public void It_should_emit_first_author()
    {
        _result["contentStandard"]!["authors"]![0]!["author"]!.GetValue<string>().Should().Be("Smith, J.");
    }

    [Test]
    public void It_should_emit_second_author()
    {
        _result["contentStandard"]!["authors"]![1]!["author"]!.GetValue<string>().Should().Be("Jones, A.");
    }

    [Test]
    public void It_should_not_emit_authors_on_root()
    {
        // authors must be nested under contentStandard, not on the root object
        _result["authors"].Should().BeNull();
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Empty_Collection
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "School");
    private static readonly DbTableName _addressTableName = new(_schema, "SchoolAddress");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _addressesScope = new(
        "$.addresses[*]",
        [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
    );

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolId",
        [new JsonPathSegment.Property("schoolId")]
    );

    private static readonly JsonPathExpression _cityPath = new(
        "$.addresses[*].city",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("city"),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        var rootColumns = new List<DbColumnModel>
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
        };

        var rootTableModel = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: rootColumns,
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

        var addressColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName("CollectionItemId"),
                Kind: ColumnKind.CollectionKey,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("School_DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Ordinal"),
                Kind: ColumnKind.Ordinal,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("City"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                IsNullable: false,
                SourceJsonPath: _cityPath,
                TargetResource: null
            ),
        };

        var addressTableModel = new DbTableModel(
            Table: _addressTableName,
            JsonScope: _addressesScope,
            Key: new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: addressColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        object?[] rootRow = [1L, 255901];

        // No address rows for this document
        var rootTableRows = new HydratedTableRows(rootTableModel, [rootRow]);
        var addressTableRows = new HydratedTableRows(addressTableModel, []);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: [rootTableRows, addressTableRows],
            referenceProjectionPlans: [],
            descriptorProjectionSources: [],
            descriptorUriLookup: new Dictionary<long, string>()
        );
    }

    [Test]
    public void It_should_emit_schoolId()
    {
        _result["schoolId"]!.GetValue<int>().Should().Be(255901);
    }

    [Test]
    public void It_should_not_emit_an_addresses_property()
    {
        _result["addresses"].Should().BeNull();
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Null_Optional_Reference
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "StudentSchoolAssociation");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _schoolReferencePath = new(
        "$.schoolReference",
        [new JsonPathSegment.Property("schoolReference")]
    );

    private static readonly JsonPathExpression _schoolReferenceSchoolIdPath = new(
        "$.schoolReference.schoolId",
        [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
    );

    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

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
                ColumnName: new DbColumnName("School_DocumentId"),
                Kind: ColumnKind.DocumentFk,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: true,
                SourceJsonPath: null,
                TargetResource: _schoolResource
            ),
            new(
                ColumnName: new DbColumnName("SchoolReference_SchoolId"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: _schoolReferenceSchoolIdPath,
                TargetResource: null
            ),
        };

        var tableModel = new DbTableModel(
            Table: _tableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_StudentSchoolAssociation",
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

        var refPlan = new ReferenceIdentityProjectionTablePlan(
            Table: _tableName,
            BindingsInOrder:
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: _schoolReferencePath,
                    TargetResource: _schoolResource,
                    FkColumnOrdinal: 1,
                    IdentityFieldOrdinalsInOrder:
                    [
                        new ReferenceIdentityProjectionFieldOrdinal(
                            ReferenceJsonPath: _schoolReferenceSchoolIdPath,
                            ColumnOrdinal: 2
                        ),
                    ]
                ),
            ]
        );

        // FK is null — optional reference is absent
        object?[] row = [1L, null, null];

        var tableRows = new HydratedTableRows(tableModel, [row]);

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: [tableRows],
            referenceProjectionPlans: [refPlan],
            descriptorProjectionSources: [],
            descriptorUriLookup: new Dictionary<long, string>()
        );
    }

    [Test]
    public void It_should_not_emit_schoolReference()
    {
        _result["schoolReference"].Should().BeNull();
    }

    [Test]
    public void It_should_return_a_json_object()
    {
        _result.Should().BeOfType<JsonObject>();
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Physical_Order_Different_From_Compiled_Json_Paths
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "School");
    private static readonly DbTableName _omegaTableName = new(_schema, "SchoolOmega");

    private static readonly JsonPathExpression _rootScope = new("$", []);
    private static readonly JsonPathExpression _omegaScope = new(
        "$.omega[*]",
        [new JsonPathSegment.Property("omega"), new JsonPathSegment.AnyArrayElement()]
    );

    private static readonly JsonPathExpression _alphaBetaPath = new(
        "$.alpha.beta",
        [new JsonPathSegment.Property("alpha"), new JsonPathSegment.Property("beta")]
    );

    private static readonly JsonPathExpression _alphaReferencePath = new(
        "$.alphaReference",
        [new JsonPathSegment.Property("alphaReference")]
    );

    private static readonly JsonPathExpression _alphaReferenceCodePath = new(
        "$.alphaReference.code",
        [new JsonPathSegment.Property("alphaReference"), new JsonPathSegment.Property("code")]
    );

    private static readonly JsonPathExpression _alphaReferenceNamespacePath = new(
        "$.alphaReference.namespace",
        [new JsonPathSegment.Property("alphaReference"), new JsonPathSegment.Property("namespace")]
    );

    private static readonly JsonPathExpression _gammaDescriptorPath = new(
        "$.gammaDescriptor",
        [new JsonPathSegment.Property("gammaDescriptor")]
    );

    private static readonly JsonPathExpression _omegaDeltaPath = new(
        "$.omega[*].delta",
        [
            new JsonPathSegment.Property("omega"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("delta"),
        ]
    );

    private static readonly JsonPathExpression _zetaPath = new(
        "$.zeta",
        [new JsonPathSegment.Property("zeta")]
    );

    private static readonly QualifiedResourceName _alphaResource = new("Ed-Fi", "Alpha");
    private static readonly QualifiedResourceName _gammaDescriptorResource = new("Ed-Fi", "GammaDescriptor");

    [SetUp]
    public void SetUp()
    {
        var rootTableModel = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Zeta"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 20),
                    false,
                    _zetaPath,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("AlphaReference_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    _alphaResource
                ),
                new DbColumnModel(
                    new DbColumnName("AlphaBeta"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    _alphaBetaPath,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("AlphaReferenceNamespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    false,
                    _alphaReferenceNamespacePath,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("GammaDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    _gammaDescriptorResource
                ),
                new DbColumnModel(
                    new DbColumnName("AlphaReferenceCode"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    false,
                    _alphaReferenceCodePath,
                    null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        var omegaTableModel = new DbTableModel(
            Table: _omegaTableName,
            JsonScope: _omegaScope,
            Key: new TableKey(
                "PK_SchoolOmega",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Delta"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 20),
                    false,
                    _omegaDeltaPath,
                    null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                []
            ),
        };

        var referencePlan = new ReferenceIdentityProjectionTablePlan(
            Table: _rootTableName,
            BindingsInOrder:
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: _alphaReferencePath,
                    TargetResource: _alphaResource,
                    FkColumnOrdinal: 2,
                    IdentityFieldOrdinalsInOrder:
                    [
                        new ReferenceIdentityProjectionFieldOrdinal(
                            ReferenceJsonPath: _alphaReferenceNamespacePath,
                            ColumnOrdinal: 4
                        ),
                        new ReferenceIdentityProjectionFieldOrdinal(
                            ReferenceJsonPath: _alphaReferenceCodePath,
                            ColumnOrdinal: 6
                        ),
                    ]
                ),
            ]
        );

        object?[] rootRow = [1L, "z-value", 55L, 3, "uri-space", 99L, "code-1"];
        object?[] omegaRow = [2001L, 1L, 0, "delta-1"];

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder:
            [
                new HydratedTableRows(rootTableModel, [rootRow]),
                new HydratedTableRows(omegaTableModel, [omegaRow]),
            ],
            referenceProjectionPlans: [referencePlan],
            descriptorProjectionSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: _gammaDescriptorPath,
                    Table: _rootTableName,
                    FkColumn: new DbColumnName("GammaDescriptor_DescriptorId"),
                    DescriptorResource: _gammaDescriptorResource
                ),
            ],
            descriptorUriLookup: new Dictionary<long, string> { [99L] = "uri://gamma" }
        );
    }

    [Test]
    public void It_should_emit_root_properties_in_compiled_json_path_order()
    {
        GetPropertyNames(_result.AsObject())
            .Should()
            .Equal("alpha", "alphaReference", "gammaDescriptor", "omega", "zeta");
    }

    [Test]
    public void It_should_emit_reference_fields_in_compiled_json_path_order()
    {
        GetPropertyNames(_result["alphaReference"]!.AsObject()).Should().Equal("code", "namespace");
    }

    private static string[] GetPropertyNames(JsonObject jsonObject)
    {
        List<string> propertyNames = [];

        foreach (var property in jsonObject)
        {
            propertyNames.Add(property.Key);
        }

        return [.. propertyNames];
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Empty_Collection_Under_Inlined_Object
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "Course");
    private static readonly DbTableName _authorTableName = new(_schema, "CourseAuthor");

    private static readonly JsonPathExpression _rootScope = new("$", []);
    private static readonly JsonPathExpression _authorsScope = new(
        "$.contentStandard.authors[*]",
        [
            new JsonPathSegment.Property("contentStandard"),
            new JsonPathSegment.Property("authors"),
            new JsonPathSegment.AnyArrayElement(),
        ]
    );

    private static readonly JsonPathExpression _courseCodePath = new(
        "$.courseCode",
        [new JsonPathSegment.Property("courseCode")]
    );

    private static readonly JsonPathExpression _authorPath = new(
        "$.contentStandard.authors[*].author",
        [
            new JsonPathSegment.Property("contentStandard"),
            new JsonPathSegment.Property("authors"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("author"),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        var rootTableModel = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_Course",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("CourseCode"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    false,
                    _courseCodePath,
                    null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        var authorTableModel = new DbTableModel(
            Table: _authorTableName,
            JsonScope: _authorsScope,
            Key: new TableKey(
                "PK_CourseAuthor",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Course_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Author"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    _authorPath,
                    null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("Course_DocumentId")],
                [new DbColumnName("Course_DocumentId")],
                []
            ),
        };

        object?[] rootRow = [1L, "ELA-1"];

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder:
            [
                new HydratedTableRows(rootTableModel, [rootRow]),
                new HydratedTableRows(authorTableModel, []),
            ],
            referenceProjectionPlans: [],
            descriptorProjectionSources: [],
            descriptorUriLookup: new Dictionary<long, string>()
        );
    }

    [Test]
    public void It_should_emit_courseCode()
    {
        _result["courseCode"]!.GetValue<string>().Should().Be("ELA-1");
    }

    [Test]
    public void It_should_not_emit_contentStandard_when_the_optional_collection_is_empty()
    {
        _result["contentStandard"].Should().BeNull();
    }
}

[TestFixture]
public class Given_DocumentReconstituter_With_Empty_Collection_Extension_Scope
{
    private JsonNode _result = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "Contact");
    private static readonly DbTableName _addressTableName = new(_schema, "ContactAddress");
    private static readonly DbTableName _extensionScopeTableName = new(_schema, "ContactAddressSample");

    private static readonly JsonPathExpression _rootScope = new("$", []);
    private static readonly JsonPathExpression _addressesScope = new(
        "$.addresses[*]",
        [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
    );
    private static readonly JsonPathExpression _extensionScope = new(
        "$.addresses[*]._ext.sample",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
        ]
    );

    private static readonly JsonPathExpression _contactIdPath = new(
        "$.contactId",
        [new JsonPathSegment.Property("contactId")]
    );

    private static readonly JsonPathExpression _cityPath = new(
        "$.addresses[*].city",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("city"),
        ]
    );

    private static readonly JsonPathExpression _isUrbanPath = new(
        "$.addresses[*]._ext.sample.isUrban",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
            new JsonPathSegment.Property("isUrban"),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        var rootTableModel = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_Contact",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("ContactId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    _contactIdPath,
                    null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        var addressTableModel = new DbTableModel(
            Table: _addressTableName,
            JsonScope: _addressesScope,
            Key: new TableKey(
                "PK_ContactAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("City"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    _cityPath,
                    null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                []
            ),
        };

        var extensionScopeTableModel = new DbTableModel(
            Table: _extensionScopeTableName,
            JsonScope: _extensionScope,
            Key: new TableKey(
                "PK_ContactAddressSample",
                [new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("BaseCollectionItemId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("IsUrban"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean),
                    true,
                    _isUrbanPath,
                    null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.CollectionExtensionScope,
                [new DbColumnName("BaseCollectionItemId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("BaseCollectionItemId")],
                []
            ),
        };

        object?[] rootRow = [1L, 123];
        object?[] addressRow = [1L, 2001L, 0, "Austin"];
        object?[] extensionScopeRow = [1L, 2001L, null];

        _result = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder:
            [
                new HydratedTableRows(rootTableModel, [rootRow]),
                new HydratedTableRows(addressTableModel, [addressRow]),
                new HydratedTableRows(extensionScopeTableModel, [extensionScopeRow]),
            ],
            referenceProjectionPlans: [],
            descriptorProjectionSources: [],
            descriptorUriLookup: new Dictionary<long, string>()
        );
    }

    [Test]
    public void It_should_emit_the_address()
    {
        _result["addresses"]![0]!["city"]!.GetValue<string>().Should().Be("Austin");
    }

    [Test]
    public void It_should_not_emit_an_empty_extension_scope()
    {
        _result["addresses"]![0]!["_ext"].Should().BeNull();
    }
}
