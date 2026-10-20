// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Builds a School root plan whose identity is (SchoolId, Name), with Name optionally flagged
/// for the SQL Server identity collation, plus a non-identity ShortName column.
/// </summary>
internal static class RelationalWriteIdentityStabilityTestSupport
{
    private const string ImmutableIdentityMessage =
        "Identifying values for the School resource cannot be changed. Delete and recreate the resource item instead.";

    public static RelationalWriteExecutorRequest CreateRequest(
        bool nameUsesSqlServerIdentityCollation,
        RelationalWriteOperationKind operationKind = RelationalWriteOperationKind.Put,
        bool allowIdentityUpdates = false
    )
    {
        var rootPlan = CreateRootPlan(nameUsesSqlServerIdentityCollation);
        var resourceModel = Given_Default_Relational_Write_Executor.CreateRelationalResourceModel(
            rootPlan.TableModel
        );
        var mappingSet = WithSchoolIdAndNameIdentity(
            Given_Default_Relational_Write_Executor.CreateMappingSet(
                resourceModel,
                [rootPlan],
                SqlDialect.Mssql
            )
        );
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            operationKind,
            operationKind is RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetRequest.Put(documentUuid)
                : new RelationalWriteTargetRequest.Post(
                    new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                    documentUuid
                ),
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse("""{"schoolId":255901}""")!,
            allowIdentityUpdates,
            new TraceId("identity-stability-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
            new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 44L)
        );
    }

    /// <summary>
    /// Replaces the document-stamping trigger so its identity projection is (SchoolId, Name).
    /// </summary>
    public static MappingSet WithSchoolIdAndNameIdentity(MappingSet mappingSet) =>
        mappingSet with
        {
            Model = mappingSet.Model with
            {
                TriggersInCreateOrder =
                [
                    new DbTriggerInfo(
                        new DbTriggerName("TR_School_DocumentStamping"),
                        new DbTableName(new DbSchemaName("edfi"), "School"),
                        [new DbColumnName("DocumentId")],
                        [new DbColumnName("SchoolId"), new DbColumnName("Name")],
                        new TriggerKindParameters.DocumentStamping()
                    ),
                ],
            },
        };

    public static RelationalWriteMergeResult CreateMergeResult(
        RelationalWriteExecutorRequest request,
        (int SchoolId, string Name, string ShortName) current,
        (int SchoolId, string Name, string ShortName) merged
    ) => CreateMergeResult(request.WritePlan.TablePlansInDependencyOrder[0], current, merged);

    public static RelationalWriteMergeResult CreateMergeResult(
        TableWritePlan rootPlan,
        (int SchoolId, string Name, string ShortName) current,
        (int SchoolId, string Name, string ShortName) merged
    )
    {
        return new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [CreateRow(rootPlan, current)],
                    [CreateRow(rootPlan, merged)]
                ),
            ],
            supportsGuardedNoOp: true
        );
    }

    public static RelationalWriteMergedTableRow MergedRootRow(RelationalWriteMergeResult mergeResult) =>
        mergeResult.TablesInDependencyOrder[0].MergedRows.Single();

    public static RelationalWriteExecutorResult ImmutableIdentityFailure(
        RelationalWriteOperationKind operationKind
    ) =>
        operationKind is RelationalWriteOperationKind.Put
            ? new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureImmutableIdentity(ImmutableIdentityMessage)
            )
            : new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureImmutableIdentity(ImmutableIdentityMessage)
            );

    private static RelationalWriteMergedTableRow CreateRow(
        TableWritePlan rootPlan,
        (int SchoolId, string Name, string ShortName) values
    ) =>
        RelationalWriteRowHelpers.CreateMergedTableRow(
            rootPlan,
            [
                new FlattenedWriteValue.Literal(345L),
                new FlattenedWriteValue.Literal(values.SchoolId),
                new FlattenedWriteValue.Literal(values.Name),
                new FlattenedWriteValue.Literal(values.ShortName),
            ]
        );

    public static TableWritePlan CreateRootPlan(bool nameUsesSqlServerIdentityCollation)
    {
        var stringType = new RelationalScalarType(ScalarKind.String, MaxLength: 75);
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
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
                    new DbColumnName("SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression("$.schoolId", []),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    stringType,
                    false,
                    new JsonPathExpression("$.name", []),
                    null,
                    new ColumnStorage.Stored(),
                    UsesSqlServerIdentityCollation: nameUsesSqlServerIdentityCollation
                ),
                new DbColumnModel(
                    new DbColumnName("ShortName"),
                    ColumnKind.Scalar,
                    stringType,
                    true,
                    new JsonPathExpression("$.shortName", []),
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

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @SchoolId, @Name, @ShortName)",
            UpdateSql: "update edfi.\"School\" set \"SchoolId\" = @SchoolId, \"Name\" = @Name, \"ShortName\" = @ShortName where \"DocumentId\" = @DocumentId",
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
                        new JsonPathExpression("$.schoolId", []),
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                    "SchoolId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(new JsonPathExpression("$.name", []), stringType),
                    "Name"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(new JsonPathExpression("$.shortName", []), stringType),
                    "ShortName"
                ),
            ],
            KeyUnificationPlans: []
        );
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteIdentityStability_With_A_Flagged_Identity_Differing_Only_In_Case
{
    private RelationalWriteMergeResult _mergeResult = null!;
    private RelationalWriteMergeResult _resultMergeResult = null!;
    private RelationalWriteExecutorResult? _failure;

    [SetUp]
    public void Setup()
    {
        var request = RelationalWriteIdentityStabilityTestSupport.CreateRequest(
            nameUsesSqlServerIdentityCollation: true
        );
        _mergeResult = RelationalWriteIdentityStabilityTestSupport.CreateMergeResult(
            request,
            current: (255901, "Lincoln High", "LHS"),
            merged: (255901, "LINCOLN HIGH", "Lincoln")
        );

        (_resultMergeResult, _failure) = RelationalWriteIdentityStability.Apply(request, _mergeResult);
    }

    [Test]
    public void It_does_not_fail()
    {
        _failure.Should().BeNull();
    }

    [Test]
    public void It_rebinds_the_identity_column_to_the_stored_value()
    {
        RelationalWriteIdentityStabilityTestSupport
            .MergedRootRow(_resultMergeResult)
            .Values[2]
            .Should()
            .Be(new FlattenedWriteValue.Literal("Lincoln High"));
    }

    [Test]
    public void It_keeps_the_requested_non_identity_value()
    {
        RelationalWriteIdentityStabilityTestSupport
            .MergedRootRow(_resultMergeResult)
            .Values[3]
            .Should()
            .Be(new FlattenedWriteValue.Literal("Lincoln"));
    }

    [Test]
    public void It_recomputes_the_comparable_values_from_the_rebound_values()
    {
        var reboundRow = RelationalWriteIdentityStabilityTestSupport.MergedRootRow(_resultMergeResult);

        reboundRow
            .ComparableValues.Should()
            .Equal(
                RelationalWriteMergeSupport.ProjectComparableValues(
                    _resultMergeResult.TablesInDependencyOrder[0].TableWritePlan,
                    reboundRow.Values
                )
            );
        reboundRow.ComparableValues.Should().Contain(new FlattenedWriteValue.Literal("Lincoln High"));
        reboundRow.ComparableValues.Should().NotContain(new FlattenedWriteValue.Literal("LINCOLN HIGH"));
    }

    [Test]
    public void It_leaves_the_input_merge_result_unchanged()
    {
        RelationalWriteIdentityStabilityTestSupport
            .MergedRootRow(_mergeResult)
            .Values[2]
            .Should()
            .Be(new FlattenedWriteValue.Literal("LINCOLN HIGH"));
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteIdentityStability_With_An_Unflagged_Identity_Differing_Only_In_Case
{
    [TestCase(RelationalWriteOperationKind.Put)]
    [TestCase(RelationalWriteOperationKind.Post)]
    public void It_returns_the_immutable_identity_failure(RelationalWriteOperationKind operationKind)
    {
        var request = RelationalWriteIdentityStabilityTestSupport.CreateRequest(
            nameUsesSqlServerIdentityCollation: false,
            operationKind
        );
        var mergeResult = RelationalWriteIdentityStabilityTestSupport.CreateMergeResult(
            request,
            current: (255901, "Lincoln High", "LHS"),
            merged: (255901, "LINCOLN HIGH", "LHS")
        );

        var (_, failure) = RelationalWriteIdentityStability.Apply(request, mergeResult);

        failure
            .Should()
            .BeEquivalentTo(
                RelationalWriteIdentityStabilityTestSupport.ImmutableIdentityFailure(operationKind)
            );
    }

    [Test]
    public void It_keeps_the_request_casing_when_identity_updates_are_allowed()
    {
        var request = RelationalWriteIdentityStabilityTestSupport.CreateRequest(
            nameUsesSqlServerIdentityCollation: false,
            allowIdentityUpdates: true
        );
        var mergeResult = RelationalWriteIdentityStabilityTestSupport.CreateMergeResult(
            request,
            current: (255901, "Lincoln High", "LHS"),
            merged: (255901, "LINCOLN HIGH", "LHS")
        );

        var (resultMergeResult, failure) = RelationalWriteIdentityStability.Apply(request, mergeResult);

        failure.Should().BeNull();
        resultMergeResult.Should().BeSameAs(mergeResult);
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteIdentityStability_With_No_Identity_Difference
{
    [Test]
    public void It_returns_the_same_merge_result_instance()
    {
        var request = RelationalWriteIdentityStabilityTestSupport.CreateRequest(
            nameUsesSqlServerIdentityCollation: true
        );
        var mergeResult = RelationalWriteIdentityStabilityTestSupport.CreateMergeResult(
            request,
            current: (255901, "Lincoln High", "LHS"),
            merged: (255901, "Lincoln High", "Lincoln")
        );

        var (resultMergeResult, failure) = RelationalWriteIdentityStability.Apply(request, mergeResult);

        failure.Should().BeNull();
        resultMergeResult.Should().BeSameAs(mergeResult);
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteIdentityStability_With_A_Case_Only_And_A_Genuine_Identity_Change
{
    [TestCase(RelationalWriteOperationKind.Put)]
    [TestCase(RelationalWriteOperationKind.Post)]
    public void It_returns_the_immutable_identity_failure_when_identity_updates_are_disallowed(
        RelationalWriteOperationKind operationKind
    )
    {
        var request = RelationalWriteIdentityStabilityTestSupport.CreateRequest(
            nameUsesSqlServerIdentityCollation: true,
            operationKind
        );
        var mergeResult = RelationalWriteIdentityStabilityTestSupport.CreateMergeResult(
            request,
            current: (255901, "Lincoln High", "LHS"),
            merged: (255902, "LINCOLN HIGH", "LHS")
        );

        var (_, failure) = RelationalWriteIdentityStability.Apply(request, mergeResult);

        failure
            .Should()
            .BeEquivalentTo(
                RelationalWriteIdentityStabilityTestSupport.ImmutableIdentityFailure(operationKind)
            );
    }

    [Test]
    public void It_rebinds_only_the_case_only_column_when_identity_updates_are_allowed()
    {
        var request = RelationalWriteIdentityStabilityTestSupport.CreateRequest(
            nameUsesSqlServerIdentityCollation: true,
            allowIdentityUpdates: true
        );
        var mergeResult = RelationalWriteIdentityStabilityTestSupport.CreateMergeResult(
            request,
            current: (255901, "Lincoln High", "LHS"),
            merged: (255902, "LINCOLN HIGH", "LHS")
        );

        var (resultMergeResult, failure) = RelationalWriteIdentityStability.Apply(request, mergeResult);

        failure.Should().BeNull();
        var mergedRootRow = RelationalWriteIdentityStabilityTestSupport.MergedRootRow(resultMergeResult);
        mergedRootRow.Values[1].Should().Be(new FlattenedWriteValue.Literal(255902));
        mergedRootRow.Values[2].Should().Be(new FlattenedWriteValue.Literal("Lincoln High"));
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteIdentityStability_With_A_Flagged_Identity_Matched_Only_By_The_Collation
{
    // SQL_Latin1_General_CP1_CI_AS can equate "ß" and "ss", but OrdinalIgnoreCase does not, so
    // the comparer residue is treated as a genuine identity change and fails closed.
    [TestCase(RelationalWriteOperationKind.Put)]
    [TestCase(RelationalWriteOperationKind.Post)]
    public void It_returns_the_immutable_identity_failure(RelationalWriteOperationKind operationKind)
    {
        var request = RelationalWriteIdentityStabilityTestSupport.CreateRequest(
            nameUsesSqlServerIdentityCollation: true,
            operationKind
        );
        var mergeResult = RelationalWriteIdentityStabilityTestSupport.CreateMergeResult(
            request,
            current: (255901, "Strasse High", "SH"),
            merged: (255901, "Straße High", "SH")
        );

        var (_, failure) = RelationalWriteIdentityStability.Apply(request, mergeResult);

        failure
            .Should()
            .BeEquivalentTo(
                RelationalWriteIdentityStabilityTestSupport.ImmutableIdentityFailure(operationKind)
            );
    }

    [Test]
    public void It_keeps_the_request_value_when_identity_updates_are_allowed()
    {
        var request = RelationalWriteIdentityStabilityTestSupport.CreateRequest(
            nameUsesSqlServerIdentityCollation: true,
            allowIdentityUpdates: true
        );
        var mergeResult = RelationalWriteIdentityStabilityTestSupport.CreateMergeResult(
            request,
            current: (255901, "Strasse High", "SH"),
            merged: (255901, "Straße High", "SH")
        );

        var (resultMergeResult, failure) = RelationalWriteIdentityStability.Apply(request, mergeResult);

        failure.Should().BeNull();
        resultMergeResult.Should().BeSameAs(mergeResult);
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteIdentityStability_For_A_New_Document
{
    [Test]
    public void It_returns_the_same_merge_result_instance_without_comparing()
    {
        var existingRequest = RelationalWriteIdentityStabilityTestSupport.CreateRequest(
            nameUsesSqlServerIdentityCollation: true,
            RelationalWriteOperationKind.Post
        );
        var request = existingRequest with
        {
            TargetContext = new RelationalWriteTargetContext.CreateNew(
                new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"))
            ),
        };
        var mergeResult = RelationalWriteIdentityStabilityTestSupport.CreateMergeResult(
            request,
            current: (255901, "Lincoln High", "LHS"),
            merged: (255902, "LINCOLN HIGH", "LHS")
        );

        var (resultMergeResult, failure) = RelationalWriteIdentityStability.Apply(request, mergeResult);

        failure.Should().BeNull();
        resultMergeResult.Should().BeSameAs(mergeResult);
    }
}
