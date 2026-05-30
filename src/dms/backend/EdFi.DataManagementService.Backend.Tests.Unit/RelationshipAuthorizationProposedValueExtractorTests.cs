// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationProposedValueExtractor
{
    [Test]
    public void It_maps_finalized_root_row_values_to_subject_runtime_values()
    {
        var rootPlan = CreateRootPlan();
        var rootRow = CreateRootRow(rootPlan, schoolId: 255901L, studentDocumentId: 98765L);

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(CreateProposedEdOrgCheckSpec(rootPlan)),
            rootRow,
            emittedAuth1Index: 4
        );

        var ready = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject;
        ready.RuntimeCheck.EmittedAuth1Index.Should().Be(4);
        ready
            .RuntimeCheck.Strategies.Should()
            .ContainSingle()
            .Which.Subjects.Should()
            .ContainSingle()
            .Which.RuntimeValue.Should()
            .Be(new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(255901L));
    }

    [Test]
    public void It_uses_profile_merged_hidden_stored_values_from_the_finalized_root_row()
    {
        var rootPlan = CreateRootPlan();
        var rootRow = CreateRootRow(rootPlan, schoolId: 255901L, studentDocumentId: 98765L);

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(CreateProposedDirectPeopleCheckSpec(rootPlan)),
            rootRow,
            emittedAuth1Index: 5
        );

        var runtimeSubject = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject.RuntimeCheck.Strategies.Should()
            .ContainSingle()
            .Subject.Subjects.Should()
            .ContainSingle()
            .Subject;
        runtimeSubject
            .RuntimeValue.Should()
            .Be(new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(98765L));
        runtimeSubject.Subject.PersonMetadata.Should().NotBeNull();
        runtimeSubject
            .Subject.PersonMetadata!.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.RootRow);
    }

    [Test]
    public void It_maps_existing_target_document_ids_for_self_people_subjects()
    {
        var rootPlan = CreateRootPlan();
        var rootRow = CreateRootRow(
            rootPlan,
            schoolId: 255901L,
            studentDocumentId: 98765L,
            rootDocumentId: FlattenedWriteValue.UnresolvedRootDocumentId.Instance
        );

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(CreateProposedSelfPeopleCheckSpec(rootPlan)),
            rootRow,
            emittedAuth1Index: 6,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                456789L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                ObservedContentVersion: 101L
            )
        );

        var runtimeSubject = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject.RuntimeCheck.Strategies.Should()
            .ContainSingle()
            .Subject.Subjects.Should()
            .ContainSingle()
            .Subject;
        runtimeSubject
            .RuntimeValue.Should()
            .Be(new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(456789L));
        runtimeSubject.Binding.Column.Value.Should().Be("DocumentId");
        runtimeSubject
            .Subject.PersonMetadata!.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId);
    }

    [Test]
    public void It_maps_transitive_people_values_as_first_hop_anchor_runtime_values()
    {
        var rootPlan = CreateRootPlan();
        var rootRow = CreateRootRow(rootPlan, schoolId: 255901L, studentDocumentId: 98765L);

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(CreateProposedTransitivePeopleCheckSpec(rootPlan)),
            rootRow,
            emittedAuth1Index: 7
        );

        var runtimeSubject = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject.RuntimeCheck.Strategies.Should()
            .ContainSingle()
            .Subject.Subjects.Should()
            .ContainSingle()
            .Subject;
        var runtimeValue = runtimeSubject
            .RuntimeValue.Should()
            .BeOfType<ProposedRelationshipAuthorizationRuntimeValue.TransitivePeopleFirstHopAnchorValue>()
            .Subject;
        runtimeValue.Value.Should().Be(255901L);
        runtimeValue.Binding.Should().Be(runtimeSubject.Binding);
        runtimeValue.Path.Kind.Should().Be(RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath);
        runtimeValue.Path.Steps.Should().HaveCount(2);
        runtimeSubject.Subject.Table.ToString().Should().Be("edfi.StudentSchoolAssociation");
        runtimeSubject.Subject.Column.Should().Be(AuthNames.StudentDocumentId);
    }

    [TestCaseSource(nameof(MissingValueCases))]
    public void It_maps_null_or_unresolved_finalized_values_to_null_runtime_parameters(
        FlattenedWriteValue missingValue
    )
    {
        var rootPlan = CreateRootPlan();
        var rootRow = CreateRootRow(
            rootPlan,
            schoolId: 255901L,
            studentDocumentId: 98765L,
            schoolIdValue: missingValue
        );

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(CreateProposedEdOrgCheckSpec(rootPlan)),
            rootRow,
            emittedAuth1Index: 8
        );

        result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject.RuntimeCheck.Strategies[0]
            .Subjects[0]
            .RuntimeValue.Should()
            .Be(new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(null));
    }

    [Test]
    public void It_rejects_existing_target_self_people_subjects_without_target_context()
    {
        var rootPlan = CreateRootPlan();
        var rootRow = CreateRootRow(rootPlan, schoolId: 255901L, studentDocumentId: 98765L);

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(CreateProposedSelfPeopleCheckSpec(rootPlan)),
            rootRow,
            emittedAuth1Index: 9
        );

        result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.InvalidAuthorizationPlan>()
            .Which.FailureMessage.Should()
            .Be(
                "Proposed relationship authorization binding '0:0' requires an existing target DocumentId, but no target context was provided."
            );
    }

    private static IEnumerable<TestCaseData> MissingValueCases()
    {
        yield return new TestCaseData(new FlattenedWriteValue.Literal(null)).SetName("null literal");
        yield return new TestCaseData(new FlattenedWriteValue.Literal(DBNull.Value)).SetName(
            "DBNull literal"
        );
        yield return new TestCaseData(FlattenedWriteValue.UnresolvedRootDocumentId.Instance).SetName(
            "unresolved root document id"
        );
    }

    private static RootWriteRowBuffer CreateRootRow(
        TableWritePlan rootPlan,
        long schoolId,
        long studentDocumentId,
        FlattenedWriteValue? rootDocumentId = null,
        FlattenedWriteValue? schoolIdValue = null
    ) =>
        new(
            rootPlan,
            [
                rootDocumentId ?? new FlattenedWriteValue.Literal(345L),
                schoolIdValue ?? new FlattenedWriteValue.Literal(schoolId),
                new FlattenedWriteValue.Literal(studentDocumentId),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );

    private static RelationshipAuthorizationResult.Authorized CreateAuthorized(
        params RelationshipAuthorizationCheckSpec[] checkSpecs
    ) =>
        new(
            checkSpecs,
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Pgsql,
                [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );

    private static RelationshipAuthorizationCheckSpec CreateProposedEdOrgCheckSpec(TableWritePlan rootPlan)
    {
        var binding = CreateBinding(rootPlan, "SchoolId", bindingIndex: 1);
        var subject = new RelationshipAuthorizationSubject(
            SchoolResource,
            RootTable,
            binding.Column,
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                RelationshipAuthorizationHierarchyDirection.Normal
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    "$.schoolReference.schoolId",
                    "SchoolId"
                ),
            ]
        );

        return CreateProposedCheckSpec(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            subject,
            binding
        );
    }

    private static RelationshipAuthorizationCheckSpec CreateProposedDirectPeopleCheckSpec(
        TableWritePlan rootPlan
    )
    {
        var binding = CreateBinding(rootPlan, AuthNames.StudentDocumentId.Value, bindingIndex: 2);
        var subject = CreatePersonSubject(
            RootTable,
            AuthNames.StudentDocumentId,
            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
            [new ColumnPathStep(RootTable, AuthNames.StudentDocumentId, StudentTable, DocumentIdColumn)],
            new RelationshipAuthorizationPersonProposedAnchor(
                RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
                binding
            )
        );

        return CreateProposedCheckSpec(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            subject,
            binding
        );
    }

    private static RelationshipAuthorizationCheckSpec CreateProposedSelfPeopleCheckSpec(
        TableWritePlan rootPlan
    )
    {
        var binding = CreateBinding(rootPlan, DocumentIdColumn.Value, bindingIndex: 0);
        var subject = CreatePersonSubject(
            RootTable,
            DocumentIdColumn,
            RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
            [],
            new RelationshipAuthorizationPersonProposedAnchor(
                RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId,
                binding
            )
        );

        return CreateProposedCheckSpec(
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
            subject,
            binding
        );
    }

    private static RelationshipAuthorizationCheckSpec CreateProposedTransitivePeopleCheckSpec(
        TableWritePlan rootPlan
    )
    {
        var binding = CreateBinding(rootPlan, "SchoolId", bindingIndex: 1);
        var subject = CreatePersonSubject(
            StudentSchoolAssociationTable,
            AuthNames.StudentDocumentId,
            RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
            [
                new ColumnPathStep(
                    RootTable,
                    binding.Column,
                    StudentSchoolAssociationTable,
                    DocumentIdColumn
                ),
                new ColumnPathStep(
                    StudentSchoolAssociationTable,
                    AuthNames.StudentDocumentId,
                    StudentTable,
                    DocumentIdColumn
                ),
            ],
            new RelationshipAuthorizationPersonProposedAnchor(
                RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
                binding
            )
        );

        return CreateProposedCheckSpec(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            subject,
            binding
        );
    }

    private static RelationshipAuthorizationCheckSpec CreateProposedCheckSpec(
        string strategyName,
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationProposedValueBinding binding
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, RawConfiguredIndex: 0),
            RelationshipLocalOrder: 0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Proposed,
            [subject],
            new RelationshipAuthorizationCheckTarget.Proposed(RootTable, [binding])
        );

    private static RelationshipAuthorizationSubject CreatePersonSubject(
        DbTableName subjectTable,
        DbColumnName subjectColumn,
        RelationshipAuthorizationPersonSubjectPathKind pathKind,
        IReadOnlyList<ColumnPathStep> pathSteps,
        RelationshipAuthorizationPersonProposedAnchor proposedAnchor
    ) =>
        new(
            SchoolResource,
            subjectTable,
            subjectColumn,
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.Student,
                    "$.studentReference.studentUniqueId",
                    "StudentUniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                RelationshipAuthorizationPersonKind.Student,
                new RelationshipAuthorizationPersonSubjectPath(pathKind, pathSteps),
                new RelationshipAuthorizationPersonStoredAnchor(RootTable, DocumentIdColumn),
                proposedAnchor
            )
        );

    private static RelationshipAuthorizationProposedValueBinding CreateBinding(
        TableWritePlan rootPlan,
        string columnName,
        int bindingIndex
    )
    {
        var binding = rootPlan.ColumnBindings[bindingIndex];
        binding.Column.ColumnName.Value.Should().Be(columnName);

        return new RelationshipAuthorizationProposedValueBinding(
            RootTable,
            binding.Column.ColumnName,
            bindingIndex,
            binding.Column.ColumnName.Value,
            binding.ParameterName
        );
    }

    private static TableWritePlan CreateRootPlan()
    {
        var tableModel = new DbTableModel(
            RootTable,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(DocumentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                CreateColumn(DocumentIdColumn, ColumnKind.ParentKeyPart, scalarType: null, sourcePath: null),
                CreateColumn(
                    new DbColumnName("SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    "$.schoolId"
                ),
                CreateColumn(
                    AuthNames.StudentDocumentId,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    "$.studentReference.studentUniqueId"
                ),
                CreateColumn(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    "$.name"
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [DocumentIdColumn],
                [DocumentIdColumn],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @SchoolId, @Student_DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"SchoolId\" = @SchoolId, \"Student_DocumentId\" = @Student_DocumentId, \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 4, 1000),
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
                        tableModel.Columns[1].SourceJsonPath!.Value,
                        tableModel.Columns[1].ScalarType!
                    ),
                    "SchoolId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        tableModel.Columns[2].SourceJsonPath!.Value,
                        tableModel.Columns[2].ScalarType!
                    ),
                    "Student_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        tableModel.Columns[3].SourceJsonPath!.Value,
                        tableModel.Columns[3].ScalarType!
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static DbColumnModel CreateColumn(
        DbColumnName columnName,
        ColumnKind kind,
        RelationalScalarType? scalarType,
        string? sourcePath
    ) =>
        new(
            columnName,
            kind,
            scalarType,
            IsNullable: false,
            sourcePath is null ? null : new JsonPathExpression(sourcePath, []),
            TargetResource: null,
            new ColumnStorage.Stored()
        );

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");
    private static readonly DbTableName RootTable = new(new DbSchemaName("edfi"), "School");
    private static readonly DbTableName StudentTable = new(new DbSchemaName("edfi"), "Student");
    private static readonly DbTableName StudentSchoolAssociationTable = new(
        new DbSchemaName("edfi"),
        "StudentSchoolAssociation"
    );
}
