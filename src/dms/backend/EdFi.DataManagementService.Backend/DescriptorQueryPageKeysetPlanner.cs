// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal sealed class DescriptorQueryPageKeysetPlanner(SqlDialect dialect)
{
    private const string OffsetParameterName = "offset";
    private const string LimitParameterName = "limit";
    private const string ResourceKeyIdParameterName = "resourceKeyId";
    private const string ContentVersionColumnName = ChangeVersionFilterConstants.ContentVersionColumnName;
    private const string MinChangeVersionParameterName =
        ChangeVersionFilterConstants.MinChangeVersionParameterName;
    private const string MaxChangeVersionParameterName =
        ChangeVersionFilterConstants.MaxChangeVersionParameterName;
    private static readonly DbTableName _descriptorTable = new(new DbSchemaName("dms"), "Descriptor");
    private readonly PageDocumentIdSqlCompiler _sqlCompiler = new(dialect);

    public PageKeysetSpec.Query Plan(
        MappingSet mappingSet,
        QualifiedResourceName requestResource,
        DescriptorQueryPreprocessingResult preprocessingResult,
        PaginationParameters paginationParameters,
        PageDocumentIdAuthorizationSpec? authorization = null,
        ChangeVersionRange? changeVersionRange = null
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(preprocessingResult);

        if (preprocessingResult.Outcome is not RelationalQueryPreprocessingOutcome.Continue)
        {
            throw new ArgumentException(
                "Descriptor query page planning requires preprocessing results in the continue state.",
                nameof(preprocessingResult)
            );
        }

        var parameterNamesByIndex = DeriveParameterNames(
            preprocessingResult.QueryElementsInOrder,
            authorization
        );
        var queryPredicates = PlanPredicates(preprocessingResult.QueryElementsInOrder, parameterNamesByIndex);
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, requestResource);

        // The page keyset roots on dms.Descriptor, whose denormalized ResourceKeyId carries the
        // same project-qualified type authority descriptor GET-by-id reads from dms.Document.
        // Descriptor field filters land on the root; only ?id= joins dms.Document.
        List<QueryValuePredicate> predicates =
        [
            new QueryValuePredicate(
                new DbColumnName("ResourceKeyId"),
                QueryComparisonOperator.Equal,
                ResourceKeyIdParameterName
            ),
            .. queryPredicates,
        ];
        AppendChangeVersionPredicates(changeVersionRange, predicates);

        var pageQuerySpec = new PageDocumentIdQuerySpec(
            RootTable: _descriptorTable,
            Predicates: predicates,
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            OffsetParameterName: OffsetParameterName,
            LimitParameterName: LimitParameterName,
            IncludeTotalCountSql: paginationParameters.TotalCount,
            Authorization: authorization
        );
        var sqlPlan = _sqlCompiler.Compile(pageQuerySpec);
        var parameterValues = BuildParameterValues(
            resourceKeyId,
            preprocessingResult.QueryElementsInOrder,
            parameterNamesByIndex,
            paginationParameters,
            authorization,
            changeVersionRange
        );

        return new PageKeysetSpec.Query(sqlPlan, parameterValues);
    }

    /// <summary>
    /// Appends <c>ContentVersion</c> range predicates for the validated change-version window
    /// against the page keyset root. The root is <c>dms.Descriptor</c>, whose ContentVersion
    /// mirror the DMS-1173 stamping triggers keep in lock-step with the canonical
    /// <c>dms.Document.ContentVersion</c> — the same root-mirror filtering regular resources use.
    /// </summary>
    private static void AppendChangeVersionPredicates(
        ChangeVersionRange? changeVersionRange,
        List<QueryValuePredicate> predicates
    )
    {
        if (changeVersionRange is null)
        {
            return;
        }

        if (changeVersionRange.MinChangeVersion is not null)
        {
            predicates.Add(
                new QueryValuePredicate(
                    new DbColumnName(ContentVersionColumnName),
                    QueryComparisonOperator.GreaterThanOrEqual,
                    MinChangeVersionParameterName,
                    ScalarKind.Int64
                )
            );
        }

        if (changeVersionRange.MaxChangeVersion is not null)
        {
            predicates.Add(
                new QueryValuePredicate(
                    new DbColumnName(ContentVersionColumnName),
                    QueryComparisonOperator.LessThanOrEqual,
                    MaxChangeVersionParameterName,
                    ScalarKind.Int64
                )
            );
        }
    }

    private static IReadOnlyList<QueryValuePredicate> PlanPredicates(
        IReadOnlyList<PreprocessedDescriptorQueryElement> queryElementsInOrder,
        IReadOnlyList<string> parameterNamesByIndex
    )
    {
        var predicates = new QueryValuePredicate[queryElementsInOrder.Count];

        for (var index = 0; index < queryElementsInOrder.Count; index++)
        {
            var queryElement =
                queryElementsInOrder[index]
                ?? throw new ArgumentException(
                    "Query elements must not contain null entries.",
                    nameof(queryElementsInOrder)
                );

            predicates[index] = PlanPredicate(queryElement, parameterNamesByIndex[index]);
        }

        return predicates;
    }

    private static QueryValuePredicate PlanPredicate(
        PreprocessedDescriptorQueryElement queryElement,
        string parameterName
    )
    {
        ValidatePreprocessedValueKindOrThrow(queryElement);

        return queryElement.SupportedField.ValueKind switch
        {
            // The ?id= filter targets dms.Document.DocumentUuid, which is not on the
            // dms.Descriptor root, so the DocumentUuid target makes the compiler emit the
            // shared document join.
            DescriptorQueryValueKind.DocumentUuid => new QueryValuePredicate(
                new QueryPredicateTarget.DocumentUuid(),
                QueryComparisonOperator.Equal,
                parameterName
            ),
            DescriptorQueryValueKind.String or DescriptorQueryValueKind.Date =>
                CreateDescriptorColumnPredicate(queryElement.SupportedField, parameterName),
            _ => throw new InvalidOperationException(
                $"Descriptor query page planning does not recognize supported value kind "
                    + $"'{queryElement.SupportedField.ValueKind}' for query field "
                    + $"'{queryElement.SupportedField.QueryFieldName}'."
            ),
        };
    }

    private static QueryValuePredicate CreateDescriptorColumnPredicate(
        SupportedDescriptorQueryField supportedField,
        string parameterName
    )
    {
        var descriptorColumn =
            supportedField.DescriptorColumn
            ?? throw new InvalidOperationException(
                $"Descriptor query page planning requires descriptor column metadata for query field "
                    + $"'{supportedField.QueryFieldName}' with value kind '{supportedField.ValueKind}'."
            );
        var scalarKind =
            supportedField.ScalarKind
            ?? throw new InvalidOperationException(
                $"Descriptor query page planning requires scalar metadata for query field "
                    + $"'{supportedField.QueryFieldName}' with value kind '{supportedField.ValueKind}'."
            );

        // Descriptor columns live on the dms.Descriptor page keyset root itself.
        return new QueryValuePredicate(
            descriptorColumn,
            QueryComparisonOperator.Equal,
            parameterName,
            scalarKind
        );
    }

    private static void ValidatePreprocessedValueKindOrThrow(PreprocessedDescriptorQueryElement queryElement)
    {
        var supportedField = queryElement.SupportedField;
        var isCompatible = supportedField.ValueKind switch
        {
            DescriptorQueryValueKind.DocumentUuid => queryElement.Value
                is PreprocessedDescriptorQueryValue.DocumentUuid,
            DescriptorQueryValueKind.String => queryElement.Value is PreprocessedDescriptorQueryValue.Raw,
            DescriptorQueryValueKind.Date => queryElement.Value
                is PreprocessedDescriptorQueryValue.DateOnlyValue,
            _ => false,
        };

        if (isCompatible)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Descriptor query page planning expected preprocessed value kind '{supportedField.ValueKind}' "
                + $"for query field '{supportedField.QueryFieldName}', but received "
                + $"'{queryElement.Value.GetType().Name}'."
        );
    }

    private static IReadOnlyDictionary<string, object?> BuildParameterValues(
        short resourceKeyId,
        IReadOnlyList<PreprocessedDescriptorQueryElement> queryElementsInOrder,
        IReadOnlyList<string> parameterNamesByIndex,
        PaginationParameters paginationParameters,
        PageDocumentIdAuthorizationSpec? authorization,
        ChangeVersionRange? changeVersionRange
    )
    {
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal)
        {
            [ResourceKeyIdParameterName] = resourceKeyId,
            [OffsetParameterName] = (long)(paginationParameters.Offset ?? 0),
            [LimitParameterName] = (long)(paginationParameters.Limit ?? paginationParameters.MaximumPageSize),
        };

        if (changeVersionRange?.MinChangeVersion is { } minChangeVersion)
        {
            parameterValues[MinChangeVersionParameterName] = minChangeVersion;
        }

        if (changeVersionRange?.MaxChangeVersion is { } maxChangeVersion)
        {
            parameterValues[MaxChangeVersionParameterName] = maxChangeVersion;
        }

        for (var index = 0; index < queryElementsInOrder.Count; index++)
        {
            var queryElement = queryElementsInOrder[index];
            parameterValues[parameterNamesByIndex[index]] = queryElement.Value switch
            {
                PreprocessedDescriptorQueryValue.Raw(var rawValue) => rawValue,
                PreprocessedDescriptorQueryValue.DocumentUuid(var documentUuid) => documentUuid,
                PreprocessedDescriptorQueryValue.DateOnlyValue(var dateOnlyValue) => dateOnlyValue,
                _ => throw new InvalidOperationException(
                    $"Descriptor query page planning does not recognize preprocessed value type "
                        + $"'{queryElement.Value.GetType().Name}' for query field "
                        + $"'{queryElement.SupportedField.QueryFieldName}'."
                ),
            };
        }

        NamespacePrefixParameterValueBinder.Bind(
            parameterValues,
            authorization?.NamespacePrefixParameterization
        );

        return parameterValues;
    }

    private static IReadOnlyList<string> DeriveParameterNames(
        IReadOnlyList<PreprocessedDescriptorQueryElement> queryElementsInOrder,
        PageDocumentIdAuthorizationSpec? authorization
    )
    {
        var seeds = queryElementsInOrder
            .Select(
                (element, index) =>
                    new QueryParameterNameSeed(
                        Index: index,
                        BaseName: QueryParameterNameAllocator.CreateBaseName(
                            element.SupportedField.QueryFieldName
                        ),
                        QueryFieldName: element.SupportedField.QueryFieldName,
                        Disambiguator: string.Join(
                            "|",
                            element.QueryElement.DocumentPaths.Select(static path => path.Value)
                        )
                    )
            )
            .ToArray();

        return QueryParameterNameAllocator.Allocate(
            seeds,
            [
                ResourceKeyIdParameterName,
                OffsetParameterName,
                LimitParameterName,
                MinChangeVersionParameterName,
                MaxChangeVersionParameterName,
                .. QueryParameterNameAllocator.CollectAuthorizationParameterNames(authorization),
            ]
        );
    }
}
