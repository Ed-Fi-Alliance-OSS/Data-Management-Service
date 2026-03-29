// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class SchoolAddressCollectionPlanFixture
{
    private static readonly JsonPathExpression AddressesPath = new(
        "$.addresses[*]",
        [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
    );

    private static readonly JsonPathExpression AddressTypePath = new(
        "$.addressType",
        [new JsonPathSegment.Property("addressType")]
    );

    private static readonly JsonPathExpression StreetNumberNamePath = new(
        "$.streetNumberName",
        [new JsonPathSegment.Property("streetNumberName")]
    );

    public static SchoolAddressCollectionPlanFixtureData Create(
        params string[] additionalCollectionKeyColumnNames
    )
    {
        return Create(DbTableKind.Collection, additionalCollectionKeyColumnNames);
    }

    public static SchoolAddressCollectionPlanFixtureData Create(
        DbTableKind tableKind = DbTableKind.Collection,
        params string[] additionalCollectionKeyColumnNames
    )
    {
        var extraCollectionKeyColumnNames = additionalCollectionKeyColumnNames
            .Select(static columnName => new DbColumnName(columnName))
            .ToArray();
        var tableModel = CreateTableModel(tableKind, extraCollectionKeyColumnNames);
        var columnBindings = CreateColumnBindings(tableModel, extraCollectionKeyColumnNames.Length);
        var ordinalBindingIndex = 2 + extraCollectionKeyColumnNames.Length;
        var semanticIdentityBindingIndex = ordinalBindingIndex + 1;
        var streetNumberBindingIndex = ordinalBindingIndex + 2;

        return new SchoolAddressCollectionPlanFixtureData(
            TableModel: tableModel,
            ColumnBindings: columnBindings,
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        RelativePath: AddressTypePath,
                        BindingIndex: semanticIdentityBindingIndex
                    ),
                ],
                StableRowIdentityBindingIndex: 1,
                UpdateByStableRowIdentitySql: "UPDATE COLLECTION SQL",
                DeleteByStableRowIdentitySql: "DELETE COLLECTION SQL",
                OrdinalBindingIndex: ordinalBindingIndex,
                CompareBindingIndexesInOrder:
                [
                    1,
                    ordinalBindingIndex,
                    semanticIdentityBindingIndex,
                    streetNumberBindingIndex,
                ]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                ColumnName: new DbColumnName("CollectionItemId"),
                BindingIndex: 1
            )
        );
    }

    private static DbTableModel CreateTableModel(
        DbTableKind tableKind,
        IReadOnlyList<DbColumnName> additionalCollectionKeyColumnNames
    )
    {
        var columns = new List<DbColumnModel>
        {
            new(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                new DbColumnName("CollectionItemId"),
                ColumnKind.CollectionKey,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
        };

        columns.AddRange(
            additionalCollectionKeyColumnNames.Select(static columnName => new DbColumnModel(
                columnName,
                ColumnKind.CollectionKey,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ))
        );

        columns.AddRange([
            new DbColumnModel(
                new DbColumnName("Ordinal"),
                ColumnKind.Ordinal,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("AddressType"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                IsNullable: false,
                SourceJsonPath: AddressTypePath,
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("StreetNumberName"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, MaxLength: 150),
                IsNullable: true,
                SourceJsonPath: StreetNumberNamePath,
                TargetResource: null
            ),
        ]);

        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            AddressesPath,
            new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            columns,
            []
        )
        {
            IdentityMetadata = CreateIdentityMetadata(tableKind),
        };
    }

    private static WriteColumnBinding[] CreateColumnBindings(
        DbTableModel tableModel,
        int additionalCollectionKeyCount
    )
    {
        var bindings = new List<WriteColumnBinding>
        {
            new(
                Column: tableModel.Columns[0],
                Source: new WriteValueSource.DocumentId(),
                ParameterName: "documentId"
            ),
            new(
                Column: tableModel.Columns[1],
                Source: new WriteValueSource.Precomputed(),
                ParameterName: "collectionItemId"
            ),
        };

        for (var index = 0; index < additionalCollectionKeyCount; index++)
        {
            var columnModel = tableModel.Columns[index + 2];

            bindings.Add(
                new WriteColumnBinding(
                    Column: columnModel,
                    Source: new WriteValueSource.Precomputed(),
                    ParameterName: ToCamelCase(columnModel.ColumnName.Value)
                )
            );
        }

        var ordinalColumnIndex = 2 + additionalCollectionKeyCount;

        bindings.AddRange([
            new WriteColumnBinding(
                Column: tableModel.Columns[ordinalColumnIndex],
                Source: new WriteValueSource.Ordinal(),
                ParameterName: "ordinal"
            ),
            new WriteColumnBinding(
                Column: tableModel.Columns[ordinalColumnIndex + 1],
                Source: new WriteValueSource.Scalar(
                    AddressTypePath,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 32)
                ),
                ParameterName: "addressType"
            ),
            new WriteColumnBinding(
                Column: tableModel.Columns[ordinalColumnIndex + 2],
                Source: new WriteValueSource.Scalar(
                    StreetNumberNamePath,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 150)
                ),
                ParameterName: "streetNumberName"
            ),
        ]);

        return [.. bindings];
    }

    private static DbTableIdentityMetadata CreateIdentityMetadata(DbTableKind tableKind)
    {
        return new DbTableIdentityMetadata(
            TableKind: tableKind,
            PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
            RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
            SemanticIdentityBindings:
            [
                new CollectionSemanticIdentityBinding(
                    RelativePath: AddressTypePath,
                    ColumnName: new DbColumnName("AddressType")
                ),
            ]
        );
    }

    private static string ToCamelCase(string value)
    {
        return string.IsNullOrEmpty(value)
            ? value
            : string.Create(
                value.Length,
                value,
                static (buffer, state) =>
                {
                    buffer[0] = char.ToLowerInvariant(state[0]);
                    state.AsSpan(1).CopyTo(buffer[1..]);
                }
            );
    }
}

internal sealed record SchoolAddressCollectionPlanFixtureData(
    DbTableModel TableModel,
    IReadOnlyList<WriteColumnBinding> ColumnBindings,
    CollectionMergePlan CollectionMergePlan,
    CollectionKeyPreallocationPlan CollectionKeyPreallocationPlan
)
{
    public TableWritePlan CreateTableWritePlan()
    {
        return CreateTableWritePlan(
            updateSql: null,
            deleteByParentSql: null,
            collectionMergePlan: CollectionMergePlan,
            collectionKeyPreallocationPlan: CollectionKeyPreallocationPlan
        );
    }

    public TableWritePlan CreateTableWritePlan(
        string? updateSql,
        string? deleteByParentSql,
        CollectionMergePlan? collectionMergePlan,
        CollectionKeyPreallocationPlan? collectionKeyPreallocationPlan
    )
    {
        return new TableWritePlan(
            TableModel: TableModel,
            InsertSql: "INSERT SQL",
            UpdateSql: updateSql,
            DeleteByParentSql: deleteByParentSql,
            BulkInsertBatching: new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 100,
                ParametersPerRow: ColumnBindings.Count,
                MaxParametersPerCommand: 2100
            ),
            ColumnBindings: ColumnBindings,
            KeyUnificationPlans: [],
            CollectionMergePlan: collectionMergePlan,
            CollectionKeyPreallocationPlan: collectionKeyPreallocationPlan
        );
    }

    public RelationalResourceModel CreateResourceModel()
    {
        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: TableModel,
            TablesInDependencyOrder: [TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }
}
