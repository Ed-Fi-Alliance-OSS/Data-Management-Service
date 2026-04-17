// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalQueryRequestPreprocessor
{
    private static readonly Guid _edFiUuidv5Namespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

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
                case RelationalQueryFieldTarget.DescriptorIdColumn(var _, var descriptorResource):
                    pendingDescriptorResolutions ??= [];
                    pendingDescriptorResolutions.Add(
                        new PendingDescriptorQueryResolution(
                            index,
                            queryElement,
                            CreateDescriptorReference(queryElement, descriptorResource, index)
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
                        queryCapability.SupportedFieldsByQueryField[
                            pendingDescriptorResolution.QueryElement.QueryFieldName
                        ],
                        new PreprocessedRelationalQueryValue.DescriptorDocumentId(
                            resolvedDescriptorReference.DocumentId
                        )
                    );
            }
        }

        return new RelationalQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            preprocessedElements
        );
    }

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
            DescriptorReferenceFailureReason.DescriptorTypeMismatch =>
                $"Relational query preprocessing determined descriptor query field '{queryElement.QueryFieldName}' value "
                    + $"'{queryElement.Value}' resolves to a different descriptor resource than '{targetResource}', so the query has no matches.",
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
                queryElement.Value.ToLowerInvariant()
            ),
        ]);

        return new DescriptorReference(
            descriptorResourceInfo,
            documentIdentity,
            CreateReferentialId(descriptorResourceInfo, documentIdentity),
            new JsonPath($"$.query[{index}]")
        );
    }

    private static ReferentialId CreateReferentialId(
        BaseResourceInfo resourceInfo,
        DocumentIdentity documentIdentity
    )
    {
        var identityString = string.Join(
            "#",
            documentIdentity.DocumentIdentityElements.Select(static element =>
                $"{element.IdentityJsonPath.Value}={element.IdentityValue}"
            )
        );

        return new ReferentialId(
            Deterministic.Create(
                _edFiUuidv5Namespace,
                $"{resourceInfo.ProjectName.Value}{resourceInfo.ResourceName.Value}{identityString}"
            )
        );
    }

    private sealed record PendingDescriptorQueryResolution(
        int Index,
        QueryElement QueryElement,
        DescriptorReference DescriptorReference
    );
}
