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
                    tableModel.Columns[1],
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
