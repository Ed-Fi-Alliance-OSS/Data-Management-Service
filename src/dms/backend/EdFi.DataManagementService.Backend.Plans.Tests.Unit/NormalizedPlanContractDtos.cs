// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using ExternalPlans = EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal sealed record QualifiedResourceNameDto(string ProjectName, string ResourceName);

internal sealed record DbTableNameDto(string Schema, string Name);

internal sealed record RelationalScalarTypeDto(
    NormalizedScalarKind Kind,
    int? MaxLength = null,
    int? DecimalPrecision = null,
    int? DecimalScale = null
);

internal enum NormalizedScalarKind
{
    String,
    Int32,
    Int64,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Time,
}

internal sealed record ResourceWritePlanDto
{
    public ResourceWritePlanDto(
        QualifiedResourceNameDto Resource,
        IEnumerable<TableWritePlanDto> TablePlansInDependencyOrder
    )
    {
        ArgumentNullException.ThrowIfNull(Resource);
        ArgumentNullException.ThrowIfNull(TablePlansInDependencyOrder);

        this.Resource = Resource;
        this.TablePlansInDependencyOrder = [.. TablePlansInDependencyOrder];
    }

    public QualifiedResourceNameDto Resource { get; init; }

    public ImmutableArray<TableWritePlanDto> TablePlansInDependencyOrder { get; init; }
}

internal sealed record TableWritePlanDto
{
    public TableWritePlanDto(
        DbTableNameDto Table,
        string InsertSql,
        string? UpdateSql,
        string? DeleteByParentSql,
        BulkInsertBatchingInfoDto BulkInsertBatching,
        IEnumerable<WriteColumnBindingDto> ColumnBindings,
        IEnumerable<KeyUnificationWritePlanDto> KeyUnificationPlans,
        CollectionMergePlanDto? CollectionMergePlan = null,
        CollectionKeyPreallocationPlanDto? CollectionKeyPreallocationPlan = null
    )
    {
        ArgumentNullException.ThrowIfNull(Table);
        ArgumentNullException.ThrowIfNull(InsertSql);
        ArgumentNullException.ThrowIfNull(BulkInsertBatching);
        ArgumentNullException.ThrowIfNull(ColumnBindings);
        ArgumentNullException.ThrowIfNull(KeyUnificationPlans);

        this.Table = Table;
        this.InsertSql = InsertSql;
        this.UpdateSql = UpdateSql;
        this.DeleteByParentSql = DeleteByParentSql;
        this.BulkInsertBatching = BulkInsertBatching;
        this.ColumnBindings = [.. ColumnBindings];
        this.KeyUnificationPlans = [.. KeyUnificationPlans];
        this.CollectionMergePlan = CollectionMergePlan;
        this.CollectionKeyPreallocationPlan = CollectionKeyPreallocationPlan;
    }

    public DbTableNameDto Table { get; init; }

    public string InsertSql { get; init; }

    public string? UpdateSql { get; init; }

    public string? DeleteByParentSql { get; init; }

    public BulkInsertBatchingInfoDto BulkInsertBatching { get; init; }

    public ImmutableArray<WriteColumnBindingDto> ColumnBindings { get; init; }

    public ImmutableArray<KeyUnificationWritePlanDto> KeyUnificationPlans { get; init; }

    public CollectionMergePlanDto? CollectionMergePlan { get; init; }

    public CollectionKeyPreallocationPlanDto? CollectionKeyPreallocationPlan { get; init; }
}

internal sealed record BulkInsertBatchingInfoDto(
    int MaxRowsPerBatch,
    int ParametersPerRow,
    int MaxParametersPerCommand
);

internal sealed record WriteColumnBindingDto(
    string ColumnName,
    WriteValueSourceDto Source,
    string ParameterName
);

internal sealed record CollectionKeyPreallocationPlanDto(string ColumnName, int BindingIndex);

internal sealed record ReferenceDerivedValueSourceDto(
    int BindingIndex,
    string ReferenceObjectPath,
    string IdentityJsonPath,
    string ReferenceJsonPath
);

internal sealed record CollectionMergePlanDto
{
    private const string CanonicalSemanticIdentityBindingsPropertyName = "semantic_identity_bindings";
    private const string CanonicalUpdateSqlPropertyName = "update_by_stable_row_identity_sql";
    private const string CanonicalDeleteSqlPropertyName = "delete_by_stable_row_identity_sql";
    private const string ManifestSemanticIdentityBindingsPropertyName = "semantic_identity_bindings_in_order";
    private const string ManifestUpdateSqlPropertyName = "update_by_stable_row_identity_sql_sha256";
    private const string ManifestDeleteSqlPropertyName = "delete_by_stable_row_identity_sql_sha256";

    public CollectionMergePlanDto(
        IEnumerable<CollectionMergeSemanticIdentityBindingDto> SemanticIdentityBindings,
        int StableRowIdentityBindingIndex,
        string UpdateByStableRowIdentitySql,
        string DeleteByStableRowIdentitySql,
        int OrdinalBindingIndex,
        IEnumerable<int> CompareBindingIndexesInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(SemanticIdentityBindings);
        ArgumentNullException.ThrowIfNull(UpdateByStableRowIdentitySql);
        ArgumentNullException.ThrowIfNull(DeleteByStableRowIdentitySql);
        ArgumentNullException.ThrowIfNull(CompareBindingIndexesInOrder);

        this.SemanticIdentityBindings = [.. SemanticIdentityBindings];
        this.StableRowIdentityBindingIndex = StableRowIdentityBindingIndex;
        this.UpdateByStableRowIdentitySql = UpdateByStableRowIdentitySql;
        this.DeleteByStableRowIdentitySql = DeleteByStableRowIdentitySql;
        this.OrdinalBindingIndex = OrdinalBindingIndex;
        this.CompareBindingIndexesInOrder = [.. CompareBindingIndexesInOrder];
    }

    public ImmutableArray<CollectionMergeSemanticIdentityBindingDto> SemanticIdentityBindings { get; init; }

    public int StableRowIdentityBindingIndex { get; init; }

    public string UpdateByStableRowIdentitySql { get; init; }

    public string DeleteByStableRowIdentitySql { get; init; }

    public int OrdinalBindingIndex { get; init; }

    public ImmutableArray<int> CompareBindingIndexesInOrder { get; init; }

    public static CollectionMergePlanDto? Encode(ExternalPlans.CollectionMergePlan? collectionMergePlan)
    {
        if (collectionMergePlan is null)
        {
            return null;
        }

        return new CollectionMergePlanDto(
            SemanticIdentityBindings: collectionMergePlan.SemanticIdentityBindings.Select(
                binding => new CollectionMergeSemanticIdentityBindingDto(
                    RelativePath: binding.RelativePath.Canonical,
                    BindingIndex: binding.BindingIndex
                )
            ),
            StableRowIdentityBindingIndex: collectionMergePlan.StableRowIdentityBindingIndex,
            UpdateByStableRowIdentitySql: collectionMergePlan.UpdateByStableRowIdentitySql,
            DeleteByStableRowIdentitySql: collectionMergePlan.DeleteByStableRowIdentitySql,
            OrdinalBindingIndex: collectionMergePlan.OrdinalBindingIndex,
            CompareBindingIndexesInOrder: collectionMergePlan.CompareBindingIndexesInOrder
        );
    }

    public ExternalPlans.CollectionMergePlan Decode(int bindingCount, string argumentName)
    {
        var semanticIdentityBindings = new ExternalPlans.CollectionMergeSemanticIdentityBinding[
            this.SemanticIdentityBindings.Length
        ];

        for (
            var semanticBindingIndex = 0;
            semanticBindingIndex < this.SemanticIdentityBindings.Length;
            semanticBindingIndex++
        )
        {
            var semanticBindingDto = this.SemanticIdentityBindings[semanticBindingIndex];
            var semanticBindingArgument =
                $"{argumentName}.{nameof(SemanticIdentityBindings)}[{semanticBindingIndex}]";

            semanticIdentityBindings[semanticBindingIndex] =
                new ExternalPlans.CollectionMergeSemanticIdentityBinding(
                    RelativePath: CompileJsonPath(
                        semanticBindingDto.RelativePath,
                        $"{semanticBindingArgument}.{nameof(CollectionMergeSemanticIdentityBindingDto.RelativePath)}"
                    ),
                    BindingIndex: ValidateBindingIndex(
                        semanticBindingDto.BindingIndex,
                        bindingCount,
                        $"{semanticBindingArgument}.{nameof(CollectionMergeSemanticIdentityBindingDto.BindingIndex)}",
                        "collection semantic-identity binding index"
                    )
                );
        }

        var compareBindingIndexes = new int[this.CompareBindingIndexesInOrder.Length];

        for (var compareIndex = 0; compareIndex < this.CompareBindingIndexesInOrder.Length; compareIndex++)
        {
            compareBindingIndexes[compareIndex] = ValidateBindingIndex(
                this.CompareBindingIndexesInOrder[compareIndex],
                bindingCount,
                $"{argumentName}.{nameof(CompareBindingIndexesInOrder)}[{compareIndex}]",
                "collection compare binding index"
            );
        }

        return new ExternalPlans.CollectionMergePlan(
            SemanticIdentityBindings: semanticIdentityBindings,
            StableRowIdentityBindingIndex: ValidateBindingIndex(
                this.StableRowIdentityBindingIndex,
                bindingCount,
                $"{argumentName}.{nameof(StableRowIdentityBindingIndex)}",
                "collection stable-row-identity binding index"
            ),
            UpdateByStableRowIdentitySql: this.UpdateByStableRowIdentitySql,
            DeleteByStableRowIdentitySql: this.DeleteByStableRowIdentitySql,
            OrdinalBindingIndex: ValidateBindingIndex(
                this.OrdinalBindingIndex,
                bindingCount,
                $"{argumentName}.{nameof(OrdinalBindingIndex)}",
                "collection ordinal binding index"
            ),
            CompareBindingIndexesInOrder: compareBindingIndexes
        );
    }

    public void WriteCanonicalJson(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        WriteJson(
            writer,
            CanonicalSemanticIdentityBindingsPropertyName,
            CanonicalUpdateSqlPropertyName,
            CanonicalDeleteSqlPropertyName,
            PlanJsonCanonicalization.NormalizeMultilineText
        );
    }

    public void WriteManifestSummaryJson(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        WriteJson(
            writer,
            ManifestSemanticIdentityBindingsPropertyName,
            ManifestUpdateSqlPropertyName,
            ManifestDeleteSqlPropertyName,
            PlanManifestConventions.ComputeNormalizedSha256
        );
    }

    private void WriteJson(
        Utf8JsonWriter writer,
        string semanticIdentityBindingsPropertyName,
        string updateSqlPropertyName,
        string deleteSqlPropertyName,
        Func<string, string> writeSql
    )
    {
        writer.WriteStartObject();
        writer.WritePropertyName(semanticIdentityBindingsPropertyName);
        writer.WriteStartArray();

        foreach (var semanticIdentityBinding in this.SemanticIdentityBindings)
        {
            writer.WriteStartObject();
            writer.WriteString("relative_path", semanticIdentityBinding.RelativePath);
            writer.WriteNumber("binding_index", semanticIdentityBinding.BindingIndex);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("stable_row_identity_binding_index", this.StableRowIdentityBindingIndex);
        writer.WriteString(updateSqlPropertyName, writeSql(this.UpdateByStableRowIdentitySql));
        writer.WriteString(deleteSqlPropertyName, writeSql(this.DeleteByStableRowIdentitySql));
        writer.WriteNumber("ordinal_binding_index", this.OrdinalBindingIndex);
        writer.WritePropertyName("compare_binding_indexes_in_order");
        writer.WriteStartArray();

        foreach (var bindingIndex in this.CompareBindingIndexesInOrder)
        {
            writer.WriteNumberValue(bindingIndex);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static JsonPathExpression CompileJsonPath(string value, string argumentName)
    {
        try
        {
            return JsonPathExpressionCompiler.Compile(RequireNonEmpty(value, argumentName));
        }
        catch (ArgumentException exception) when (exception.ParamName == "jsonPath")
        {
            throw new ArgumentException($"Invalid canonical JSONPath '{value}'.", argumentName, exception);
        }
    }

    private static int ValidateBindingIndex(int bindingIndex, int count, string argumentName, string context)
    {
        if ((uint)bindingIndex >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(
                argumentName,
                bindingIndex,
                $"Binding index '{bindingIndex}' for {context} is out of range (count: {count})."
            );
        }

        return bindingIndex;
    }

    private static string RequireNonEmpty(string value, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", argumentName);
        }

        return value;
    }
}

internal sealed record CollectionMergeSemanticIdentityBindingDto(string RelativePath, int BindingIndex);

internal abstract record WriteValueSourceDto
{
    public sealed record DocumentId : WriteValueSourceDto;

    public sealed record ParentKeyPart(int Index) : WriteValueSourceDto;

    public sealed record Ordinal : WriteValueSourceDto;

    public sealed record Scalar(string RelativePath, RelationalScalarTypeDto ScalarType)
        : WriteValueSourceDto;

    public sealed record DocumentReference(int BindingIndex) : WriteValueSourceDto;

    public sealed record ReferenceDerived(ReferenceDerivedValueSourceDto ReferenceSource)
        : WriteValueSourceDto;

    public sealed record DescriptorReference(
        QualifiedResourceNameDto DescriptorResource,
        string RelativePath,
        string? DescriptorValuePath = null
    ) : WriteValueSourceDto;

    public sealed record Precomputed : WriteValueSourceDto;
}

internal sealed record KeyUnificationWritePlanDto
{
    public KeyUnificationWritePlanDto(
        string CanonicalColumnName,
        int CanonicalBindingIndex,
        IEnumerable<KeyUnificationMemberWritePlanDto> MembersInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(MembersInOrder);

        this.CanonicalColumnName = CanonicalColumnName;
        this.CanonicalBindingIndex = CanonicalBindingIndex;
        this.MembersInOrder = [.. MembersInOrder];
    }

    public string CanonicalColumnName { get; init; }

    public int CanonicalBindingIndex { get; init; }

    public ImmutableArray<KeyUnificationMemberWritePlanDto> MembersInOrder { get; init; }
}

internal abstract record KeyUnificationMemberWritePlanDto(
    string MemberPathColumnName,
    string RelativePath,
    string? PresenceColumnName,
    int? PresenceBindingIndex,
    bool PresenceIsSynthetic
)
{
    public sealed record ScalarMember(
        string MemberPathColumnName,
        string RelativePath,
        RelationalScalarTypeDto ScalarType,
        string? PresenceColumnName,
        int? PresenceBindingIndex,
        bool PresenceIsSynthetic
    )
        : KeyUnificationMemberWritePlanDto(
            MemberPathColumnName,
            RelativePath,
            PresenceColumnName,
            PresenceBindingIndex,
            PresenceIsSynthetic
        );

    public sealed record DescriptorMember(
        string MemberPathColumnName,
        string RelativePath,
        QualifiedResourceNameDto DescriptorResource,
        string? PresenceColumnName,
        int? PresenceBindingIndex,
        bool PresenceIsSynthetic
    )
        : KeyUnificationMemberWritePlanDto(
            MemberPathColumnName,
            RelativePath,
            PresenceColumnName,
            PresenceBindingIndex,
            PresenceIsSynthetic
        );

    public sealed record ReferenceDerivedMember(
        string MemberPathColumnName,
        string RelativePath,
        ReferenceDerivedValueSourceDto ReferenceSource,
        string? PresenceColumnName,
        int? PresenceBindingIndex,
        bool PresenceIsSynthetic
    )
        : KeyUnificationMemberWritePlanDto(
            MemberPathColumnName,
            RelativePath,
            PresenceColumnName,
            PresenceBindingIndex,
            PresenceIsSynthetic
        );
}

internal sealed record ResourceReadPlanDto
{
    public ResourceReadPlanDto(
        QualifiedResourceNameDto Resource,
        KeysetTableContractDto KeysetTable,
        IEnumerable<TableReadPlanDto> TablePlansInDependencyOrder,
        IEnumerable<ReferenceIdentityProjectionTablePlanDto> ReferenceIdentityProjectionPlansInDependencyOrder,
        IEnumerable<DescriptorProjectionPlanDto> DescriptorProjectionPlansInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(Resource);
        ArgumentNullException.ThrowIfNull(KeysetTable);
        ArgumentNullException.ThrowIfNull(TablePlansInDependencyOrder);
        ArgumentNullException.ThrowIfNull(ReferenceIdentityProjectionPlansInDependencyOrder);
        ArgumentNullException.ThrowIfNull(DescriptorProjectionPlansInOrder);

        this.Resource = Resource;
        this.KeysetTable = KeysetTable;
        this.TablePlansInDependencyOrder = [.. TablePlansInDependencyOrder];
        this.ReferenceIdentityProjectionPlansInDependencyOrder =
        [
            .. ReferenceIdentityProjectionPlansInDependencyOrder,
        ];
        this.DescriptorProjectionPlansInOrder = [.. DescriptorProjectionPlansInOrder];
    }

    public QualifiedResourceNameDto Resource { get; init; }

    public KeysetTableContractDto KeysetTable { get; init; }

    public ImmutableArray<TableReadPlanDto> TablePlansInDependencyOrder { get; init; }

    public ImmutableArray<ReferenceIdentityProjectionTablePlanDto> ReferenceIdentityProjectionPlansInDependencyOrder { get; init; }

    public ImmutableArray<DescriptorProjectionPlanDto> DescriptorProjectionPlansInOrder { get; init; }
}

internal sealed record KeysetTableContractDto(string TempTableName, string DocumentIdColumnName);

internal sealed record TableReadPlanDto(DbTableNameDto Table, string SelectByKeysetSql);

internal sealed record ReferenceIdentityProjectionTablePlanDto
{
    public ReferenceIdentityProjectionTablePlanDto(
        DbTableNameDto Table,
        IEnumerable<ReferenceIdentityProjectionBindingDto> BindingsInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(BindingsInOrder);

        this.Table = Table;
        this.BindingsInOrder = [.. BindingsInOrder];
    }

    public DbTableNameDto Table { get; init; }

    public ImmutableArray<ReferenceIdentityProjectionBindingDto> BindingsInOrder { get; init; }
}

internal sealed record ReferenceIdentityProjectionBindingDto
{
    public ReferenceIdentityProjectionBindingDto(
        bool IsIdentityComponent,
        string ReferenceObjectPath,
        QualifiedResourceNameDto TargetResource,
        int FkColumnOrdinal,
        IEnumerable<ReferenceIdentityProjectionFieldOrdinalDto> IdentityFieldOrdinalsInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(IdentityFieldOrdinalsInOrder);

        this.IsIdentityComponent = IsIdentityComponent;
        this.ReferenceObjectPath = ReferenceObjectPath;
        this.TargetResource = TargetResource;
        this.FkColumnOrdinal = FkColumnOrdinal;
        this.IdentityFieldOrdinalsInOrder = [.. IdentityFieldOrdinalsInOrder];
    }

    public bool IsIdentityComponent { get; init; }

    public string ReferenceObjectPath { get; init; }

    public QualifiedResourceNameDto TargetResource { get; init; }

    public int FkColumnOrdinal { get; init; }

    public ImmutableArray<ReferenceIdentityProjectionFieldOrdinalDto> IdentityFieldOrdinalsInOrder { get; init; }
}

internal sealed record ReferenceIdentityProjectionFieldOrdinalDto(
    string ReferenceJsonPath,
    int ColumnOrdinal
);

internal sealed record DescriptorProjectionPlanDto
{
    public DescriptorProjectionPlanDto(
        string SelectByKeysetSql,
        DescriptorProjectionResultShapeDto ResultShape,
        IEnumerable<DescriptorProjectionSourceDto> SourcesInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(SourcesInOrder);

        this.SelectByKeysetSql = SelectByKeysetSql;
        this.ResultShape = ResultShape;
        this.SourcesInOrder = [.. SourcesInOrder];
    }

    public string SelectByKeysetSql { get; init; }

    public DescriptorProjectionResultShapeDto ResultShape { get; init; }

    public ImmutableArray<DescriptorProjectionSourceDto> SourcesInOrder { get; init; }
}

internal sealed record DescriptorProjectionResultShapeDto(int DescriptorIdOrdinal, int UriOrdinal);

internal sealed record DescriptorProjectionSourceDto(
    string DescriptorValuePath,
    DbTableNameDto Table,
    QualifiedResourceNameDto DescriptorResource,
    int DescriptorIdColumnOrdinal
);

internal sealed record PageDocumentIdSqlPlanDto
{
    public PageDocumentIdSqlPlanDto(
        string PageDocumentIdSql,
        string? TotalCountSql,
        IEnumerable<QuerySqlParameterDto> PageParametersInOrder,
        IEnumerable<QuerySqlParameterDto>? TotalCountParametersInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(PageParametersInOrder);

        this.PageDocumentIdSql = PageDocumentIdSql;
        this.TotalCountSql = TotalCountSql;
        this.PageParametersInOrder = [.. PageParametersInOrder];

        if (TotalCountSql is null)
        {
            if (TotalCountParametersInOrder is not null)
            {
                throw new ArgumentException(
                    $"{nameof(TotalCountParametersInOrder)} must be null when {nameof(TotalCountSql)} is null.",
                    nameof(TotalCountParametersInOrder)
                );
            }

            this.TotalCountParametersInOrder = null;
            return;
        }

        this.TotalCountParametersInOrder =
        [
            .. (
                TotalCountParametersInOrder
                ?? throw new ArgumentNullException(nameof(TotalCountParametersInOrder))
            ),
        ];
    }

    public string PageDocumentIdSql { get; init; }

    public string? TotalCountSql { get; init; }

    public ImmutableArray<QuerySqlParameterDto> PageParametersInOrder { get; init; }

    public ImmutableArray<QuerySqlParameterDto>? TotalCountParametersInOrder { get; init; }
}

internal sealed record QuerySqlParameterDto(QuerySqlParameterRoleDto Role, string ParameterName);

internal enum QuerySqlParameterRoleDto
{
    Filter,
    Offset,
    Limit,
}
