// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationPeoplePathValidation
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly DbColumnName _studentDocumentIdColumn = new("Student_DocumentId");
    private static readonly DbTableName _rootTable = Table("StudentSchoolAssociation");
    private static readonly DbTableName _studentTable = Table("Student");
    private static readonly DbTableName _courseTranscriptTable = Table("CourseTranscript");
    private static readonly DbTableName _studentAcademicRecordTable = Table("StudentAcademicRecord");

    [Test]
    public void It_returns_direct_root_person_document_id_column_when_subject_matches_path_root()
    {
        var metadata = CreateDirectRootMetadata();

        var result = RelationshipAuthorizationPeoplePathValidation.GetDirectRootPersonDocumentIdColumn(
            _rootTable,
            _rootTable,
            _studentDocumentIdColumn,
            metadata,
            "query root table"
        );

        result.Should().Be(_studentDocumentIdColumn);
    }

    [TestCase("OtherAssociation", "Student_DocumentId")]
    [TestCase("StudentSchoolAssociation", "Other_DocumentId")]
    public void It_rejects_direct_root_person_path_when_subject_column_does_not_match_path_root(
        string subjectTableName,
        string subjectColumnName
    )
    {
        var subjectTable = Table(subjectTableName);
        var subjectColumn = Column(subjectColumnName);
        var metadata = CreateDirectRootMetadata();

        Action act = () =>
            RelationshipAuthorizationPeoplePathValidation.GetDirectRootPersonDocumentIdColumn(
                _rootTable,
                subjectTable,
                subjectColumn,
                metadata,
                "query root table"
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception
            .Message.Should()
            .ContainAll(
                "People authorization subject column",
                $"{subjectTable}.{subjectColumn}",
                $"{_rootTable}.{_studentDocumentIdColumn}",
                "path root column"
            );
    }

    [Test]
    public void It_accepts_valid_transitive_person_path()
    {
        Action act = () =>
            RelationshipAuthorizationPeoplePathValidation.ValidateTransitivePersonPath(
                _courseTranscriptTable,
                _studentAcademicRecordTable,
                _studentDocumentIdColumn,
                CreateValidTransitivePath()
            );

        act.Should().NotThrow();
    }

    [Test]
    public void It_rejects_transitive_path_when_first_source_table_does_not_match_root()
    {
        var pathSteps = CreateValidTransitivePath();
        pathSteps[0] = pathSteps[0] with { SourceTable = Table("OtherTranscript") };

        Action act = () =>
            RelationshipAuthorizationPeoplePathValidation.ValidateTransitivePersonPath(
                _courseTranscriptTable,
                _studentAcademicRecordTable,
                _studentDocumentIdColumn,
                pathSteps
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception
            .Message.Should()
            .ContainAll(
                "Transitive People authorization path step 0 source table",
                "edfi.OtherTranscript",
                $"expected table '{_courseTranscriptTable}'"
            );
    }

    [Test]
    public void It_rejects_transitive_path_when_nonterminal_step_is_missing_target_table()
    {
        var pathSteps = CreateValidTransitivePath();
        pathSteps[0] = pathSteps[0] with { TargetTable = null };

        Action act = () =>
            RelationshipAuthorizationPeoplePathValidation.ValidateTransitivePersonPath(
                _courseTranscriptTable,
                _studentAcademicRecordTable,
                _studentDocumentIdColumn,
                pathSteps
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Transitive People authorization path step 0 is missing a target table.");
    }

    [Test]
    public void It_rejects_transitive_path_when_terminal_source_table_does_not_match_expected_table()
    {
        var pathSteps = new List<ColumnPathStep>
        {
            new(Table("OtherAcademicRecord"), _studentDocumentIdColumn, _studentTable, _documentIdColumn),
        };

        Action act = () =>
            RelationshipAuthorizationPeoplePathValidation.ValidateTransitivePersonPath(
                _studentAcademicRecordTable,
                _studentAcademicRecordTable,
                _studentDocumentIdColumn,
                pathSteps
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception
            .Message.Should()
            .ContainAll(
                "Transitive People authorization terminal source table",
                "edfi.OtherAcademicRecord",
                $"expected table '{_studentAcademicRecordTable}'"
            );
    }

    [TestCase("OtherAcademicRecord", "Student_DocumentId")]
    [TestCase("StudentAcademicRecord", "Other_DocumentId")]
    public void It_rejects_transitive_path_when_subject_column_does_not_match_terminal_path_column(
        string subjectTableName,
        string subjectColumnName
    )
    {
        var subjectTable = Table(subjectTableName);
        var subjectColumn = Column(subjectColumnName);

        Action act = () =>
            RelationshipAuthorizationPeoplePathValidation.ValidateTransitivePersonPath(
                _courseTranscriptTable,
                subjectTable,
                subjectColumn,
                CreateValidTransitivePath()
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception
            .Message.Should()
            .ContainAll(
                "People authorization subject column",
                $"{subjectTable}.{subjectColumn}",
                $"{_studentAcademicRecordTable}.{_studentDocumentIdColumn}",
                "transitive terminal path column"
            );
    }

    private static RelationshipAuthorizationPersonSubjectMetadata CreateDirectRootMetadata() =>
        new(
            RelationshipAuthorizationPersonKind.Student,
            new RelationshipAuthorizationPersonSubjectPath(
                RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                [new ColumnPathStep(_rootTable, _studentDocumentIdColumn, _studentTable, _documentIdColumn)]
            ),
            new RelationshipAuthorizationPersonStoredAnchor(_rootTable, _documentIdColumn),
            null
        );

    private static List<ColumnPathStep> CreateValidTransitivePath() =>
        [
            new(
                _courseTranscriptTable,
                new DbColumnName("StudentAcademicRecord_DocumentId"),
                _studentAcademicRecordTable,
                _documentIdColumn
            ),
            new(_studentAcademicRecordTable, _studentDocumentIdColumn, _studentTable, _documentIdColumn),
        ];

    private static DbTableName Table(string name) => new(_edfiSchema, name);

    private static DbColumnName Column(string name) => new(name);
}
