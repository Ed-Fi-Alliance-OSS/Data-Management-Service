// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_a_proposed_custom_view_value_extractor
{
    private static readonly DbTableName _rootTable = new(new DbSchemaName("edfi"), "CourseTranscript");
    private static readonly DbTableName _otherTable = new(
        new DbSchemaName("edfi"),
        "StudentSchoolAssociation"
    );
    private static readonly DbColumnName _basisColumn = new("StudentDocumentId");

    private static SingleRecordCustomViewAuthorizationCheckSpec ProposedCheck(
        int index = 3,
        DbColumnName? column = null,
        DbTableName? rootTable = null,
        DbTableName? bindingTable = null
    ) =>
        CheckWithTarget(
            index,
            CustomViewAuthorizationCheckValueSource.Proposed,
            new CustomViewAuthorizationCheckTarget.Proposed(
                rootTable ?? _rootTable,
                new CustomViewAuthorizationProposedValueBinding(
                    bindingTable ?? rootTable ?? _rootTable,
                    column ?? _basisColumn,
                    "StudentDocumentId",
                    "cvBasis"
                )
            )
        );

    private static SingleRecordCustomViewAuthorizationCheckSpec SelfBasisCheck(
        int index = 4,
        DbTableName? rootTable = null
    ) =>
        CheckWithTarget(
            index,
            CustomViewAuthorizationCheckValueSource.Proposed,
            new CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable(rootTable ?? _rootTable)
        );

    private static SingleRecordCustomViewAuthorizationCheckSpec CheckWithTarget(
        int index,
        CustomViewAuthorizationCheckValueSource valueSource,
        CustomViewAuthorizationCheckTarget target
    ) =>
        new(
            new ConfiguredAuthorizationStrategy("StudentWithCTECourseEnrollments", 0),
            index,
            valueSource,
            new DbTableName(new DbSchemaName("auth"), "StudentWithCTECourseEnrollments"),
            new DbColumnName("DocumentId"),
            [new ColumnPathStep(_rootTable, _basisColumn, null, null)],
            target,
            new QualifiedResourceName("Ed-Fi", "Student"),
            ["StudentUniqueId"],
            "You may need a Student with CTE Course Enrollments."
        );

    [Test]
    public void It_extracts_the_basis_document_id_from_the_finalized_root_row_binding()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));

        var result = ProposedCustomViewValueExtractor.Extract(
            [ProposedCheck()],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        var work = result.Should().BeOfType<ProposedCustomViewExtractionResult.Ready>().Which.Work;
        work.SelfBasisChecks.Should().BeEmpty();
        work.SqlValues.Should().ContainSingle();
        work.SqlValues[0].Check.Index.Should().Be(3);
        work.SqlValues[0].BasisValue.Should().Be(9182L);
    }

    [Test]
    public void It_extracts_a_null_basis_value_when_the_finalized_reference_is_null()
    {
        // A nullable reference the client omitted. The §2.8 proposed-value-missing decision belongs to the
        // SQL, so extraction reports the null rather than treating it as a planning defect.
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(null));

        var result = ProposedCustomViewValueExtractor.Extract(
            [ProposedCheck()],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result
            .Should()
            .BeOfType<ProposedCustomViewExtractionResult.Ready>()
            .Which.Work.SqlValues[0]
            .BasisValue.Should()
            .BeNull();
    }

    [Test]
    public void It_extracts_a_null_basis_value_when_the_finalized_reference_is_db_null()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(DBNull.Value));

        var result = ProposedCustomViewValueExtractor.Extract(
            [ProposedCheck()],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result
            .Should()
            .BeOfType<ProposedCustomViewExtractionResult.Ready>()
            .Which.Work.SqlValues[0]
            .BasisValue.Should()
            .BeNull();
    }

    [Test]
    public void It_preserves_request_wide_indexes_across_a_slice_that_starts_above_zero()
    {
        // The proposed slice follows the stored one, so its first index is not zero. Execution maps cv1
        // payloads against the full planned list, which only works if these indexes survive extraction.
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));

        var result = ProposedCustomViewValueExtractor.Extract(
            [ProposedCheck(index: 2), ProposedCheck(index: 3)],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result
            .Should()
            .BeOfType<ProposedCustomViewExtractionResult.Ready>()
            .Which.Work.SqlValues.Select(value => value.Check.Index)
            .Should()
            .Equal(2, 3);
    }

    [Test]
    public void It_separates_self_basis_checks_from_the_checks_sql_decides()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));

        var result = ProposedCustomViewValueExtractor.Extract(
            [ProposedCheck(index: 2), SelfBasisCheck(index: 3)],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        var work = result.Should().BeOfType<ProposedCustomViewExtractionResult.Ready>().Which.Work;
        work.SqlValues.Select(value => value.Check.Index).Should().Equal(2);
        work.SelfBasisChecks.Select(check => check.Index).Should().Equal(3);
    }

    [Test]
    public void It_returns_invalid_when_no_root_binding_matches_the_basis_column()
    {
        // Failing closed is required: skipping the check would serve a write the strategy restricts.
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));

        var result = ProposedCustomViewValueExtractor.Extract(
            [ProposedCheck(column: new DbColumnName("NotAColumn"))],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result
            .Should()
            .BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>()
            .Which.FailureMessage.Should()
            .Contain("NotAColumn");
    }

    [Test]
    public void It_returns_invalid_when_a_check_targets_a_different_root_table()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));

        var result = ProposedCustomViewValueExtractor.Extract(
            [ProposedCheck(rootTable: _otherTable)],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result.Should().BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_returns_invalid_when_the_binding_names_a_foreign_table_holding_a_root_column_name()
    {
        // The target root table is correct but the binding's table is not, and the bound column name does
        // exist on the root. Matching on the column alone would read the root's value as though it were the
        // foreign table's, so the mismatch has to fail closed on its own.
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));

        var result = ProposedCustomViewValueExtractor.Extract(
            [ProposedCheck(bindingTable: _otherTable)],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result
            .Should()
            .BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>()
            .Which.FailureMessage.Should()
            .Contain("StudentSchoolAssociation");
    }

    [Test]
    public void It_returns_invalid_when_a_self_basis_check_targets_a_different_root_table()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));

        var result = ProposedCustomViewValueExtractor.Extract(
            [SelfBasisCheck(rootTable: _otherTable)],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result.Should().BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_returns_invalid_when_a_check_is_not_a_proposed_value_source()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));
        var storedCheck = CheckWithTarget(
            0,
            CustomViewAuthorizationCheckValueSource.Stored,
            new CustomViewAuthorizationCheckTarget.Stored(_rootTable, new DbColumnName("DocumentId"))
        );

        var result = ProposedCustomViewValueExtractor.Extract(
            [storedCheck],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result.Should().BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_returns_invalid_when_a_proposed_check_carries_a_stored_target()
    {
        // Value source and target are separate fields, so a plan can disagree with itself. That disagreement
        // is a configuration defect, not a denial.
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));
        var mismatched = CheckWithTarget(
            3,
            CustomViewAuthorizationCheckValueSource.Proposed,
            new CustomViewAuthorizationCheckTarget.Stored(_rootTable, new DbColumnName("DocumentId"))
        );

        var result = ProposedCustomViewValueExtractor.Extract(
            [mismatched],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result.Should().BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_returns_invalid_when_no_checks_are_supplied()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));

        var result = ProposedCustomViewValueExtractor.Extract(
            [],
            CreateWritePlan(rootRow.TableWritePlan),
            rootRow
        );

        result.Should().BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_marks_only_a_null_document_basis_whose_submitted_reference_was_deferred()
    {
        var writePlan = CreateReferenceBasisWritePlan();
        var nullRow = CreateReferenceBasisRootRow(writePlan, studentDocumentId: null, descriptorId: null);
        var resolvedRow = CreateReferenceBasisRootRow(writePlan, studentDocumentId: 9182L, descriptorId: 7);
        SingleRecordCustomViewAuthorizationCheckSpec[] checks = [ProposedCheck(3), DescriptorCheck(5)];

        var unresolved = ExtractValues(checks, writePlan, nullRow, "$.studentReference");
        var omitted = ExtractValues(checks, writePlan, nullRow);
        var resolved = ExtractValues(checks, writePlan, resolvedRow, "$.studentReference");
        var unrelated = ExtractValues(checks, writePlan, nullRow, "$.items[0].studentReference");

        unresolved
            .Select(static value =>
                (value.Check.Index, value.BasisValue, value.IsNullBecauseDeferredMissingReference)
            )
            .Should()
            .Equal((3, (object?)null, true), (5, (object?)null, false));
        omitted.Should().OnlyContain(static value => !value.IsNullBecauseDeferredMissingReference);
        resolved.Should().OnlyContain(static value => !value.IsNullBecauseDeferredMissingReference);
        resolved.Select(static value => value.BasisValue).Should().Equal(9182L, 7);
        unrelated.Should().OnlyContain(static value => !value.IsNullBecauseDeferredMissingReference);
    }

    [Test]
    public void It_rejects_invalid_basis_reference_association_metadata()
    {
        var writePlan = CreateReferenceBasisWritePlan(studentReferenceBindingIndex: 4);

        var result = ProposedCustomViewValueExtractor.Extract(
            [ProposedCheck(3)],
            writePlan,
            CreateReferenceBasisRootRow(writePlan, studentDocumentId: null, descriptorId: null),
            new HashSet<string>(["$.studentReference"], StringComparer.Ordinal)
        );

        result
            .Should()
            .BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>()
            .Which.FailureMessage.Should()
            .Contain("document-reference binding index 4");
    }

    private static IReadOnlyList<ProposedCustomViewRuntimeValue> ExtractValues(
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks,
        ResourceWritePlan writePlan,
        RootWriteRowBuffer rootRow,
        params string[] deferredPaths
    ) =>
        ProposedCustomViewValueExtractor
            .Extract(checks, writePlan, rootRow, new HashSet<string>(deferredPaths, StringComparer.Ordinal))
            .Should()
            .BeOfType<ProposedCustomViewExtractionResult.Ready>()
            .Subject.Work.SqlValues;

    private static readonly DbColumnName _descriptorColumn = new("GradeLevelDescriptorId");

    private static SingleRecordCustomViewAuthorizationCheckSpec DescriptorCheck(int index) =>
        CheckWithTarget(
            index,
            CustomViewAuthorizationCheckValueSource.Proposed,
            new CustomViewAuthorizationCheckTarget.Proposed(
                _rootTable,
                new CustomViewAuthorizationProposedValueBinding(
                    _rootTable,
                    _descriptorColumn,
                    "GradeLevelDescriptorId",
                    "cvDescriptorBasis",
                    ScalarKind.Int32
                )
            )
        );

    /// <summary>
    /// A root table whose document basis is a resolved <c>studentReference</c> FK and whose descriptor basis
    /// is a compact descriptor key, as the relational model binds them.
    /// </summary>
    private static ResourceWritePlan CreateReferenceBasisWritePlan(int studentReferenceBindingIndex = 0)
    {
        var studentReferencePath = new JsonPathExpression(
            "$.studentReference",
            [new JsonPathSegment.Property("studentReference")]
        );
        DbColumnModel[] columns =
        [
            new(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                null,
                false,
                null,
                null,
                new ColumnStorage.Stored()
            ),
            new(
                _basisColumn,
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                true,
                null,
                null,
                new ColumnStorage.Stored()
            ),
            new(
                _descriptorColumn,
                ColumnKind.DescriptorFk,
                new RelationalScalarType(ScalarKind.Int32),
                true,
                new JsonPathExpression("$.gradeLevelDescriptor", []),
                null,
                new ColumnStorage.Stored()
            ),
        ];
        var tableModel = new DbTableModel(
            _rootTable,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_CourseTranscript",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columns,
            []
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
        var tablePlan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"CourseTranscript\" values (...)",
            UpdateSql: "update edfi.\"CourseTranscript\" set ...",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(columns[0], new WriteValueSource.DocumentId(), "DocumentId"),
                new WriteColumnBinding(
                    columns[1],
                    new WriteValueSource.DocumentReference(studentReferenceBindingIndex),
                    "StudentDocumentId"
                ),
                new WriteColumnBinding(
                    columns[2],
                    new WriteValueSource.DescriptorReference(
                        new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"),
                        new JsonPathExpression("$.gradeLevelDescriptor", [])
                    ),
                    "GradeLevelDescriptorId"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
                new DbSchemaName("edfi"),
                ResourceStorageKind.RelationalTables,
                tableModel,
                [tableModel],
                [
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        studentReferencePath,
                        _rootTable,
                        _basisColumn,
                        new QualifiedResourceName("Ed-Fi", "Student"),
                        []
                    ),
                ],
                []
            ),
            [tablePlan]
        );
    }

    private static RootWriteRowBuffer CreateReferenceBasisRootRow(
        ResourceWritePlan writePlan,
        long? studentDocumentId,
        int? descriptorId
    ) =>
        new(
            writePlan.TablePlansInDependencyOrder[0],
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(studentDocumentId),
                new FlattenedWriteValue.Literal(descriptorId),
            ]
        );

    private static ResourceWritePlan CreateWritePlan(TableWritePlan rootPlan) =>
        new(
            new RelationalResourceModel(
                new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
                new DbSchemaName("edfi"),
                ResourceStorageKind.RelationalTables,
                rootPlan.TableModel,
                [rootPlan.TableModel],
                [],
                []
            ),
            [rootPlan]
        );

    private static RootWriteRowBuffer CreateRootRow(FlattenedWriteValue basisValue)
    {
        var tableModel = new DbTableModel(
            _rootTable,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_CourseTranscript",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    _basisColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    true,
                    new JsonPathExpression("$.studentReference.studentUniqueId", []),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
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

        var writePlan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"CourseTranscript\" values (@DocumentId, @StudentDocumentId)",
            UpdateSql: "update edfi.\"CourseTranscript\" set \"StudentDocumentId\" = @StudentDocumentId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.studentReference.studentUniqueId", []),
                        new RelationalScalarType(ScalarKind.Int64)
                    ),
                    "StudentDocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new RootWriteRowBuffer(
            writePlan,
            [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, basisValue]
        );
    }
}
