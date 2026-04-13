// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Pins the Core/backend vocabulary contract for hidden member preservation.
///
/// Core emits <c>HiddenMemberPaths</c> as <b>bare scope-relative member names</b>
/// (e.g., <c>entryDate</c>, <c>officialAttendancePeriod</c>), which matches
/// <see cref="CompiledScopeDescriptor.CanonicalScopeRelativeMemberPaths"/>.
/// The backend classifier compares against
/// <c>WriteValueSource.*.RelativePath.Canonical</c>, which is <c>$.</c>-prefixed
/// scope-relative JSONPath (e.g., <c>$.entryDate</c>). These tests encode that
/// contract so regressions that feed Core-shaped names straight into the classifier
/// will fail loudly.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_Core_Shaped_HiddenMemberPaths_Reach_The_Classifier
{
    private TableWritePlan _plan = null!;

    [SetUp]
    public void Setup()
    {
        var tableModel = CreateRootTableModel("School");
        _plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
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
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    [Test]
    public void It_classifies_bare_scope_relative_name_as_hidden_preserved()
    {
        // Core vocabulary: bare name, no "$." prefix.
        ImmutableArray<string> coreShapedHidden = ["name"];

        // HiddenMemberPathVocabulary.ToJsonPathRelative normalizes Core-shaped bare
        // names to the "$." vocabulary the classifier expects.  This is the same
        // conversion that RelationalWriteMerge applies at every ingestion site.
        var normalized = HiddenMemberPathVocabulary.ToJsonPathRelative(coreShapedHidden);
        var result = RelationalWriteBindingClassifier.Classify(_plan, normalized);

        // index 1 = Scalar binding for "$.name"
        result[1].Should().Be(BindingClassification.HiddenPreserved);
    }

    /// <summary>
    /// Verifies that the Core/backend vocabulary contract holds for
    /// <see cref="EdFi.DataManagementService.Backend.External.Plans.WriteValueSource.DescriptorReference"/>
    /// bindings: a bare scope-relative descriptor path normalised through
    /// <see cref="EdFi.DataManagementService.Backend.HiddenMemberPathVocabulary.ToJsonPathRelative"/>
    /// must classify the FK column as
    /// <see cref="EdFi.DataManagementService.Backend.BindingClassification.HiddenPreserved"/>.
    /// </summary>
    /// <remarks>
    /// Exercises the <c>DescriptorReference</c> shape of <c>WriteValueSource</c> so that
    /// vocabulary drift on that arm fails the same way as the scalar arm.
    /// </remarks>
    [Test]
    public void It_classifies_bare_scope_relative_descriptor_path_as_hidden_preserved()
    {
        var tableModel = CreateRootTableModel("School");
        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @GradeLevel_DescriptorId)",
            UpdateSql: "update edfi.\"School\" set \"GradeLevel_DescriptorId\" = @GradeLevel_DescriptorId where \"DocumentId\" = @DocumentId",
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
                    tableModel.Columns[3],
                    new WriteValueSource.DescriptorReference(
                        new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"),
                        new JsonPathExpression("$.gradeLevelDescriptor", [])
                    ),
                    "GradeLevel_DescriptorId"
                ),
            ],
            KeyUnificationPlans: []
        );

        // Core vocabulary: bare name, no "$." prefix.
        var hidden = HiddenMemberPathVocabulary.ToJsonPathRelative(["gradeLevelDescriptor"]);

        var result = RelationalWriteBindingClassifier.Classify(plan, hidden);

        // index 1 = DescriptorReference binding for "$.gradeLevelDescriptor"
        result[1].Should().Be(BindingClassification.HiddenPreserved);
    }

    /// <summary>
    /// Verifies that the Core/backend vocabulary contract holds for
    /// <see cref="EdFi.DataManagementService.Backend.External.Plans.WriteValueSource.DocumentReference"/>
    /// bindings: when any identity member path of a document reference is hidden (after
    /// <see cref="EdFi.DataManagementService.Backend.HiddenMemberPathVocabulary.ToJsonPathRelative"/>
    /// conversion), the composite FK column must classify as
    /// <see cref="EdFi.DataManagementService.Backend.BindingClassification.HiddenPreserved"/>.
    /// </summary>
    /// <remarks>
    /// Exercises the <c>DocumentReference</c> shape of <c>WriteValueSource</c> via
    /// <see cref="EdFi.DataManagementService.Backend.External.DocumentReferenceBinding.IdentityBindings"/>
    /// so that vocabulary drift on that arm fails loudly.
    /// </remarks>
    [Test]
    public void It_classifies_document_reference_fk_as_hidden_when_any_identity_member_is_hidden()
    {
        // Model: a calendarReference with two identity members.  Core hides only
        // "calendarReference.schoolId"; the FK must still be HiddenPreserved because
        // any hidden identity member forces the entire composite FK to be preserved.
        var tableModel = CreateRootTableModel("Session");
        var calendarTable = new DbTableName(new DbSchemaName("edfi"), "Session");

        IReadOnlyList<DocumentReferenceBinding> docRefBindings =
        [
            new DocumentReferenceBinding(
                IsIdentityComponent: false,
                ReferenceObjectPath: new JsonPathExpression("$.calendarReference", []),
                Table: calendarTable,
                FkColumn: new DbColumnName("Calendar_DocumentId"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.schoolId", []),
                        ReferenceJsonPath: new JsonPathExpression("$.calendarReference.schoolId", []),
                        Column: new DbColumnName("Calendar_SchoolId")
                    ),
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.calendarCode", []),
                        ReferenceJsonPath: new JsonPathExpression("$.calendarReference.calendarCode", []),
                        Column: new DbColumnName("Calendar_CalendarCode")
                    ),
                ]
            ),
        ];

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"Session\" values (@DocumentId, @Calendar_DocumentId)",
            UpdateSql: "update edfi.\"Session\" set \"Calendar_DocumentId\" = @Calendar_DocumentId where \"DocumentId\" = @DocumentId",
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
                    tableModel.Columns[4],
                    new WriteValueSource.DocumentReference(BindingIndex: 0),
                    "Calendar_DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        // Core vocabulary: bare scope-relative path for one identity member only.
        var hidden = HiddenMemberPathVocabulary.ToJsonPathRelative(["calendarReference.schoolId"]);

        var result = RelationalWriteBindingClassifier.Classify(plan, hidden, docRefBindings);

        // index 1 = DocumentReference FK; hidden because schoolId is hidden.
        result[1].Should().Be(BindingClassification.HiddenPreserved);
    }

    private static DbTableModel CreateRootTableModel(string tableName)
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), tableName),
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("SchoolId", ColumnKind.Scalar),
                CreateColumn("Name", ColumnKind.Scalar),
                CreateColumn("GradeLevel_DescriptorId", ColumnKind.DescriptorFk),
                CreateColumn("LocalEducationAgency_DocumentId", ColumnKind.DocumentFk),
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
    }

    private static DbColumnModel CreateColumn(string name, ColumnKind kind, bool isNullable = false)
    {
        return new DbColumnModel(
            new DbColumnName(name),
            kind,
            kind is ColumnKind.Scalar or ColumnKind.Ordinal
                ? new RelationalScalarType(ScalarKind.String)
                : null,
            isNullable,
            null,
            null,
            new ColumnStorage.Stored()
        );
    }
}
