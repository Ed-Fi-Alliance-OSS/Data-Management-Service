// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_Guarded_No_Op
{
    [Test]
    public void It_is_not_a_no_op_when_collection_row_payload_matches_but_immediate_parent_locator_changes()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateNestedCollectionPlan();
        var mergeResult = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [CreateRow(345L, 255901, "Lincoln High")],
                    [CreateRow(345L, 255901, "Lincoln High")]
                ),
                new RelationalWriteMergedTableState(
                    collectionPlan,
                    [CreateNestedCollectionRow(10L, 345L, 100L, 1, "A")],
                    [CreateNestedCollectionRow(10L, 345L, 200L, 1, "A")]
                ),
            ],
            supportsGuardedNoOp: true
        );

        RelationalWriteGuardedNoOp.IsNoOpCandidate(mergeResult).Should().BeFalse();
    }

    [Test]
    public void It_is_not_a_no_op_when_collection_row_payload_matches_but_row_identity_and_parent_locator_change_together()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateNestedCollectionPlan();
        var mergeResult = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [CreateRow(345L, 255901, "Lincoln High")],
                    [CreateRow(345L, 255901, "Lincoln High")]
                ),
                new RelationalWriteMergedTableState(
                    collectionPlan,
                    [CreateNestedCollectionRow(10L, 345L, 100L, 1, "A")],
                    [CreateNestedCollectionRow(20L, 345L, 200L, 1, "A")]
                ),
            ],
            supportsGuardedNoOp: true
        );

        RelationalWriteGuardedNoOp.IsNoOpCandidate(mergeResult).Should().BeFalse();
    }

    [Test]
    public void It_is_a_no_op_when_collection_row_payload_and_row_identity_and_parent_locators_all_match()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateNestedCollectionPlan();
        var mergeResult = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [CreateRow(345L, 255901, "Lincoln High")],
                    [CreateRow(345L, 255901, "Lincoln High")]
                ),
                new RelationalWriteMergedTableState(
                    collectionPlan,
                    [CreateNestedCollectionRow(10L, 345L, 100L, 1, "A")],
                    [CreateNestedCollectionRow(10L, 345L, 100L, 1, "A")]
                ),
            ],
            supportsGuardedNoOp: true
        );

        RelationalWriteGuardedNoOp.IsNoOpCandidate(mergeResult).Should().BeTrue();
    }

    private static RelationalWriteMergedTableRow CreateRow(params object?[] values) =>
        new(
            values.Select(value => (FlattenedWriteValue)new FlattenedWriteValue.Literal(value)),
            values.Select(value => (FlattenedWriteValue)new FlattenedWriteValue.Literal(value))
        );

    private static RelationalWriteMergedTableRow CreateNestedCollectionRow(
        long collectionItemId,
        long parentDocumentId,
        long parentCollectionItemId,
        int ordinal,
        string code
    )
    {
        FlattenedWriteValue[] values =
        [
            new FlattenedWriteValue.Literal(collectionItemId),
            new FlattenedWriteValue.Literal(parentDocumentId),
            new FlattenedWriteValue.Literal(parentCollectionItemId),
            new FlattenedWriteValue.Literal(ordinal),
            new FlattenedWriteValue.Literal(code),
        ];

        FlattenedWriteValue[] comparableValues =
        [
            new FlattenedWriteValue.Literal(ordinal),
            new FlattenedWriteValue.Literal(code),
        ];

        return new RelationalWriteMergedTableRow(values, comparableValues);
    }

    private static TableWritePlan CreateNestedCollectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolCategoryCode"),
            new JsonPathExpression("$.categories[*].codes[*]", []),
            new TableKey(
                "PK_SchoolCategoryCode",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ParentDocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ParentCollectionItemId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Code"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    false,
                    new JsonPathExpression("$.code", []),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("ParentDocumentId")],
                [new DbColumnName("ParentCollectionItemId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.code", []),
                        new DbColumnName("Code")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolCategoryCode\" values (@CollectionItemId, @ParentDocumentId, @ParentCollectionItemId, @Ordinal, @Code)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentCollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.code", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "Code"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.code", []), 4)],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolCategoryCode\" set \"Ordinal\" = @Ordinal, \"Code\" = @Code where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolCategoryCode\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [3, 4]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    internal static TableWritePlan CreateRootPlan()
    {
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
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", []),
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
            InsertSql: """insert into edfi."School" values (@DocumentId, @SchoolId, @Name)""",
            UpdateSql: """update edfi."School" set "SchoolId" = @SchoolId, "Name" = @Name where "DocumentId" = @DocumentId""",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
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
}
