// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using ExternalPlans = EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class NormalizedPlanContractCodec
{
    private static readonly string[] SupportedKeysetTempTableNamesInDeterministicOrder =
    [
        ExternalPlans.KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql).Table.Name,
        ExternalPlans.KeysetTableConventions.GetKeysetTableContract(SqlDialect.Mssql).Table.Name,
    ];

    public static ResourceWritePlanDto Encode(ExternalPlans.ResourceWritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new ResourceWritePlanDto(
            Resource: EncodeQualifiedResourceName(plan.Model.Resource),
            TablePlansInDependencyOrder: plan.TablePlansInDependencyOrder.Select(
                tablePlan => new TableWritePlanDto(
                    Table: EncodeTableName(tablePlan.TableModel.Table),
                    InsertSql: tablePlan.InsertSql,
                    UpdateSql: tablePlan.UpdateSql,
                    DeleteByParentSql: tablePlan.DeleteByParentSql,
                    BulkInsertBatching: new BulkInsertBatchingInfoDto(
                        tablePlan.BulkInsertBatching.MaxRowsPerBatch,
                        tablePlan.BulkInsertBatching.ParametersPerRow,
                        tablePlan.BulkInsertBatching.MaxParametersPerCommand
                    ),
                    ColumnBindings: tablePlan.ColumnBindings.Select(binding => new WriteColumnBindingDto(
                        ColumnName: binding.Column.ColumnName.Value,
                        Source: EncodeWriteValueSource(binding.Source),
                        ParameterName: binding.ParameterName
                    )),
                    KeyUnificationPlans: tablePlan.KeyUnificationPlans.Select(
                        keyUnificationPlan => new KeyUnificationWritePlanDto(
                            CanonicalColumnName: keyUnificationPlan.CanonicalColumn.Value,
                            CanonicalBindingIndex: keyUnificationPlan.CanonicalBindingIndex,
                            MembersInOrder: keyUnificationPlan.MembersInOrder.Select(
                                EncodeKeyUnificationMember
                            )
                        )
                    )
                )
            )
        );
    }

    public static ResourceReadPlanDto Encode(ExternalPlans.ResourceReadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new ResourceReadPlanDto(
            Resource: EncodeQualifiedResourceName(plan.Model.Resource),
            KeysetTable: new KeysetTableContractDto(
                plan.KeysetTable.Table.Name,
                plan.KeysetTable.DocumentIdColumnName.Value
            ),
            TablePlansInDependencyOrder: plan.TablePlansInDependencyOrder.Select(
                tablePlan => new TableReadPlanDto(
                    Table: EncodeTableName(tablePlan.TableModel.Table),
                    SelectByKeysetSql: tablePlan.SelectByKeysetSql
                )
            ),
            ReferenceIdentityProjectionPlansInDependencyOrder: plan.ReferenceIdentityProjectionPlansInDependencyOrder.Select(
                tablePlan => new ReferenceIdentityProjectionTablePlanDto(
                    Table: EncodeTableName(tablePlan.Table),
                    BindingsInOrder: tablePlan.BindingsInOrder.Select(
                        binding => new ReferenceIdentityProjectionBindingDto(
                            IsIdentityComponent: binding.IsIdentityComponent,
                            ReferenceObjectPath: binding.ReferenceObjectPath.Canonical,
                            TargetResource: EncodeQualifiedResourceName(binding.TargetResource),
                            FkColumnOrdinal: binding.FkColumnOrdinal,
                            IdentityFieldOrdinalsInOrder: binding.IdentityFieldOrdinalsInOrder.Select(
                                fieldOrdinal => new ReferenceIdentityProjectionFieldOrdinalDto(
                                    ReferenceJsonPath: fieldOrdinal.ReferenceJsonPath.Canonical,
                                    ColumnOrdinal: fieldOrdinal.ColumnOrdinal
                                )
                            )
                        )
                    )
                )
            ),
            DescriptorProjectionPlansInOrder: plan.DescriptorProjectionPlansInOrder.Select(
                descriptorProjectionPlan => new DescriptorProjectionPlanDto(
                    SelectByKeysetSql: descriptorProjectionPlan.SelectByKeysetSql,
                    ResultShape: new DescriptorProjectionResultShapeDto(
                        descriptorProjectionPlan.ResultShape.DescriptorIdOrdinal,
                        descriptorProjectionPlan.ResultShape.UriOrdinal
                    ),
                    SourcesInOrder: descriptorProjectionPlan.SourcesInOrder.Select(
                        source => new DescriptorProjectionSourceDto(
                            DescriptorValuePath: source.DescriptorValuePath.Canonical,
                            Table: EncodeTableName(source.Table),
                            DescriptorResource: EncodeQualifiedResourceName(source.DescriptorResource),
                            DescriptorIdColumnOrdinal: source.DescriptorIdColumnOrdinal
                        )
                    )
                )
            )
        );
    }

    public static PageDocumentIdSqlPlanDto Encode(ExternalPlans.PageDocumentIdSqlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new PageDocumentIdSqlPlanDto(
            PageDocumentIdSql: plan.PageDocumentIdSql,
            TotalCountSql: plan.TotalCountSql,
            PageParametersInOrder: plan.PageParametersInOrder.Select(parameter => new QuerySqlParameterDto(
                Role: EncodeQuerySqlParameterRole(parameter.Role),
                ParameterName: parameter.ParameterName
            )),
            TotalCountParametersInOrder: plan.TotalCountParametersInOrder is null
                ? null
                : plan.TotalCountParametersInOrder.Value.Select(parameter => new QuerySqlParameterDto(
                    Role: EncodeQuerySqlParameterRole(parameter.Role),
                    ParameterName: parameter.ParameterName
                ))
        );
    }

    public static ExternalPlans.ResourceWritePlan Decode(
        ResourceWritePlanDto dto,
        RelationalResourceModel model
    )
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(model);

        ValidateResourceMatchesModel(dto.Resource, model.Resource, nameof(dto.Resource));

        var tablesByName = BuildTableLookup(model);
        var decodedTablePlans = new ExternalPlans.TableWritePlan[dto.TablePlansInDependencyOrder.Length];

        for (
            var tablePlanIndex = 0;
            tablePlanIndex < dto.TablePlansInDependencyOrder.Length;
            tablePlanIndex++
        )
        {
            var tablePlanDto = dto.TablePlansInDependencyOrder[tablePlanIndex];
            var tablePlanArgument =
                $"{nameof(ResourceWritePlanDto.TablePlansInDependencyOrder)}[{tablePlanIndex}]";
            var tableModel = ResolveTableModel(tablePlanDto.Table, tablesByName, tablePlanArgument);
            var columnsByName = BuildColumnLookup(tableModel);

            var decodedColumnBindings = new ExternalPlans.WriteColumnBinding[
                tablePlanDto.ColumnBindings.Length
            ];

            for (
                var columnBindingIndex = 0;
                columnBindingIndex < tablePlanDto.ColumnBindings.Length;
                columnBindingIndex++
            )
            {
                var columnBindingDto = tablePlanDto.ColumnBindings[columnBindingIndex];
                var columnBindingArgument =
                    $"{tablePlanArgument}.{nameof(TableWritePlanDto.ColumnBindings)}[{columnBindingIndex}]";
                var columnModel = ResolveColumnModel(
                    columnBindingDto.ColumnName,
                    tableModel,
                    columnsByName,
                    $"{columnBindingArgument}.{nameof(WriteColumnBindingDto.ColumnName)}"
                );

                var source = DecodeWriteValueSource(
                    columnBindingDto.Source,
                    model,
                    tableModel,
                    columnModel,
                    $"{columnBindingArgument}.{nameof(WriteColumnBindingDto.Source)}"
                );

                var parameterNameArgument =
                    $"{columnBindingArgument}.{nameof(WriteColumnBindingDto.ParameterName)}";

                PlanSqlWriterExtensions.ValidateBareParameterName(
                    columnBindingDto.ParameterName,
                    parameterNameArgument
                );

                decodedColumnBindings[columnBindingIndex] = new ExternalPlans.WriteColumnBinding(
                    Column: columnModel,
                    Source: source,
                    ParameterName: columnBindingDto.ParameterName
                );
            }

            ValidateUniqueParameterNames(
                decodedColumnBindings.Select(static binding => binding.ParameterName),
                nameof(TableWritePlanDto.ColumnBindings),
                $"table '{FormatTableName(tableModel.Table)}'"
            );

            var decodedKeyUnificationPlans = DecodeKeyUnificationPlans(
                tablePlanDto,
                tableModel,
                columnsByName,
                decodedColumnBindings.Length,
                tablePlanArgument
            );

            decodedTablePlans[tablePlanIndex] = new ExternalPlans.TableWritePlan(
                TableModel: tableModel,
                InsertSql: tablePlanDto.InsertSql,
                UpdateSql: tablePlanDto.UpdateSql,
                DeleteByParentSql: tablePlanDto.DeleteByParentSql,
                BulkInsertBatching: new ExternalPlans.BulkInsertBatchingInfo(
                    MaxRowsPerBatch: tablePlanDto.BulkInsertBatching.MaxRowsPerBatch,
                    ParametersPerRow: tablePlanDto.BulkInsertBatching.ParametersPerRow,
                    MaxParametersPerCommand: tablePlanDto.BulkInsertBatching.MaxParametersPerCommand
                ),
                ColumnBindings: decodedColumnBindings,
                KeyUnificationPlans: decodedKeyUnificationPlans
            );
        }

        return new ExternalPlans.ResourceWritePlan(model, decodedTablePlans);
    }

    public static ExternalPlans.ResourceReadPlan Decode(
        ResourceReadPlanDto dto,
        RelationalResourceModel model
    )
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(model);

        ValidateResourceMatchesModel(dto.Resource, model.Resource, nameof(dto.Resource));

        var tablesByName = BuildTableLookup(model);
        var tablePlans = DecodeTableReadPlans(dto, tablesByName);
        var referenceIdentityProjectionPlans = DecodeReferenceIdentityProjectionPlans(
            dto,
            model,
            tablesByName
        );
        var descriptorProjectionPlans = DecodeDescriptorProjectionPlans(dto, model, tablesByName);

        var tempTableNameArgument =
            $"{nameof(ResourceReadPlanDto.KeysetTable)}.{nameof(KeysetTableContractDto.TempTableName)}";
        var documentIdColumnNameArgument =
            $"{nameof(ResourceReadPlanDto.KeysetTable)}.{nameof(KeysetTableContractDto.DocumentIdColumnName)}";

        var keysetTempTableName = ValidateSupportedKeysetTempTableName(
            dto.KeysetTable.TempTableName,
            tempTableNameArgument
        );

        var keysetDocumentIdColumnName = ValidateSupportedKeysetDocumentIdColumnName(
            dto.KeysetTable.DocumentIdColumnName,
            documentIdColumnNameArgument
        );

        return new ExternalPlans.ResourceReadPlan(
            Model: model,
            KeysetTable: new ExternalPlans.KeysetTableContract(
                Table: new ExternalPlans.SqlRelationRef.TempTable(keysetTempTableName),
                DocumentIdColumnName: new DbColumnName(keysetDocumentIdColumnName)
            ),
            TablePlansInDependencyOrder: tablePlans,
            ReferenceIdentityProjectionPlansInDependencyOrder: referenceIdentityProjectionPlans,
            DescriptorProjectionPlansInOrder: descriptorProjectionPlans
        );
    }

    public static ExternalPlans.PageDocumentIdSqlPlan Decode(PageDocumentIdSqlPlanDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var decodedPageParameters = DecodeQueryParameters(
            dto.PageParametersInOrder,
            nameof(PageDocumentIdSqlPlanDto.PageParametersInOrder)
        );

        ValidateUniqueParameterNames(
            decodedPageParameters.Select(static parameter => parameter.ParameterName),
            nameof(PageDocumentIdSqlPlanDto.PageParametersInOrder),
            "page query plan"
        );
        ValidatePagingRoleInventory(
            decodedPageParameters,
            nameof(PageDocumentIdSqlPlanDto.PageParametersInOrder)
        );

        ExternalPlans.QuerySqlParameter[]? decodedTotalCountParameters;

        if (dto.TotalCountSql is null)
        {
            if (dto.TotalCountParametersInOrder is not null)
            {
                throw new ArgumentException(
                    $"{nameof(PageDocumentIdSqlPlanDto.TotalCountParametersInOrder)} must be null when {nameof(PageDocumentIdSqlPlanDto.TotalCountSql)} is null.",
                    nameof(dto)
                );
            }

            decodedTotalCountParameters = null;
        }
        else
        {
            if (dto.TotalCountParametersInOrder is null)
            {
                throw new ArgumentException(
                    $"{nameof(PageDocumentIdSqlPlanDto.TotalCountParametersInOrder)} is required when {nameof(PageDocumentIdSqlPlanDto.TotalCountSql)} is provided.",
                    nameof(dto)
                );
            }

            decodedTotalCountParameters = DecodeQueryParameters(
                dto.TotalCountParametersInOrder.Value,
                nameof(PageDocumentIdSqlPlanDto.TotalCountParametersInOrder)
            );
            ValidateUniqueParameterNames(
                decodedTotalCountParameters.Select(static parameter => parameter.ParameterName),
                nameof(PageDocumentIdSqlPlanDto.TotalCountParametersInOrder),
                "total-count query plan"
            );
            ValidateFilterOnlyRoleInventory(
                decodedTotalCountParameters,
                nameof(PageDocumentIdSqlPlanDto.TotalCountParametersInOrder)
            );
        }

        return new ExternalPlans.PageDocumentIdSqlPlan(
            PageDocumentIdSql: dto.PageDocumentIdSql,
            TotalCountSql: dto.TotalCountSql,
            PageParametersInOrder: decodedPageParameters,
            TotalCountParametersInOrder: decodedTotalCountParameters
        );
    }

    private static ExternalPlans.QuerySqlParameter[] DecodeQueryParameters(
        IReadOnlyList<QuerySqlParameterDto> parametersInOrder,
        string argumentName
    )
    {
        var decodedParameters = new ExternalPlans.QuerySqlParameter[parametersInOrder.Count];

        for (var index = 0; index < parametersInOrder.Count; index++)
        {
            var parameter = parametersInOrder[index];
            var parameterNameArgument =
                $"{argumentName}[{index}].{nameof(QuerySqlParameterDto.ParameterName)}";

            PlanSqlWriterExtensions.ValidateBareParameterName(parameter.ParameterName, parameterNameArgument);

            decodedParameters[index] = new ExternalPlans.QuerySqlParameter(
                Role: DecodeQuerySqlParameterRole(parameter.Role),
                ParameterName: parameter.ParameterName
            );
        }

        return decodedParameters;
    }

    private static IReadOnlyList<ExternalPlans.TableReadPlan> DecodeTableReadPlans(
        ResourceReadPlanDto dto,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        var decoded = new ExternalPlans.TableReadPlan[dto.TablePlansInDependencyOrder.Length];

        for (var index = 0; index < dto.TablePlansInDependencyOrder.Length; index++)
        {
            var tablePlanDto = dto.TablePlansInDependencyOrder[index];
            var tablePlanArgument = $"{nameof(ResourceReadPlanDto.TablePlansInDependencyOrder)}[{index}]";
            var tableModel = ResolveTableModel(tablePlanDto.Table, tablesByName, tablePlanArgument);

            decoded[index] = new ExternalPlans.TableReadPlan(tableModel, tablePlanDto.SelectByKeysetSql);
        }

        return decoded;
    }

    private static IReadOnlyList<ExternalPlans.ReferenceIdentityProjectionTablePlan> DecodeReferenceIdentityProjectionPlans(
        ResourceReadPlanDto dto,
        RelationalResourceModel model,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        var decoded = new ExternalPlans.ReferenceIdentityProjectionTablePlan[
            dto.ReferenceIdentityProjectionPlansInDependencyOrder.Length
        ];

        for (
            var tablePlanIndex = 0;
            tablePlanIndex < dto.ReferenceIdentityProjectionPlansInDependencyOrder.Length;
            tablePlanIndex++
        )
        {
            var tablePlanDto = dto.ReferenceIdentityProjectionPlansInDependencyOrder[tablePlanIndex];
            var tablePlanArgument =
                $"{nameof(ResourceReadPlanDto.ReferenceIdentityProjectionPlansInDependencyOrder)}[{tablePlanIndex}]";
            var tableModel = ResolveTableModel(tablePlanDto.Table, tablesByName, tablePlanArgument);

            var bindings = new ExternalPlans.ReferenceIdentityProjectionBinding[
                tablePlanDto.BindingsInOrder.Length
            ];

            for (var bindingIndex = 0; bindingIndex < tablePlanDto.BindingsInOrder.Length; bindingIndex++)
            {
                var bindingDto = tablePlanDto.BindingsInOrder[bindingIndex];
                var bindingArgument =
                    $"{tablePlanArgument}.{nameof(ReferenceIdentityProjectionTablePlanDto.BindingsInOrder)}[{bindingIndex}]";

                var fkOrdinalArgument =
                    $"{bindingArgument}.{nameof(ReferenceIdentityProjectionBindingDto.FkColumnOrdinal)}";

                var fkColumnOrdinal = ValidateOrdinal(
                    bindingDto.FkColumnOrdinal,
                    tableModel.Columns.Count,
                    fkOrdinalArgument,
                    $"FK column ordinal on table '{FormatTableName(tableModel.Table)}'"
                );

                var fkColumn = tableModel.Columns[fkColumnOrdinal].ColumnName;
                var (modelBindingIndex, modelBinding) = ResolveDocumentReferenceBindingByTableAndColumn(
                    model,
                    tableModel.Table,
                    fkColumn,
                    bindingArgument
                );

                var referenceObjectPathArgument =
                    $"{bindingArgument}.{nameof(ReferenceIdentityProjectionBindingDto.ReferenceObjectPath)}";
                var referenceObjectPath = CompileJsonPath(
                    bindingDto.ReferenceObjectPath,
                    referenceObjectPathArgument
                );

                if (
                    !string.Equals(
                        referenceObjectPath.Canonical,
                        modelBinding.ReferenceObjectPath.Canonical,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Document-reference binding index '{modelBindingIndex}' has reference-object path "
                            + $"'{modelBinding.ReferenceObjectPath.Canonical}', but "
                            + $"'{referenceObjectPathArgument}' was '{referenceObjectPath.Canonical}'."
                    );
                }

                var targetResource = DecodeQualifiedResourceName(bindingDto.TargetResource);

                if (targetResource != modelBinding.TargetResource)
                {
                    throw new InvalidOperationException(
                        $"Document-reference binding index '{modelBindingIndex}' targets "
                            + $"'{FormatResourceName(modelBinding.TargetResource)}' in model, but "
                            + $"'{bindingArgument}.{nameof(ReferenceIdentityProjectionBindingDto.TargetResource)}' "
                            + $"was '{FormatResourceName(targetResource)}'."
                    );
                }

                var fieldOrdinals = new ExternalPlans.ReferenceIdentityProjectionFieldOrdinal[
                    bindingDto.IdentityFieldOrdinalsInOrder.Length
                ];

                for (
                    var fieldIndex = 0;
                    fieldIndex < bindingDto.IdentityFieldOrdinalsInOrder.Length;
                    fieldIndex++
                )
                {
                    var fieldDto = bindingDto.IdentityFieldOrdinalsInOrder[fieldIndex];
                    var fieldArgument =
                        $"{bindingArgument}.{nameof(ReferenceIdentityProjectionBindingDto.IdentityFieldOrdinalsInOrder)}[{fieldIndex}]";

                    var columnOrdinalArgument =
                        $"{fieldArgument}.{nameof(ReferenceIdentityProjectionFieldOrdinalDto.ColumnOrdinal)}";
                    var columnOrdinal = ValidateOrdinal(
                        fieldDto.ColumnOrdinal,
                        tableModel.Columns.Count,
                        columnOrdinalArgument,
                        $"reference identity field ordinal on table '{FormatTableName(tableModel.Table)}'"
                    );

                    var referenceJsonPathArgument =
                        $"{fieldArgument}.{nameof(ReferenceIdentityProjectionFieldOrdinalDto.ReferenceJsonPath)}";
                    var referenceJsonPath = CompileJsonPath(
                        fieldDto.ReferenceJsonPath,
                        referenceJsonPathArgument
                    );

                    var columnName = tableModel.Columns[columnOrdinal].ColumnName;
                    var hasMatchingIdentityBinding = modelBinding.IdentityBindings.Any(identityBinding =>
                        string.Equals(
                            identityBinding.ReferenceJsonPath.Canonical,
                            referenceJsonPath.Canonical,
                            StringComparison.Ordinal
                        )
                        && identityBinding.Column == columnName
                    );

                    if (!hasMatchingIdentityBinding)
                    {
                        throw new InvalidOperationException(
                            $"Reference identity field '{referenceJsonPath.Canonical}' at ordinal '{columnOrdinal}' "
                                + $"does not match any identity binding for document-reference binding index "
                                + $"'{modelBindingIndex}' on table '{FormatTableName(tableModel.Table)}'."
                        );
                    }

                    fieldOrdinals[fieldIndex] = new ExternalPlans.ReferenceIdentityProjectionFieldOrdinal(
                        ReferenceJsonPath: referenceJsonPath,
                        ColumnOrdinal: columnOrdinal
                    );
                }

                bindings[bindingIndex] = new ExternalPlans.ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: bindingDto.IsIdentityComponent,
                    ReferenceObjectPath: referenceObjectPath,
                    TargetResource: targetResource,
                    FkColumnOrdinal: fkColumnOrdinal,
                    IdentityFieldOrdinalsInOrder: fieldOrdinals
                );
            }

            decoded[tablePlanIndex] = new ExternalPlans.ReferenceIdentityProjectionTablePlan(
                Table: tableModel.Table,
                BindingsInOrder: bindings
            );
        }

        return decoded;
    }

    private static IReadOnlyList<ExternalPlans.DescriptorProjectionPlan> DecodeDescriptorProjectionPlans(
        ResourceReadPlanDto dto,
        RelationalResourceModel model,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        var decoded = new ExternalPlans.DescriptorProjectionPlan[dto.DescriptorProjectionPlansInOrder.Length];

        for (var planIndex = 0; planIndex < dto.DescriptorProjectionPlansInOrder.Length; planIndex++)
        {
            var planDto = dto.DescriptorProjectionPlansInOrder[planIndex];
            var planArgument = $"{nameof(ResourceReadPlanDto.DescriptorProjectionPlansInOrder)}[{planIndex}]";
            var resultShape = new ExternalPlans.DescriptorProjectionResultShape(
                DescriptorIdOrdinal: ValidateNonNegative(
                    planDto.ResultShape.DescriptorIdOrdinal,
                    $"{planArgument}.{nameof(DescriptorProjectionPlanDto.ResultShape)}.{nameof(DescriptorProjectionResultShapeDto.DescriptorIdOrdinal)}",
                    "descriptor result-shape descriptor-id ordinal"
                ),
                UriOrdinal: ValidateNonNegative(
                    planDto.ResultShape.UriOrdinal,
                    $"{planArgument}.{nameof(DescriptorProjectionPlanDto.ResultShape)}.{nameof(DescriptorProjectionResultShapeDto.UriOrdinal)}",
                    "descriptor result-shape URI ordinal"
                )
            );

            ValidateDescriptorProjectionResultShape(
                resultShape,
                $"{planArgument}.{nameof(DescriptorProjectionPlanDto.ResultShape)}"
            );

            var sources = new ExternalPlans.DescriptorProjectionSource[planDto.SourcesInOrder.Length];

            for (var sourceIndex = 0; sourceIndex < planDto.SourcesInOrder.Length; sourceIndex++)
            {
                var sourceDto = planDto.SourcesInOrder[sourceIndex];
                var sourceArgument =
                    $"{planArgument}.{nameof(DescriptorProjectionPlanDto.SourcesInOrder)}[{sourceIndex}]";
                var tableModel = ResolveTableModel(
                    sourceDto.Table,
                    tablesByName,
                    $"{sourceArgument}.{nameof(DescriptorProjectionSourceDto.Table)}"
                );

                var descriptorIdColumnOrdinalArgument =
                    $"{sourceArgument}.{nameof(DescriptorProjectionSourceDto.DescriptorIdColumnOrdinal)}";

                var descriptorIdColumnOrdinal = ValidateOrdinal(
                    sourceDto.DescriptorIdColumnOrdinal,
                    tableModel.Columns.Count,
                    descriptorIdColumnOrdinalArgument,
                    $"descriptor-id column ordinal on table '{FormatTableName(tableModel.Table)}'"
                );

                var descriptorColumn = tableModel.Columns[descriptorIdColumnOrdinal].ColumnName;
                var (descriptorEdgeIndex, descriptorEdgeSource) = ResolveDescriptorEdgeSourceByTableAndColumn(
                    model,
                    tableModel.Table,
                    descriptorColumn,
                    sourceArgument
                );

                var descriptorValuePathArgument =
                    $"{sourceArgument}.{nameof(DescriptorProjectionSourceDto.DescriptorValuePath)}";
                var descriptorValuePath = CompileJsonPath(
                    sourceDto.DescriptorValuePath,
                    descriptorValuePathArgument
                );

                if (
                    !string.Equals(
                        descriptorValuePath.Canonical,
                        descriptorEdgeSource.DescriptorValuePath.Canonical,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Descriptor edge source index '{descriptorEdgeIndex}' has descriptor value path "
                            + $"'{descriptorEdgeSource.DescriptorValuePath.Canonical}', but "
                            + $"'{descriptorValuePathArgument}' was '{descriptorValuePath.Canonical}'."
                    );
                }

                var descriptorResource = DecodeQualifiedResourceName(sourceDto.DescriptorResource);

                if (descriptorResource != descriptorEdgeSource.DescriptorResource)
                {
                    throw new InvalidOperationException(
                        $"Descriptor edge source index '{descriptorEdgeIndex}' targets "
                            + $"'{FormatResourceName(descriptorEdgeSource.DescriptorResource)}' in model, but "
                            + $"'{sourceArgument}.{nameof(DescriptorProjectionSourceDto.DescriptorResource)}' "
                            + $"was '{FormatResourceName(descriptorResource)}'."
                    );
                }

                sources[sourceIndex] = new ExternalPlans.DescriptorProjectionSource(
                    DescriptorValuePath: descriptorValuePath,
                    Table: tableModel.Table,
                    DescriptorResource: descriptorResource,
                    DescriptorIdColumnOrdinal: descriptorIdColumnOrdinal
                );
            }

            decoded[planIndex] = new ExternalPlans.DescriptorProjectionPlan(
                SelectByKeysetSql: planDto.SelectByKeysetSql,
                ResultShape: resultShape,
                SourcesInOrder: sources
            );
        }

        return decoded;
    }

    private static void ValidateDescriptorProjectionResultShape(
        ExternalPlans.DescriptorProjectionResultShape resultShape,
        string argumentName
    )
    {
        if (resultShape is { DescriptorIdOrdinal: 0, UriOrdinal: 1 })
        {
            return;
        }

        throw new ArgumentException(
            "Descriptor projection result shape must expose DescriptorId at ordinal 0 and Uri at ordinal 1.",
            argumentName
        );
    }

    private static IReadOnlyList<ExternalPlans.KeyUnificationWritePlan> DecodeKeyUnificationPlans(
        TableWritePlanDto tablePlanDto,
        DbTableModel tableModel,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnsByName,
        int columnBindingCount,
        string tablePlanArgument
    )
    {
        var decodedPlans = new ExternalPlans.KeyUnificationWritePlan[tablePlanDto.KeyUnificationPlans.Length];

        for (var keyPlanIndex = 0; keyPlanIndex < tablePlanDto.KeyUnificationPlans.Length; keyPlanIndex++)
        {
            var keyPlanDto = tablePlanDto.KeyUnificationPlans[keyPlanIndex];
            var keyPlanArgument =
                $"{tablePlanArgument}.{nameof(TableWritePlanDto.KeyUnificationPlans)}[{keyPlanIndex}]";

            var canonicalColumnModel = ResolveColumnModel(
                keyPlanDto.CanonicalColumnName,
                tableModel,
                columnsByName,
                $"{keyPlanArgument}.{nameof(KeyUnificationWritePlanDto.CanonicalColumnName)}"
            );

            var canonicalBindingIndexArgument =
                $"{keyPlanArgument}.{nameof(KeyUnificationWritePlanDto.CanonicalBindingIndex)}";

            var canonicalBindingIndex = ValidateBindingIndex(
                keyPlanDto.CanonicalBindingIndex,
                columnBindingCount,
                canonicalBindingIndexArgument,
                "canonical binding index"
            );

            var members = new ExternalPlans.KeyUnificationMemberWritePlan[keyPlanDto.MembersInOrder.Length];

            for (var memberIndex = 0; memberIndex < keyPlanDto.MembersInOrder.Length; memberIndex++)
            {
                var memberDto = keyPlanDto.MembersInOrder[memberIndex];
                var memberArgument =
                    $"{keyPlanArgument}.{nameof(KeyUnificationWritePlanDto.MembersInOrder)}[{memberIndex}]";

                var memberPathColumnModel = ResolveColumnModel(
                    memberDto.MemberPathColumnName,
                    tableModel,
                    columnsByName,
                    $"{memberArgument}.{nameof(KeyUnificationMemberWritePlanDto.MemberPathColumnName)}"
                );

                DbColumnName? presenceColumnName = null;

                if (memberDto.PresenceColumnName is not null)
                {
                    presenceColumnName = ResolveColumnModel(
                        memberDto.PresenceColumnName,
                        tableModel,
                        columnsByName,
                        $"{memberArgument}.{nameof(KeyUnificationMemberWritePlanDto.PresenceColumnName)}"
                    ).ColumnName;
                }

                if (memberDto.PresenceBindingIndex is not null && memberDto.PresenceColumnName is null)
                {
                    throw new ArgumentException(
                        "Presence binding index requires a presence column name.",
                        $"{memberArgument}.{nameof(KeyUnificationMemberWritePlanDto.PresenceBindingIndex)}"
                    );
                }

                int? presenceBindingIndex = null;

                if (memberDto.PresenceBindingIndex is not null)
                {
                    var presenceBindingArgument =
                        $"{memberArgument}.{nameof(KeyUnificationMemberWritePlanDto.PresenceBindingIndex)}";

                    presenceBindingIndex = ValidateBindingIndex(
                        memberDto.PresenceBindingIndex.Value,
                        columnBindingCount,
                        presenceBindingArgument,
                        "presence binding index"
                    );
                }

                var relativePath = CompileJsonPath(
                    memberDto.RelativePath,
                    $"{memberArgument}.{nameof(KeyUnificationMemberWritePlanDto.RelativePath)}"
                );

                members[memberIndex] = memberDto switch
                {
                    KeyUnificationMemberWritePlanDto.ScalarMember scalarMember =>
                        new ExternalPlans.KeyUnificationMemberWritePlan.ScalarMember(
                            MemberPathColumn: memberPathColumnModel.ColumnName,
                            RelativePath: relativePath,
                            ScalarType: DecodeScalarType(
                                scalarMember.ScalarType,
                                $"{memberArgument}.{nameof(KeyUnificationMemberWritePlanDto.ScalarMember.ScalarType)}"
                            ),
                            PresenceColumn: presenceColumnName,
                            PresenceBindingIndex: presenceBindingIndex,
                            PresenceIsSynthetic: memberDto.PresenceIsSynthetic
                        ),
                    KeyUnificationMemberWritePlanDto.DescriptorMember descriptorMember =>
                        new ExternalPlans.KeyUnificationMemberWritePlan.DescriptorMember(
                            MemberPathColumn: memberPathColumnModel.ColumnName,
                            RelativePath: relativePath,
                            DescriptorResource: DecodeQualifiedResourceName(
                                descriptorMember.DescriptorResource
                            ),
                            PresenceColumn: presenceColumnName,
                            PresenceBindingIndex: presenceBindingIndex,
                            PresenceIsSynthetic: memberDto.PresenceIsSynthetic
                        ),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(tablePlanDto),
                        memberDto.GetType().Name,
                        "Unsupported key-unification member DTO kind."
                    ),
                };
            }

            decodedPlans[keyPlanIndex] = new ExternalPlans.KeyUnificationWritePlan(
                CanonicalColumn: canonicalColumnModel.ColumnName,
                CanonicalBindingIndex: canonicalBindingIndex,
                MembersInOrder: members
            );
        }

        return decodedPlans;
    }

    private static ExternalPlans.WriteValueSource DecodeWriteValueSource(
        WriteValueSourceDto sourceDto,
        RelationalResourceModel model,
        DbTableModel tableModel,
        DbColumnModel columnModel,
        string argumentName
    )
    {
        ArgumentNullException.ThrowIfNull(sourceDto);

        return sourceDto switch
        {
            WriteValueSourceDto.DocumentId => new ExternalPlans.WriteValueSource.DocumentId(),
            WriteValueSourceDto.ParentKeyPart parentKeyPart =>
                new ExternalPlans.WriteValueSource.ParentKeyPart(
                    ValidateNonNegative(
                        parentKeyPart.Index,
                        $"{argumentName}.{nameof(WriteValueSourceDto.ParentKeyPart.Index)}",
                        "parent key part index"
                    )
                ),
            WriteValueSourceDto.Ordinal => new ExternalPlans.WriteValueSource.Ordinal(),
            WriteValueSourceDto.Scalar scalar => new ExternalPlans.WriteValueSource.Scalar(
                RelativePath: CompileJsonPath(
                    scalar.RelativePath,
                    $"{argumentName}.{nameof(WriteValueSourceDto.Scalar.RelativePath)}"
                ),
                Type: DecodeScalarType(
                    scalar.ScalarType,
                    $"{argumentName}.{nameof(WriteValueSourceDto.Scalar.ScalarType)}"
                )
            ),
            WriteValueSourceDto.DocumentReference documentReference => DecodeDocumentReferenceSource(
                documentReference,
                model,
                tableModel,
                columnModel,
                argumentName
            ),
            WriteValueSourceDto.DescriptorReference descriptorReference =>
                new ExternalPlans.WriteValueSource.DescriptorReference(
                    DescriptorResource: DecodeQualifiedResourceName(descriptorReference.DescriptorResource),
                    RelativePath: CompileJsonPath(
                        descriptorReference.RelativePath,
                        $"{argumentName}.{nameof(WriteValueSourceDto.DescriptorReference.RelativePath)}"
                    ),
                    DescriptorValuePath: descriptorReference.DescriptorValuePath is null
                        ? null
                        : CompileJsonPath(
                            descriptorReference.DescriptorValuePath,
                            $"{argumentName}.{nameof(WriteValueSourceDto.DescriptorReference.DescriptorValuePath)}"
                        )
                ),
            WriteValueSourceDto.Precomputed => new ExternalPlans.WriteValueSource.Precomputed(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(sourceDto),
                sourceDto.GetType().Name,
                "Unsupported write value source DTO kind."
            ),
        };
    }

    private static ExternalPlans.WriteValueSource.DocumentReference DecodeDocumentReferenceSource(
        WriteValueSourceDto.DocumentReference source,
        RelationalResourceModel model,
        DbTableModel tableModel,
        DbColumnModel columnModel,
        string argumentName
    )
    {
        var binding = ResolveDocumentReferenceBindingByIndex(model, source.BindingIndex, argumentName);

        if (binding.Table != tableModel.Table || binding.FkColumn != columnModel.ColumnName)
        {
            throw new InvalidOperationException(
                $"Document-reference binding index '{source.BindingIndex}' points to "
                    + $"'{FormatTableName(binding.Table)}.{binding.FkColumn.Value}', but "
                    + $"'{argumentName}' targets '{FormatTableName(tableModel.Table)}.{columnModel.ColumnName.Value}'."
            );
        }

        var columnPath = columnModel.SourceJsonPath?.Canonical;

        if (columnPath is null)
        {
            throw new InvalidOperationException(
                $"Column '{columnModel.ColumnName.Value}' on table '{FormatTableName(tableModel.Table)}' "
                    + $"has no source JSONPath to validate document-reference binding index "
                    + $"'{source.BindingIndex}'."
            );
        }

        if (!string.Equals(columnPath, binding.ReferenceObjectPath.Canonical, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Document-reference binding index '{source.BindingIndex}' has reference-object path "
                    + $"'{binding.ReferenceObjectPath.Canonical}', but bound column path was '{columnPath}'."
            );
        }

        return new ExternalPlans.WriteValueSource.DocumentReference(source.BindingIndex);
    }

    private static WriteValueSourceDto EncodeWriteValueSource(ExternalPlans.WriteValueSource source)
    {
        return source switch
        {
            ExternalPlans.WriteValueSource.DocumentId => new WriteValueSourceDto.DocumentId(),
            ExternalPlans.WriteValueSource.ParentKeyPart parentKeyPart =>
                new WriteValueSourceDto.ParentKeyPart(parentKeyPart.Index),
            ExternalPlans.WriteValueSource.Ordinal => new WriteValueSourceDto.Ordinal(),
            ExternalPlans.WriteValueSource.Scalar scalar => new WriteValueSourceDto.Scalar(
                RelativePath: scalar.RelativePath.Canonical,
                ScalarType: EncodeScalarType(scalar.Type)
            ),
            ExternalPlans.WriteValueSource.DocumentReference documentReference =>
                new WriteValueSourceDto.DocumentReference(documentReference.BindingIndex),
            ExternalPlans.WriteValueSource.DescriptorReference descriptorReference =>
                new WriteValueSourceDto.DescriptorReference(
                    DescriptorResource: EncodeQualifiedResourceName(descriptorReference.DescriptorResource),
                    RelativePath: descriptorReference.RelativePath.Canonical,
                    DescriptorValuePath: descriptorReference.DescriptorValuePath?.Canonical
                ),
            ExternalPlans.WriteValueSource.Precomputed => new WriteValueSourceDto.Precomputed(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(source),
                source.GetType().Name,
                "Unsupported write value source kind."
            ),
        };
    }

    private static KeyUnificationMemberWritePlanDto EncodeKeyUnificationMember(
        ExternalPlans.KeyUnificationMemberWritePlan member
    )
    {
        return member switch
        {
            ExternalPlans.KeyUnificationMemberWritePlan.ScalarMember scalarMember =>
                new KeyUnificationMemberWritePlanDto.ScalarMember(
                    MemberPathColumnName: scalarMember.MemberPathColumn.Value,
                    RelativePath: scalarMember.RelativePath.Canonical,
                    ScalarType: EncodeScalarType(scalarMember.ScalarType),
                    PresenceColumnName: scalarMember.PresenceColumn?.Value,
                    PresenceBindingIndex: scalarMember.PresenceBindingIndex,
                    PresenceIsSynthetic: scalarMember.PresenceIsSynthetic
                ),
            ExternalPlans.KeyUnificationMemberWritePlan.DescriptorMember descriptorMember =>
                new KeyUnificationMemberWritePlanDto.DescriptorMember(
                    MemberPathColumnName: descriptorMember.MemberPathColumn.Value,
                    RelativePath: descriptorMember.RelativePath.Canonical,
                    DescriptorResource: EncodeQualifiedResourceName(descriptorMember.DescriptorResource),
                    PresenceColumnName: descriptorMember.PresenceColumn?.Value,
                    PresenceBindingIndex: descriptorMember.PresenceBindingIndex,
                    PresenceIsSynthetic: descriptorMember.PresenceIsSynthetic
                ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(member),
                member.GetType().Name,
                "Unsupported key-unification member kind."
            ),
        };
    }

    private static QuerySqlParameterRoleDto EncodeQuerySqlParameterRole(
        ExternalPlans.QuerySqlParameterRole role
    )
    {
        return role switch
        {
            ExternalPlans.QuerySqlParameterRole.Filter => QuerySqlParameterRoleDto.Filter,
            ExternalPlans.QuerySqlParameterRole.Offset => QuerySqlParameterRoleDto.Offset,
            ExternalPlans.QuerySqlParameterRole.Limit => QuerySqlParameterRoleDto.Limit,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported query role."),
        };
    }

    private static ExternalPlans.QuerySqlParameterRole DecodeQuerySqlParameterRole(
        QuerySqlParameterRoleDto role
    )
    {
        return role switch
        {
            QuerySqlParameterRoleDto.Filter => ExternalPlans.QuerySqlParameterRole.Filter,
            QuerySqlParameterRoleDto.Offset => ExternalPlans.QuerySqlParameterRole.Offset,
            QuerySqlParameterRoleDto.Limit => ExternalPlans.QuerySqlParameterRole.Limit,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported query role DTO."),
        };
    }

    private static RelationalScalarTypeDto EncodeScalarType(RelationalScalarType scalarType)
    {
        return new RelationalScalarTypeDto(
            Kind: EncodeScalarKind(scalarType.Kind),
            MaxLength: scalarType.MaxLength,
            DecimalPrecision: scalarType.Decimal?.Precision,
            DecimalScale: scalarType.Decimal?.Scale
        );
    }

    private static RelationalScalarType DecodeScalarType(
        RelationalScalarTypeDto scalarType,
        string argumentName
    )
    {
        ArgumentNullException.ThrowIfNull(scalarType);

        if ((scalarType.DecimalPrecision is null) != (scalarType.DecimalScale is null))
        {
            throw new ArgumentException(
                "Decimal precision and scale must be provided together or both omitted.",
                argumentName
            );
        }

        return new RelationalScalarType(
            Kind: DecodeScalarKind(scalarType.Kind),
            MaxLength: scalarType.MaxLength,
            Decimal: scalarType.DecimalPrecision is null
                ? null
                : (scalarType.DecimalPrecision.Value, scalarType.DecimalScale!.Value)
        );
    }

    private static NormalizedScalarKind EncodeScalarKind(ScalarKind kind)
    {
        return kind switch
        {
            ScalarKind.String => NormalizedScalarKind.String,
            ScalarKind.Int32 => NormalizedScalarKind.Int32,
            ScalarKind.Int64 => NormalizedScalarKind.Int64,
            ScalarKind.Decimal => NormalizedScalarKind.Decimal,
            ScalarKind.Boolean => NormalizedScalarKind.Boolean,
            ScalarKind.Date => NormalizedScalarKind.Date,
            ScalarKind.DateTime => NormalizedScalarKind.DateTime,
            ScalarKind.Time => NormalizedScalarKind.Time,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported scalar kind."),
        };
    }

    private static ScalarKind DecodeScalarKind(NormalizedScalarKind kind)
    {
        return kind switch
        {
            NormalizedScalarKind.String => ScalarKind.String,
            NormalizedScalarKind.Int32 => ScalarKind.Int32,
            NormalizedScalarKind.Int64 => ScalarKind.Int64,
            NormalizedScalarKind.Decimal => ScalarKind.Decimal,
            NormalizedScalarKind.Boolean => ScalarKind.Boolean,
            NormalizedScalarKind.Date => ScalarKind.Date,
            NormalizedScalarKind.DateTime => ScalarKind.DateTime,
            NormalizedScalarKind.Time => ScalarKind.Time,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported scalar kind DTO."),
        };
    }

    private static QualifiedResourceNameDto EncodeQualifiedResourceName(QualifiedResourceName value)
    {
        return new QualifiedResourceNameDto(value.ProjectName, value.ResourceName);
    }

    private static QualifiedResourceName DecodeQualifiedResourceName(QualifiedResourceNameDto value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new QualifiedResourceName(
            ProjectName: RequireNonEmpty(value.ProjectName, nameof(QualifiedResourceNameDto.ProjectName)),
            ResourceName: RequireNonEmpty(value.ResourceName, nameof(QualifiedResourceNameDto.ResourceName))
        );
    }

    private static DbTableNameDto EncodeTableName(DbTableName tableName)
    {
        return new DbTableNameDto(tableName.Schema.Value, tableName.Name);
    }

    private static DbTableName DecodeTableName(DbTableNameDto tableName, string argumentName)
    {
        ArgumentNullException.ThrowIfNull(tableName);

        var schema = RequireNonEmpty(tableName.Schema, $"{argumentName}.{nameof(DbTableNameDto.Schema)}");
        var name = RequireNonEmpty(tableName.Name, $"{argumentName}.{nameof(DbTableNameDto.Name)}");

        return new DbTableName(new DbSchemaName(schema), name);
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

    private static void ValidateResourceMatchesModel(
        QualifiedResourceNameDto dtoResource,
        QualifiedResourceName modelResource,
        string argumentName
    )
    {
        var decodedResource = DecodeQualifiedResourceName(dtoResource);

        if (decodedResource != modelResource)
        {
            throw new ArgumentException(
                $"DTO resource '{FormatResourceName(decodedResource)}' does not match model "
                    + $"resource '{FormatResourceName(modelResource)}'.",
                argumentName
            );
        }
    }

    private static IReadOnlyDictionary<DbTableName, DbTableModel> BuildTableLookup(
        RelationalResourceModel model
    )
    {
        var tablesByName = new Dictionary<DbTableName, DbTableModel> { [model.Root.Table] = model.Root };

        foreach (var tableModel in model.TablesInDependencyOrder)
        {
            tablesByName.TryAdd(tableModel.Table, tableModel);
        }

        return tablesByName;
    }

    private static IReadOnlyDictionary<DbColumnName, DbColumnModel> BuildColumnLookup(DbTableModel tableModel)
    {
        var columnsByName = new Dictionary<DbColumnName, DbColumnModel>(tableModel.Columns.Count);

        foreach (var columnModel in tableModel.Columns)
        {
            if (!columnsByName.TryAdd(columnModel.ColumnName, columnModel))
            {
                throw new InvalidOperationException(
                    $"Table '{FormatTableName(tableModel.Table)}' has duplicate column name "
                        + $"'{columnModel.ColumnName.Value}' in the relational model."
                );
            }
        }

        return columnsByName;
    }

    private static DbTableModel ResolveTableModel(
        DbTableNameDto table,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName,
        string argumentName
    )
    {
        var tableName = DecodeTableName(table, argumentName);

        if (tablesByName.TryGetValue(tableName, out var tableModel))
        {
            return tableModel;
        }

        throw new ArgumentException(
            $"Unknown table '{FormatTableName(tableName)}' referenced by '{argumentName}'.",
            argumentName
        );
    }

    private static DbColumnModel ResolveColumnModel(
        string columnName,
        DbTableModel tableModel,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnsByName,
        string argumentName
    )
    {
        var nonEmptyColumnName = RequireNonEmpty(columnName, argumentName);
        var dbColumnName = new DbColumnName(nonEmptyColumnName);

        if (columnsByName.TryGetValue(dbColumnName, out var columnModel))
        {
            return columnModel;
        }

        throw new ArgumentException(
            $"Unknown column '{nonEmptyColumnName}' on table '{FormatTableName(tableModel.Table)}' "
                + $"referenced by '{argumentName}'.",
            argumentName
        );
    }

    private static DocumentReferenceBinding ResolveDocumentReferenceBindingByIndex(
        RelationalResourceModel model,
        int bindingIndex,
        string context
    )
    {
        if ((uint)bindingIndex >= (uint)model.DocumentReferenceBindings.Count)
        {
            throw new ArgumentOutOfRangeException(
                "bindingIndex",
                bindingIndex,
                $"Document-reference binding index '{bindingIndex}' at '{context}' is out of range "
                    + $"for '{nameof(RelationalResourceModel.DocumentReferenceBindings)}' "
                    + $"(count: {model.DocumentReferenceBindings.Count})."
            );
        }

        return model.DocumentReferenceBindings[bindingIndex];
    }

    private static (
        int BindingIndex,
        DocumentReferenceBinding Binding
    ) ResolveDocumentReferenceBindingByTableAndColumn(
        RelationalResourceModel model,
        DbTableName table,
        DbColumnName fkColumn,
        string context
    )
    {
        var bindingIndex = -1;
        DocumentReferenceBinding binding = null!;

        for (var index = 0; index < model.DocumentReferenceBindings.Count; index++)
        {
            var candidate = model.DocumentReferenceBindings[index];

            if (candidate.Table != table || candidate.FkColumn != fkColumn)
            {
                continue;
            }

            if (bindingIndex >= 0)
            {
                throw new InvalidOperationException(
                    $"Multiple document-reference bindings were found for "
                        + $"'{FormatTableName(table)}.{fkColumn.Value}' while decoding '{context}'."
                );
            }

            bindingIndex = index;
            binding = candidate;
        }

        if (bindingIndex < 0)
        {
            throw new InvalidOperationException(
                $"No document-reference binding was found for '{FormatTableName(table)}.{fkColumn.Value}' "
                    + $"while decoding '{context}'."
            );
        }

        return (bindingIndex, binding);
    }

    private static (
        int DescriptorEdgeIndex,
        DescriptorEdgeSource Source
    ) ResolveDescriptorEdgeSourceByTableAndColumn(
        RelationalResourceModel model,
        DbTableName table,
        DbColumnName fkColumn,
        string context
    )
    {
        var descriptorEdgeIndex = -1;
        DescriptorEdgeSource source = null!;

        for (var index = 0; index < model.DescriptorEdgeSources.Count; index++)
        {
            var candidate = model.DescriptorEdgeSources[index];

            if (candidate.Table != table || candidate.FkColumn != fkColumn)
            {
                continue;
            }

            if (descriptorEdgeIndex >= 0)
            {
                throw new InvalidOperationException(
                    $"Multiple descriptor edge sources were found for "
                        + $"'{FormatTableName(table)}.{fkColumn.Value}' while decoding '{context}'."
                );
            }

            descriptorEdgeIndex = index;
            source = candidate;
        }

        if (descriptorEdgeIndex < 0)
        {
            throw new InvalidOperationException(
                $"No descriptor edge source was found for '{FormatTableName(table)}.{fkColumn.Value}' "
                    + $"while decoding '{context}'."
            );
        }

        return (descriptorEdgeIndex, source);
    }

    private static int ValidateOrdinal(int ordinal, int count, string argumentName, string context)
    {
        if ((uint)ordinal >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(
                argumentName,
                ordinal,
                $"Ordinal '{ordinal}' for {context} is out of range (count: {count})."
            );
        }

        return ordinal;
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

    private static int ValidateNonNegative(int value, string argumentName, string context)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                argumentName,
                value,
                $"Value for {context} cannot be negative."
            );
        }

        return value;
    }

    private static void ValidateUniqueParameterNames(
        IEnumerable<string> parameterNames,
        string argumentName,
        string context
    )
    {
        var duplicateGroups = parameterNames
            .GroupBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.OrderBy(static name => name, StringComparer.Ordinal).ToArray())
            .OrderBy(static group => group[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group[0], StringComparer.Ordinal)
            .ToArray();

        if (duplicateGroups.Length == 0)
        {
            return;
        }

        var formattedDuplicateGroups = string.Join(
            ", ",
            duplicateGroups.Select(static group =>
                $"[{string.Join(", ", group.Select(static name => $"'{name}'"))}]"
            )
        );

        throw new ArgumentException(
            $"Duplicate parameter names are not allowed (case-insensitive) in {context}. "
                + $"Colliding names: [{formattedDuplicateGroups}].",
            argumentName
        );
    }

    private static void ValidatePagingRoleInventory(
        IReadOnlyList<ExternalPlans.QuerySqlParameter> parametersInOrder,
        string argumentName
    )
    {
        var offsetCount = 0;
        var limitCount = 0;

        foreach (var parameter in parametersInOrder)
        {
            switch (parameter.Role)
            {
                case ExternalPlans.QuerySqlParameterRole.Offset:
                    offsetCount++;
                    break;
                case ExternalPlans.QuerySqlParameterRole.Limit:
                    limitCount++;
                    break;
            }
        }

        if (offsetCount == 1 && limitCount == 1)
        {
            return;
        }

        throw new ArgumentException(
            "Query plan parameters must include exactly one Offset and one Limit role entry. "
                + $"Observed counts: Offset={offsetCount}, Limit={limitCount}.",
            argumentName
        );
    }

    private static void ValidateFilterOnlyRoleInventory(
        IReadOnlyList<ExternalPlans.QuerySqlParameter> parametersInOrder,
        string argumentName
    )
    {
        var invalidRoleCounts = parametersInOrder
            .Where(static parameter => parameter.Role is not ExternalPlans.QuerySqlParameterRole.Filter)
            .GroupBy(static parameter => parameter.Role)
            .Select(static group => $"{group.Key}={group.Count()}")
            .OrderBy(static summary => summary, StringComparer.Ordinal)
            .ToArray();

        if (invalidRoleCounts.Length == 0)
        {
            return;
        }

        throw new ArgumentException(
            "Total-count query plan parameters may only include Filter role entries. "
                + $"Observed non-filter counts: {string.Join(", ", invalidRoleCounts)}.",
            argumentName
        );
    }

    private static string RequireNonEmpty(string value, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", argumentName);
        }

        return value;
    }

    private static string ValidateSupportedKeysetTempTableName(string value, string argumentName)
    {
        var keysetTempTableName = RequireNonEmpty(value, argumentName);

        foreach (var supportedKeysetTempTableName in SupportedKeysetTempTableNamesInDeterministicOrder)
        {
            if (string.Equals(keysetTempTableName, supportedKeysetTempTableName, StringComparison.Ordinal))
            {
                return keysetTempTableName;
            }
        }

        var pgsqlKeysetTableName = SupportedKeysetTempTableNamesInDeterministicOrder[0];
        var mssqlKeysetTableName = SupportedKeysetTempTableNamesInDeterministicOrder[1];

        throw new ArgumentException(
            $"Unsupported keyset temp table name '{keysetTempTableName}'. "
                + $"Supported names are '{pgsqlKeysetTableName}' and '{mssqlKeysetTableName}'.",
            argumentName
        );
    }

    private static string ValidateSupportedKeysetDocumentIdColumnName(string value, string argumentName)
    {
        const string supportedColumnName = "DocumentId";

        var keysetDocumentIdColumnName = RequireNonEmpty(value, argumentName);

        if (string.Equals(keysetDocumentIdColumnName, supportedColumnName, StringComparison.Ordinal))
        {
            return keysetDocumentIdColumnName;
        }

        throw new ArgumentException(
            $"Unsupported keyset DocumentId column name '{keysetDocumentIdColumnName}'. "
                + $"Supported names are '{supportedColumnName}'.",
            argumentName
        );
    }

    private static string FormatTableName(DbTableName tableName)
    {
        return $"{tableName.Schema.Value}.{tableName.Name}";
    }

    private static string FormatResourceName(QualifiedResourceName resourceName)
    {
        return $"{resourceName.ProjectName}.{resourceName.ResourceName}";
    }
}
