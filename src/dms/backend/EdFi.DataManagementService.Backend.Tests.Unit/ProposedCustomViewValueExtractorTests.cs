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

        var result = ProposedCustomViewValueExtractor.Extract([ProposedCheck()], rootRow);

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

        var result = ProposedCustomViewValueExtractor.Extract([ProposedCheck()], rootRow);

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

        var result = ProposedCustomViewValueExtractor.Extract([ProposedCheck()], rootRow);

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

        var result = ProposedCustomViewValueExtractor.Extract([storedCheck], rootRow);

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

        var result = ProposedCustomViewValueExtractor.Extract([mismatched], rootRow);

        result.Should().BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_returns_invalid_when_no_checks_are_supplied()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(9182L));

        var result = ProposedCustomViewValueExtractor.Extract([], rootRow);

        result.Should().BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>();
    }

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
