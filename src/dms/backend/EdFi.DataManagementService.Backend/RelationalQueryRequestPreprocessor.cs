// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalQueryRequestPreprocessor
{
    public static async Task<RelationalQueryPreprocessingResult> PreprocessAsync(
        MappingSet mappingSet,
        QualifiedResourceName requestResource,
        IReadOnlyList<QueryElement> queryElements,
        RelationalQueryCapability queryCapability,
        IReferenceResolver referenceResolver,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(queryElements);
        ArgumentNullException.ThrowIfNull(queryCapability);
        ArgumentNullException.ThrowIfNull(referenceResolver);

        if (queryCapability.Support is not RelationalQuerySupport.Supported)
        {
            throw new ArgumentException(
                "Relational query preprocessing requires resource query capability metadata in the supported state.",
                nameof(queryCapability)
            );
        }

        var validationFailures = ValidateDescriptorQueryValues(queryElements, queryCapability);
        validationFailures = AddSqlServerStorageWidthFailures(
            mappingSet,
            requestResource,
            queryElements,
            queryCapability,
            validationFailures
        );

        if (validationFailures is not null)
        {
            return new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.ValidationFailure(validationFailures),
                []
            );
        }

        var preprocessedElements = new PreprocessedRelationalQueryElement[queryElements.Count];
        List<PendingDescriptorQueryResolution>? pendingDescriptorResolutions = null;

        for (var index = 0; index < queryElements.Count; index++)
        {
            var queryElement =
                queryElements[index]
                ?? throw new ArgumentException(
                    "Query elements must not contain null entries.",
                    nameof(queryElements)
                );

            if (
                !queryCapability.SupportedFieldsByQueryField.TryGetValue(
                    queryElement.QueryFieldName,
                    out var supportedField
                )
            )
            {
                throw new InvalidOperationException(
                    $"Relational query preprocessing could not find supported query metadata for field "
                        + $"'{queryElement.QueryFieldName}'."
                );
            }

            switch (supportedField.Target)
            {
                case RelationalQueryFieldTarget.RootColumn:
                case RelationalQueryFieldTarget.CollectionColumn { DescriptorResource: null }:
                    preprocessedElements[index] = new PreprocessedRelationalQueryElement(
                        queryElement,
                        supportedField,
                        new PreprocessedRelationalQueryValue.Raw(queryElement.Value)
                    );
                    continue;
                case RelationalQueryFieldTarget.DocumentUuid:
                    if (!Guid.TryParse(queryElement.Value, out var documentUuid))
                    {
                        return new RelationalQueryPreprocessingResult(
                            new RelationalQueryPreprocessingOutcome.EmptyPage(
                                $"Relational query preprocessing determined query field '{queryElement.QueryFieldName}' value "
                                    + $"'{queryElement.Value}' is not a valid UUID, so the query has no matches."
                            ),
                            []
                        );
                    }

                    preprocessedElements[index] = new PreprocessedRelationalQueryElement(
                        queryElement,
                        supportedField,
                        new PreprocessedRelationalQueryValue.DocumentUuid(documentUuid)
                    );
                    continue;
                case RelationalQueryFieldTarget.DescriptorIdColumn:
                case RelationalQueryFieldTarget.CollectionColumn:
                    // Root and collection descriptor filters are resolved in one batched lookup.
                    pendingDescriptorResolutions ??= [];
                    pendingDescriptorResolutions.Add(
                        new PendingDescriptorQueryResolution(
                            index,
                            queryElement,
                            supportedField,
                            CreateDescriptorReference(
                                queryElement,
                                GetDescriptorResource(supportedField.Target),
                                index
                            )
                        )
                    );
                    continue;
                default:
                    throw new InvalidOperationException(
                        $"Relational query preprocessing does not recognize supported target type "
                            + $"'{supportedField.Target.GetType().Name}' for query field '{queryElement.QueryFieldName}'."
                    );
            }
        }

        if (pendingDescriptorResolutions is not null)
        {
            var resolvedReferences = await referenceResolver
                .ResolveAsync(
                    new ReferenceResolverRequest(
                        MappingSet: mappingSet,
                        RequestResource: requestResource,
                        DocumentReferences: [],
                        DescriptorReferences: pendingDescriptorResolutions
                            .Select(static resolution => resolution.DescriptorReference)
                            .ToArray()
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            var invalidDescriptorFailuresByPath = resolvedReferences.InvalidDescriptorReferences.ToDictionary(
                static failure => failure.Path.Value,
                static failure => failure,
                StringComparer.Ordinal
            );
            var successfulDescriptorReferencesByPath =
                resolvedReferences.SuccessfulDescriptorReferencesByPath.ToDictionary(
                    static entry => entry.Key.Value,
                    static entry => entry.Value,
                    StringComparer.Ordinal
                );

            foreach (var pendingDescriptorResolution in pendingDescriptorResolutions)
            {
                if (
                    invalidDescriptorFailuresByPath.TryGetValue(
                        pendingDescriptorResolution.DescriptorReference.Path.Value,
                        out var failure
                    )
                )
                {
                    return new RelationalQueryPreprocessingResult(
                        new RelationalQueryPreprocessingOutcome.EmptyPage(
                            BuildDescriptorLookupFailureMessage(
                                pendingDescriptorResolution.QueryElement,
                                failure
                            )
                        ),
                        []
                    );
                }

                if (
                    !successfulDescriptorReferencesByPath.TryGetValue(
                        pendingDescriptorResolution.DescriptorReference.Path.Value,
                        out var resolvedDescriptorReference
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Relational query preprocessing did not receive a successful or invalid descriptor resolution for query field "
                            + $"'{pendingDescriptorResolution.QueryElement.QueryFieldName}'."
                    );
                }

                preprocessedElements[pendingDescriptorResolution.Index] =
                    new PreprocessedRelationalQueryElement(
                        pendingDescriptorResolution.QueryElement,
                        pendingDescriptorResolution.SupportedField,
                        new PreprocessedRelationalQueryValue.DescriptorId(
                            resolvedDescriptorReference.DescriptorId
                        )
                    );
            }
        }

        return new RelationalQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            preprocessedElements
        );
    }

    /// <summary>
    /// Checks every descriptor-valued element before the main pass, so a malformed value is rejected
    /// whatever its position: the main pass returns an empty page as soon as an earlier element proves
    /// the query has no matches. Returns null when every value is well-formed.
    /// </summary>
    private static List<WriteValidationFailure>? ValidateDescriptorQueryValues(
        IReadOnlyList<QueryElement> queryElements,
        RelationalQueryCapability queryCapability
    )
    {
        List<WriteValidationFailure>? failures = null;

        foreach (var queryElement in queryElements)
        {
            if (
                queryElement is not null
                && queryCapability.SupportedFieldsByQueryField.TryGetValue(
                    queryElement.QueryFieldName,
                    out var supportedField
                )
                && IsDescriptorTarget(supportedField.Target)
                && !DescriptorText.IsWellFormedWithoutNul(queryElement.Value)
            )
            {
                failures ??= [];
                failures.Add(
                    new WriteValidationFailure(
                        queryElement.DocumentPaths[0],
                        DescriptorText.InvalidValueMessage
                    )
                );
            }
        }

        return failures;
    }

    private static QualifiedResourceName GetDescriptorResource(RelationalQueryFieldTarget target) =>
        target switch
        {
            RelationalQueryFieldTarget.DescriptorIdColumn(_, var descriptorResource) => descriptorResource,
            RelationalQueryFieldTarget.CollectionColumn { DescriptorResource: { } descriptorResource } =>
                descriptorResource,
            _ => throw new InvalidOperationException(
                $"Relational query preprocessing expected a descriptor target, but received '{target.GetType().Name}'."
            ),
        };

    private static bool IsDescriptorTarget(RelationalQueryFieldTarget target) =>
        target
            is RelationalQueryFieldTarget.DescriptorIdColumn
                or RelationalQueryFieldTarget.CollectionColumn { DescriptorResource: not null };

    /// <summary>
    /// Appends a failure for every SQL Server string filter wider than the root or collection column it is
    /// compared against, by the column's mapped length. Descriptor references are not checked here: they
    /// bind a resolved descriptor ID, and the descriptor lookup accepts URIs wider than any stored one
    /// because the SQL Server identity collation can equate them with a stored descriptor.
    /// Runs before the main pass for the same reason descriptor values are checked first, so a rejection
    /// does not depend on an earlier element proving the query has no matches.
    /// </summary>
    private static List<WriteValidationFailure>? AddSqlServerStorageWidthFailures(
        MappingSet mappingSet,
        QualifiedResourceName requestResource,
        IReadOnlyList<QueryElement> queryElements,
        RelationalQueryCapability queryCapability,
        List<WriteValidationFailure>? failures
    )
    {
        SqlDialect dialect = mappingSet.Key.Dialect;

        if (dialect != SqlDialect.Mssql)
        {
            return failures;
        }

        RelationalResourceModel? resourceModel = null;

        foreach (var queryElement in queryElements)
        {
            if (
                queryElement is null
                || !queryCapability.SupportedFieldsByQueryField.TryGetValue(
                    queryElement.QueryFieldName,
                    out var supportedField
                )
            )
            {
                continue;
            }

            if (
                supportedField.Target
                is not (
                    RelationalQueryFieldTarget.RootColumn
                    or RelationalQueryFieldTarget.CollectionColumn { DescriptorResource: null }
                )
            )
            {
                continue;
            }

            resourceModel ??= mappingSet.GetConcreteResourceModelOrThrow(requestResource).RelationalModel;

            DbColumnModel? filterColumn = supportedField.Target switch
            {
                RelationalQueryFieldTarget.RootColumn(var column) => FindColumn(resourceModel.Root, column),
                RelationalQueryFieldTarget.CollectionColumn collectionColumn => FindColumn(
                    resourceModel.TablesInDependencyOrder.FirstOrDefault(table =>
                        table.Table == collectionColumn.CollectionTable
                    ),
                    collectionColumn.Column
                ),
                _ => null,
            };
            int? columnLength = filterColumn?.ScalarType is { Kind: ScalarKind.String } scalarType
                ? scalarType.MaxLength
                : null;

            if (QueryFilterStorageWidth.ExceedsSqlServerColumn(dialect, queryElement.Value, columnLength))
            {
                failures ??= [];
                failures.Add(QueryFilterStorageWidth.InvalidValueFailure(queryElement));
            }
        }

        return failures;
    }

    private static DbColumnModel? FindColumn(DbTableModel? table, DbColumnName columnName) =>
        table?.Columns.FirstOrDefault(column => column.ColumnName == columnName);

    private static string BuildDescriptorLookupFailureMessage(
        QueryElement queryElement,
        DescriptorReferenceFailure failure
    )
    {
        var targetResource =
            $"{failure.TargetResource.ProjectName.Value}.{failure.TargetResource.ResourceName.Value}";

        return failure.Reason switch
        {
            DescriptorReferenceFailureReason.Missing =>
                $"Relational query preprocessing determined descriptor query field '{queryElement.QueryFieldName}' value "
                    + $"'{queryElement.Value}' does not resolve to descriptor resource '{targetResource}', so the query has no matches.",
            _ => throw new InvalidOperationException(
                $"Relational query preprocessing does not recognize descriptor failure reason '{failure.Reason}'."
            ),
        };
    }

    private static DescriptorReference CreateDescriptorReference(
        QueryElement queryElement,
        QualifiedResourceName descriptorResource,
        int index
    )
    {
        var descriptorResourceInfo = new BaseResourceInfo(
            new ProjectName(descriptorResource.ProjectName),
            new ResourceName(descriptorResource.ResourceName),
            true
        );
        var documentIdentity = new DocumentIdentity([
            new DocumentIdentityElement(
                DocumentIdentity.DescriptorIdentityJsonPath,
                DescriptorText.AssertWellFormedWithoutNul(queryElement.Value)
            ),
        ]);

        return new DescriptorReference(
            descriptorResourceInfo,
            documentIdentity,
            new JsonPath($"$.query[{index}]")
        );
    }

    private sealed record PendingDescriptorQueryResolution(
        int Index,
        QueryElement QueryElement,
        SupportedRelationalQueryField SupportedField,
        DescriptorReference DescriptorReference
    );
}
