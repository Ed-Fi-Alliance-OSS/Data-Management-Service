// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_ReferenceIdentityProjector_With_Concrete_Reference
{
    private ReferenceProjectionResult _result = null!;

    private static readonly JsonPathExpression _schoolReferencePath = new(
        "$.schoolReference",
        [new JsonPathSegment.Property("schoolReference")]
    );

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolReference.schoolId",
        [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
    );

    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    [SetUp]
    public void SetUp()
    {
        // Row buffer: [DocumentId=1, School_DocumentId=10, School_SchoolId=255901]
        object?[] row = [1L, 10L, 255901L];

        var binding = new ReferenceIdentityProjectionBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: _schoolReferencePath,
            TargetResource: _schoolResource,
            FkColumnOrdinal: 1,
            IdentityFieldOrdinalsInOrder:
            [
                new ReferenceIdentityProjectionFieldOrdinal(_schoolIdPath, ColumnOrdinal: 2),
            ]
        );

        _result = ReferenceIdentityProjector.Project(row, binding);
    }

    [Test]
    public void It_should_return_a_present_result()
    {
        _result.Should().BeOfType<ReferenceProjectionResult.Present>();
    }

    [Test]
    public void It_should_emit_the_correct_reference_object_path()
    {
        var present = (ReferenceProjectionResult.Present)_result;
        present.ReferenceObjectPath.Canonical.Should().Be("$.schoolReference");
    }

    [Test]
    public void It_should_emit_the_correct_target_resource()
    {
        var present = (ReferenceProjectionResult.Present)_result;
        present.TargetResource.Should().Be(_schoolResource);
    }

    [Test]
    public void It_should_mark_identity_component()
    {
        var present = (ReferenceProjectionResult.Present)_result;
        present.IsIdentityComponent.Should().BeTrue();
    }

    [Test]
    public void It_should_emit_the_identity_field_with_correct_value()
    {
        var present = (ReferenceProjectionResult.Present)_result;
        present.FieldsInOrder.Should().HaveCount(1);

        var field = present.FieldsInOrder.Single(f =>
            f.ReferenceJsonPath.Canonical == "$.schoolReference.schoolId"
        );
        field.Value.Should().Be(255901L);
    }
}

[TestFixture]
public class Given_ReferenceIdentityProjector_With_Abstract_Reference
{
    private ReferenceProjectionResult _result = null!;

    private static readonly JsonPathExpression _educationOrgReferencePath = new(
        "$.educationOrganizationReference",
        [new JsonPathSegment.Property("educationOrganizationReference")]
    );

    private static readonly JsonPathExpression _educationOrgIdPath = new(
        "$.educationOrganizationReference.educationOrganizationId",
        [
            new JsonPathSegment.Property("educationOrganizationReference"),
            new JsonPathSegment.Property("educationOrganizationId"),
        ]
    );

    private static readonly QualifiedResourceName _abstractResource = new("Ed-Fi", "EducationOrganization");

    [SetUp]
    public void SetUp()
    {
        // Row buffer: [DocumentId=1, EdOrg_DocumentId=20, EdOrg_EducationOrganizationId=100]
        object?[] row = [1L, 20L, 100L];

        var binding = new ReferenceIdentityProjectionBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: _educationOrgReferencePath,
            TargetResource: _abstractResource,
            FkColumnOrdinal: 1,
            IdentityFieldOrdinalsInOrder:
            [
                new ReferenceIdentityProjectionFieldOrdinal(_educationOrgIdPath, ColumnOrdinal: 2),
            ]
        );

        _result = ReferenceIdentityProjector.Project(row, binding);
    }

    [Test]
    public void It_should_return_a_present_result()
    {
        _result.Should().BeOfType<ReferenceProjectionResult.Present>();
    }

    [Test]
    public void It_should_emit_the_abstract_target_resource()
    {
        var present = (ReferenceProjectionResult.Present)_result;
        present.TargetResource.Should().Be(_abstractResource);
    }

    [Test]
    public void It_should_emit_the_identity_field_with_correct_value()
    {
        var present = (ReferenceProjectionResult.Present)_result;
        var field = present.FieldsInOrder.Single(f =>
            f.ReferenceJsonPath.Canonical == "$.educationOrganizationReference.educationOrganizationId"
        );
        field.Value.Should().Be(100L);
    }
}

[TestFixture]
public class Given_ReferenceIdentityProjector_With_Null_Fk
{
    private ReferenceProjectionResult _result = null!;

    [SetUp]
    public void SetUp()
    {
        // Row buffer: [DocumentId=1, School_DocumentId=NULL, School_SchoolId=NULL]
        object?[] row = [1L, null, null];

        var binding = new ReferenceIdentityProjectionBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: new("$.schoolReference", [new JsonPathSegment.Property("schoolReference")]),
            TargetResource: new("Ed-Fi", "School"),
            FkColumnOrdinal: 1,
            IdentityFieldOrdinalsInOrder:
            [
                new ReferenceIdentityProjectionFieldOrdinal(
                    new(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    ColumnOrdinal: 2
                ),
            ]
        );

        _result = ReferenceIdentityProjector.Project(row, binding);
    }

    [Test]
    public void It_should_return_an_absent_result()
    {
        _result.Should().BeOfType<ReferenceProjectionResult.Absent>();
    }
}

[TestFixture]
public class Given_ReferenceIdentityProjector_With_Multiple_References_On_Same_Row
{
    private ReferenceProjectionResult _schoolResult = null!;
    private ReferenceProjectionResult _calendarResult = null!;

    private static readonly JsonPathExpression _schoolReferencePath = new(
        "$.schoolReference",
        [new JsonPathSegment.Property("schoolReference")]
    );

    private static readonly JsonPathExpression _calendarReferencePath = new(
        "$.calendarReference",
        [new JsonPathSegment.Property("calendarReference")]
    );

    [SetUp]
    public void SetUp()
    {
        // Row buffer: [DocumentId=1, School_DocumentId=10, School_SchoolId=255901, Calendar_DocumentId=20, Calendar_CalendarCode="CAL1"]
        object?[] row = [1L, 10L, 255901L, 20L, "CAL1"];

        var schoolBinding = new ReferenceIdentityProjectionBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: _schoolReferencePath,
            TargetResource: new("Ed-Fi", "School"),
            FkColumnOrdinal: 1,
            IdentityFieldOrdinalsInOrder:
            [
                new ReferenceIdentityProjectionFieldOrdinal(
                    new(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    ColumnOrdinal: 2
                ),
            ]
        );

        var calendarBinding = new ReferenceIdentityProjectionBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: _calendarReferencePath,
            TargetResource: new("Ed-Fi", "Calendar"),
            FkColumnOrdinal: 3,
            IdentityFieldOrdinalsInOrder:
            [
                new ReferenceIdentityProjectionFieldOrdinal(
                    new(
                        "$.calendarReference.calendarCode",
                        [
                            new JsonPathSegment.Property("calendarReference"),
                            new JsonPathSegment.Property("calendarCode"),
                        ]
                    ),
                    ColumnOrdinal: 4
                ),
            ]
        );

        _schoolResult = ReferenceIdentityProjector.Project(row, schoolBinding);
        _calendarResult = ReferenceIdentityProjector.Project(row, calendarBinding);
    }

    [Test]
    public void It_should_project_the_school_reference_independently()
    {
        _schoolResult.Should().BeOfType<ReferenceProjectionResult.Present>();

        var present = (ReferenceProjectionResult.Present)_schoolResult;
        present.ReferenceObjectPath.Canonical.Should().Be("$.schoolReference");
        present
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.schoolReference.schoolId")
            .Value.Should()
            .Be(255901L);
    }

    [Test]
    public void It_should_project_the_calendar_reference_independently()
    {
        _calendarResult.Should().BeOfType<ReferenceProjectionResult.Present>();

        var present = (ReferenceProjectionResult.Present)_calendarResult;
        present.ReferenceObjectPath.Canonical.Should().Be("$.calendarReference");
        present
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.calendarReference.calendarCode")
            .Value.Should()
            .Be("CAL1");
    }
}

[TestFixture]
public class Given_ReferenceIdentityProjector_With_Multiple_Identity_Fields
{
    private ReferenceProjectionResult _result = null!;

    private static readonly JsonPathExpression _sessionReferencePath = new(
        "$.sessionReference",
        [new JsonPathSegment.Property("sessionReference")]
    );

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.sessionReference.schoolId",
        [new JsonPathSegment.Property("sessionReference"), new JsonPathSegment.Property("schoolId")]
    );

    private static readonly JsonPathExpression _schoolYearPath = new(
        "$.sessionReference.schoolYear",
        [new JsonPathSegment.Property("sessionReference"), new JsonPathSegment.Property("schoolYear")]
    );

    private static readonly JsonPathExpression _sessionNamePath = new(
        "$.sessionReference.sessionName",
        [new JsonPathSegment.Property("sessionReference"), new JsonPathSegment.Property("sessionName")]
    );

    [SetUp]
    public void SetUp()
    {
        // Row buffer: [DocumentId=1, Session_DocumentId=30, Session_SchoolId=255901, Session_SchoolYear=2025, Session_SessionName="Fall"]
        object?[] row = [1L, 30L, 255901L, 2025, "Fall"];

        var binding = new ReferenceIdentityProjectionBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: _sessionReferencePath,
            TargetResource: new("Ed-Fi", "Session"),
            FkColumnOrdinal: 1,
            IdentityFieldOrdinalsInOrder:
            [
                new ReferenceIdentityProjectionFieldOrdinal(_schoolIdPath, ColumnOrdinal: 2),
                new ReferenceIdentityProjectionFieldOrdinal(_schoolYearPath, ColumnOrdinal: 3),
                new ReferenceIdentityProjectionFieldOrdinal(_sessionNamePath, ColumnOrdinal: 4),
            ]
        );

        _result = ReferenceIdentityProjector.Project(row, binding);
    }

    [Test]
    public void It_should_return_a_present_result()
    {
        _result.Should().BeOfType<ReferenceProjectionResult.Present>();
    }

    [Test]
    public void It_should_emit_all_identity_fields_in_order()
    {
        var present = (ReferenceProjectionResult.Present)_result;
        present.FieldsInOrder.Should().HaveCount(3);

        present.FieldsInOrder[0].ReferenceJsonPath.Canonical.Should().Be("$.sessionReference.schoolId");
        present.FieldsInOrder[0].Value.Should().Be(255901L);

        present.FieldsInOrder[1].ReferenceJsonPath.Canonical.Should().Be("$.sessionReference.schoolYear");
        present.FieldsInOrder[1].Value.Should().Be(2025);

        present.FieldsInOrder[2].ReferenceJsonPath.Canonical.Should().Be("$.sessionReference.sessionName");
        present.FieldsInOrder[2].Value.Should().Be("Fall");
    }
}

[TestFixture]
public class Given_ReferenceIdentityProjector_ProjectTable_With_Grouped_Results
{
    private IReadOnlyDictionary<long, IReadOnlyList<ReferenceProjectionResult.Present>> _results = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "StudentSchoolAssociation");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _schoolReferencePath = new(
        "$.schoolReference",
        [new JsonPathSegment.Property("schoolReference")]
    );

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolReference.schoolId",
        [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
    );

    [SetUp]
    public void SetUp()
    {
        // Columns: [0]=DocumentId, [1]=School_DocumentId, [2]=School_SchoolId
        var tableModel = new DbTableModel(
            _tableName,
            _rootScope,
            new TableKey(
                "PK_StudentSchoolAssociation",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
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
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: new("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    new DbColumnName("School_SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: _schoolIdPath,
                    TargetResource: null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var hydratedRows = new HydratedTableRows(
            tableModel,
            [
                // Document 1 — school reference present
                new object?[] { 1L, 10L, 255901L },
                // Document 2 — school reference absent
                new object?[] { 2L, null, null },
                // Document 3 — school reference present
                new object?[] { 3L, 20L, 255902L },
            ]
        );

        var projectionPlan = new ReferenceIdentityProjectionTablePlan(
            _tableName,
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: _schoolReferencePath,
                    TargetResource: new("Ed-Fi", "School"),
                    FkColumnOrdinal: 1,
                    IdentityFieldOrdinalsInOrder:
                    [
                        new ReferenceIdentityProjectionFieldOrdinal(_schoolIdPath, ColumnOrdinal: 2),
                    ]
                ),
            ]
        );

        _results = ReferenceIdentityProjector.ProjectTable(hydratedRows, projectionPlan);
    }

    [Test]
    public void It_should_group_present_results_by_document_id()
    {
        _results.Should().ContainKey(1L);
        _results.Should().ContainKey(3L);
    }

    [Test]
    public void It_should_omit_documents_with_absent_references()
    {
        _results.Should().NotContainKey(2L);
    }

    [Test]
    public void It_should_project_correct_values_for_document_1()
    {
        var projections = _results[1L];
        projections.Should().HaveCount(1);

        var present = projections.Single(p => p.ReferenceObjectPath.Canonical == "$.schoolReference");
        present
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.schoolReference.schoolId")
            .Value.Should()
            .Be(255901L);
    }

    [Test]
    public void It_should_project_correct_values_for_document_3()
    {
        var projections = _results[3L];
        projections.Should().HaveCount(1);

        var present = projections.Single(p => p.ReferenceObjectPath.Canonical == "$.schoolReference");
        present
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.schoolReference.schoolId")
            .Value.Should()
            .Be(255902L);
    }
}

[TestFixture]
public class Given_ReferenceIdentityProjector_ProjectTable_With_Empty_RootScopeLocatorColumns
{
    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "Orphan");

    [Test]
    public void It_should_throw_with_descriptive_message()
    {
        var tableModel = new DbTableModel(
            _tableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Orphan",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var hydratedRows = new HydratedTableRows(tableModel, [new object?[] { 1L }]);

        var projectionPlan = new ReferenceIdentityProjectionTablePlan(
            _tableName,
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: new(
                        "$.schoolReference",
                        [new JsonPathSegment.Property("schoolReference")]
                    ),
                    TargetResource: new("Ed-Fi", "School"),
                    FkColumnOrdinal: 0,
                    IdentityFieldOrdinalsInOrder: []
                ),
            ]
        );

        var act = () => ReferenceIdentityProjector.ProjectTable(hydratedRows, projectionPlan);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*expected exactly one explicit root-scope locator column*");
    }
}

[TestFixture]
public class Given_ProjectTable_With_Collection_Table
{
    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "SchoolAddress");

    [Test]
    public void It_should_throw_with_descriptive_message()
    {
        var tableModel = new DbTableModel(
            _tableName,
            new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                "PK_SchoolAddress",
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("StateAbbreviation_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: new("Ed-Fi", "StateAbbreviationDescriptor")
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var hydratedRows = new HydratedTableRows(tableModel, [new object?[] { 100L, 1L, 0, 50L }]);

        var projectionPlan = new ReferenceIdentityProjectionTablePlan(
            _tableName,
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: new(
                        "$.addresses[*].stateAbbreviationReference",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("stateAbbreviationReference"),
                        ]
                    ),
                    TargetResource: new("Ed-Fi", "StateAbbreviationDescriptor"),
                    FkColumnOrdinal: 3,
                    IdentityFieldOrdinalsInOrder: []
                ),
            ]
        );

        var act = () => ReferenceIdentityProjector.ProjectTable(hydratedRows, projectionPlan);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ProjectTable is only valid for root-scope tables*")
            .WithMessage("*Collection*")
            .WithMessage("*use Project() per-row*");
    }
}

[TestFixture]
public class Given_Project_With_Collection_Scope_Row
{
    private ReferenceProjectionResult _row0Result = null!;
    private ReferenceProjectionResult _row1Result = null!;
    private ReferenceProjectionResult _row2Result = null!;

    [SetUp]
    public void SetUp()
    {
        // Collection table rows with a reference binding.
        // Columns: [0]=CollectionItemId, [1]=School_DocumentId, [2]=Ordinal,
        //          [3]=StateAbbreviation_DocumentId, [4]=StateAbbreviation_CodeValue
        var binding = new ReferenceIdentityProjectionBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: new(
                "$.addresses[*].stateAbbreviationReference",
                [
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("stateAbbreviationReference"),
                ]
            ),
            TargetResource: new("Ed-Fi", "StateAbbreviationDescriptor"),
            FkColumnOrdinal: 3,
            IdentityFieldOrdinalsInOrder:
            [
                new ReferenceIdentityProjectionFieldOrdinal(
                    new(
                        "$.addresses[*].stateAbbreviationReference.codeValue",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("stateAbbreviationReference"),
                            new JsonPathSegment.Property("codeValue"),
                        ]
                    ),
                    ColumnOrdinal: 4
                ),
            ]
        );

        // Row 0: reference present (TX)
        object?[] row0 = [100L, 1L, 0, 50L, "TX"];
        // Row 1: reference absent (null FK)
        object?[] row1 = [101L, 1L, 1, null, null];
        // Row 2: reference present (NY) — same document, different collection item
        object?[] row2 = [102L, 1L, 2, 60L, "NY"];

        _row0Result = ReferenceIdentityProjector.Project(row0, binding);
        _row1Result = ReferenceIdentityProjector.Project(row1, binding);
        _row2Result = ReferenceIdentityProjector.Project(row2, binding);
    }

    [Test]
    public void It_should_project_the_first_row_reference()
    {
        _row0Result.Should().BeOfType<ReferenceProjectionResult.Present>();
        var present = (ReferenceProjectionResult.Present)_row0Result;
        present
            .FieldsInOrder.Single(f =>
                f.ReferenceJsonPath.Canonical == "$.addresses[*].stateAbbreviationReference.codeValue"
            )
            .Value.Should()
            .Be("TX");
    }

    [Test]
    public void It_should_return_absent_for_the_null_fk_row()
    {
        _row1Result.Should().BeOfType<ReferenceProjectionResult.Absent>();
    }

    [Test]
    public void It_should_project_the_third_row_independently()
    {
        _row2Result.Should().BeOfType<ReferenceProjectionResult.Present>();
        var present = (ReferenceProjectionResult.Present)_row2Result;
        present
            .FieldsInOrder.Single(f =>
                f.ReferenceJsonPath.Canonical == "$.addresses[*].stateAbbreviationReference.codeValue"
            )
            .Value.Should()
            .Be("NY");
    }
}

[TestFixture]
public class Given_ProjectPage_With_Root_Table_Projections
{
    private IReadOnlyDictionary<long, IReadOnlyList<ReferenceProjectionResult.Present>> _results = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "StudentSchoolAssociation");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _schoolReferencePath = new(
        "$.schoolReference",
        [new JsonPathSegment.Property("schoolReference")]
    );

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolReference.schoolId",
        [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
    );

    [SetUp]
    public void SetUp()
    {
        var rootTableModel = new DbTableModel(
            _rootTableName,
            _rootScope,
            new TableKey(
                "PK_StudentSchoolAssociation",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
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
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: new("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    new DbColumnName("School_SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: _schoolIdPath,
                    TargetResource: null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var hydratedRows = new HydratedTableRows(
            rootTableModel,
            [
                new object?[] { 1L, 10L, 255901L },
                new object?[] { 2L, null, null },
                new object?[] { 3L, 20L, 255902L },
            ]
        );

        var page = new HydratedPage(
            TotalCount: null,
            DocumentMetadata:
            [
                new DocumentMetadataRow(
                    1L,
                    Guid.NewGuid(),
                    1,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                ),
                new DocumentMetadataRow(
                    2L,
                    Guid.NewGuid(),
                    1,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                ),
                new DocumentMetadataRow(
                    3L,
                    Guid.NewGuid(),
                    1,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                ),
            ],
            TableRowsInDependencyOrder: [hydratedRows],
            DescriptorRowsInPlanOrder: []
        );

        var projectionPlan = new ReferenceIdentityProjectionTablePlan(
            _rootTableName,
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: _schoolReferencePath,
                    TargetResource: new("Ed-Fi", "School"),
                    FkColumnOrdinal: 1,
                    IdentityFieldOrdinalsInOrder:
                    [
                        new ReferenceIdentityProjectionFieldOrdinal(_schoolIdPath, ColumnOrdinal: 2),
                    ]
                ),
            ]
        );

        var resourceModel = new RelationalResourceModel(
            Resource: new("Ed-Fi", "StudentSchoolAssociation"),
            PhysicalSchema: _schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var plan = new ResourceReadPlan(
            Model: resourceModel,
            KeysetTable: new KeysetTableContract(
                new SqlRelationRef.TempTable("page"),
                new DbColumnName("DocumentId")
            ),
            TablePlansInDependencyOrder:
            [
                new TableReadPlan(rootTableModel, "SELECT * FROM edfi.StudentSchoolAssociation"),
            ],
            ReferenceIdentityProjectionPlansInDependencyOrder: [projectionPlan],
            DescriptorProjectionPlansInOrder: []
        );

        _results = ReferenceIdentityProjector.ProjectPage(page, plan);
    }

    [Test]
    public void It_should_return_projections_for_present_references()
    {
        _results.Should().ContainKey(1L);
        _results.Should().ContainKey(3L);
    }

    [Test]
    public void It_should_omit_documents_with_absent_references()
    {
        _results.Should().NotContainKey(2L);
    }

    [Test]
    public void It_should_project_correct_values()
    {
        var present = _results[1L].Single(p => p.ReferenceObjectPath.Canonical == "$.schoolReference");
        present
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.schoolReference.schoolId")
            .Value.Should()
            .Be(255901L);
    }
}

[TestFixture]
public class Given_ProjectPage_With_No_Projection_Plans
{
    private IReadOnlyDictionary<long, IReadOnlyList<ReferenceProjectionResult.Present>> _results = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "School");

    [SetUp]
    public void SetUp()
    {
        var rootTableModel = new DbTableModel(
            _rootTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var page = new HydratedPage(
            TotalCount: null,
            DocumentMetadata:
            [
                new DocumentMetadataRow(
                    1L,
                    Guid.NewGuid(),
                    1,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                ),
            ],
            TableRowsInDependencyOrder: [new HydratedTableRows(rootTableModel, [new object?[] { 1L }])],
            DescriptorRowsInPlanOrder: []
        );

        var resourceModel = new RelationalResourceModel(
            Resource: new("Ed-Fi", "School"),
            PhysicalSchema: _schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var plan = new ResourceReadPlan(
            Model: resourceModel,
            KeysetTable: new KeysetTableContract(
                new SqlRelationRef.TempTable("page"),
                new DbColumnName("DocumentId")
            ),
            TablePlansInDependencyOrder: [new TableReadPlan(rootTableModel, "SELECT * FROM edfi.School")],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );

        _results = ReferenceIdentityProjector.ProjectPage(page, plan);
    }

    [Test]
    public void It_should_return_empty_dictionary()
    {
        _results.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_ProjectPage_With_Mixed_Root_And_Collection_Plans
{
    private IReadOnlyDictionary<long, IReadOnlyList<ReferenceProjectionResult.Present>> _results = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "StudentSchoolAssociation");
    private static readonly DbTableName _collectionTableName = new(
        _schema,
        "StudentSchoolAssociationAddress"
    );

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _schoolReferencePath = new(
        "$.schoolReference",
        [new JsonPathSegment.Property("schoolReference")]
    );

    private static readonly JsonPathExpression _schoolIdPath = new(
        "$.schoolReference.schoolId",
        [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
    );

    [SetUp]
    public void SetUp()
    {
        // Root table with school reference
        var rootTableModel = new DbTableModel(
            _rootTableName,
            _rootScope,
            new TableKey(
                "PK_StudentSchoolAssociation",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
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
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: new("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    new DbColumnName("School_SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: _schoolIdPath,
                    TargetResource: null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        // Collection table with a reference (should be skipped by ProjectPage)
        var collectionTableModel = new DbTableModel(
            _collectionTableName,
            new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                "PK_StudentSchoolAssociationAddress",
                [
                    new DbKeyColumn(
                        new DbColumnName("StudentSchoolAssociation_DocumentId"),
                        ColumnKind.ParentKeyPart
                    ),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("StudentSchoolAssociation_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("State_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: new("Ed-Fi", "StateAbbreviationDescriptor")
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("StudentSchoolAssociation_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("StudentSchoolAssociation_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var rootHydratedRows = new HydratedTableRows(rootTableModel, [new object?[] { 1L, 10L, 255901L }]);

        var collectionHydratedRows = new HydratedTableRows(
            collectionTableModel,
            [new object?[] { 100L, 1L, 0, 50L }, new object?[] { 101L, 1L, 1, 60L }]
        );

        var page = new HydratedPage(
            TotalCount: null,
            DocumentMetadata:
            [
                new DocumentMetadataRow(
                    1L,
                    Guid.NewGuid(),
                    1,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                ),
            ],
            TableRowsInDependencyOrder: [rootHydratedRows, collectionHydratedRows],
            DescriptorRowsInPlanOrder: []
        );

        var rootProjectionPlan = new ReferenceIdentityProjectionTablePlan(
            _rootTableName,
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: _schoolReferencePath,
                    TargetResource: new("Ed-Fi", "School"),
                    FkColumnOrdinal: 1,
                    IdentityFieldOrdinalsInOrder:
                    [
                        new ReferenceIdentityProjectionFieldOrdinal(_schoolIdPath, ColumnOrdinal: 2),
                    ]
                ),
            ]
        );

        var collectionProjectionPlan = new ReferenceIdentityProjectionTablePlan(
            _collectionTableName,
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: new(
                        "$.addresses[*].stateAbbreviationReference",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("stateAbbreviationReference"),
                        ]
                    ),
                    TargetResource: new("Ed-Fi", "StateAbbreviationDescriptor"),
                    FkColumnOrdinal: 3,
                    IdentityFieldOrdinalsInOrder: []
                ),
            ]
        );

        var resourceModel = new RelationalResourceModel(
            Resource: new("Ed-Fi", "StudentSchoolAssociation"),
            PhysicalSchema: _schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel, collectionTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var plan = new ResourceReadPlan(
            Model: resourceModel,
            KeysetTable: new KeysetTableContract(
                new SqlRelationRef.TempTable("page"),
                new DbColumnName("DocumentId")
            ),
            TablePlansInDependencyOrder:
            [
                new TableReadPlan(rootTableModel, "SELECT 1"),
                new TableReadPlan(collectionTableModel, "SELECT 1"),
            ],
            ReferenceIdentityProjectionPlansInDependencyOrder: [rootProjectionPlan, collectionProjectionPlan],
            DescriptorProjectionPlansInOrder: []
        );

        _results = ReferenceIdentityProjector.ProjectPage(page, plan);
    }

    [Test]
    public void It_should_include_root_table_projections()
    {
        _results.Should().ContainKey(1L);
        var projections = _results[1L];
        projections.Should().ContainSingle(p => p.ReferenceObjectPath.Canonical == "$.schoolReference");
    }

    [Test]
    public void It_should_not_include_collection_table_projections()
    {
        var allProjections = _results.Values.SelectMany(v => v).ToList();
        allProjections
            .Should()
            .NotContain(p => p.ReferenceObjectPath.Canonical == "$.addresses[*].stateAbbreviationReference");
    }
}

[TestFixture]
public class Given_ReferenceIdentityProjector_With_NonNull_Fk_But_Null_Identity_Field
{
    [Test]
    public void It_should_throw_with_descriptive_message()
    {
        // Row buffer: FK is present (10L) but identity field is null
        object?[] row = [1L, 10L, null];

        var binding = new ReferenceIdentityProjectionBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: new("$.schoolReference", [new JsonPathSegment.Property("schoolReference")]),
            TargetResource: new("Ed-Fi", "School"),
            FkColumnOrdinal: 1,
            IdentityFieldOrdinalsInOrder:
            [
                new ReferenceIdentityProjectionFieldOrdinal(
                    new(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    ColumnOrdinal: 2
                ),
            ]
        );

        var act = () => ReferenceIdentityProjector.Project(row, binding);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Identity field at ordinal 2*")
            .WithMessage("*null*FK column is non-null*");
    }
}

[TestFixture]
public class Given_ProjectTable_With_RootExtension_Table
{
    private IReadOnlyDictionary<long, IReadOnlyList<ReferenceProjectionResult.Present>> _results = null!;

    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _tableName = new(_schema, "StudentSchoolAssociationExtension");

    private static readonly JsonPathExpression _rootScope = new("$", []);

    private static readonly JsonPathExpression _mentorReferencePath = new(
        "$.mentorReference",
        [new JsonPathSegment.Property("mentorReference")]
    );

    private static readonly JsonPathExpression _mentorIdPath = new(
        "$.mentorReference.staffUniqueId",
        [new JsonPathSegment.Property("mentorReference"), new JsonPathSegment.Property("staffUniqueId")]
    );

    [SetUp]
    public void SetUp()
    {
        // Columns: [0]=DocumentId, [1]=Mentor_DocumentId, [2]=Mentor_StaffUniqueId
        var tableModel = new DbTableModel(
            _tableName,
            _rootScope,
            new TableKey(
                "PK_StudentSchoolAssociationExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("Mentor_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: new("Ed-Fi", "Staff")
                ),
                new DbColumnModel(
                    new DbColumnName("Mentor_StaffUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                    IsNullable: true,
                    SourceJsonPath: _mentorIdPath,
                    TargetResource: null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var hydratedRows = new HydratedTableRows(
            tableModel,
            [new object?[] { 1L, 30L, "STAFF001" }, new object?[] { 2L, null, null }]
        );

        var projectionPlan = new ReferenceIdentityProjectionTablePlan(
            _tableName,
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: _mentorReferencePath,
                    TargetResource: new("Ed-Fi", "Staff"),
                    FkColumnOrdinal: 1,
                    IdentityFieldOrdinalsInOrder:
                    [
                        new ReferenceIdentityProjectionFieldOrdinal(_mentorIdPath, ColumnOrdinal: 2),
                    ]
                ),
            ]
        );

        _results = ReferenceIdentityProjector.ProjectTable(hydratedRows, projectionPlan);
    }

    [Test]
    public void It_should_project_present_reference_for_non_null_fk()
    {
        _results.Should().ContainKey(1L);

        var present = _results[1L].Single(p => p.ReferenceObjectPath.Canonical == "$.mentorReference");
        present
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.mentorReference.staffUniqueId")
            .Value.Should()
            .Be("STAFF001");
    }

    [Test]
    public void It_should_omit_absent_reference()
    {
        _results.Should().NotContainKey(2L);
    }
}
