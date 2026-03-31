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

        act.Should().Throw<InvalidOperationException>().WithMessage("*RootScopeLocatorColumns is empty*");
    }
}
