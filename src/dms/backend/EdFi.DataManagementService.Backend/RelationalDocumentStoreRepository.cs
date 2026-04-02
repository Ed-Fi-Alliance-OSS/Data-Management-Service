// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

public sealed class RelationalDocumentStoreRepository(
    ILogger<RelationalDocumentStoreRepository> logger,
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    IRelationalWriteExecutor writeExecutor
) : IDocumentStoreRepository, IQueryHandler
{
    private readonly ILogger<RelationalDocumentStoreRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRelationalWriteTargetLookupResolver _targetLookupResolver =
        targetLookupResolver ?? throw new ArgumentNullException(nameof(targetLookupResolver));
    private readonly IRelationalWriteExecutor _writeExecutor =
        writeExecutor ?? throw new ArgumentNullException(nameof(writeExecutor));

    public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        ArgumentNullException.ThrowIfNull(upsertRequest);
        var relationalUpsertRequest = RequireRelationalRequest<IRelationalUpsertRequest>(
            upsertRequest,
            nameof(upsertRequest)
        );
        var mappingSet = relationalUpsertRequest.MappingSet;
        ArgumentNullException.ThrowIfNull(mappingSet);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.UpsertDocument - {TraceId}",
            relationalUpsertRequest.TraceId.Value
        );

        return ExecuteWriteGuardRails<UpsertResult>(
            requestBody: relationalUpsertRequest.EdfiDoc,
            traceId: relationalUpsertRequest.TraceId,
            mappingSet,
            relationalUpsertRequest.ResourceInfo,
            RelationalWriteOperationKind.Post,
            relationalUpsertRequest.DocumentInfo.DocumentReferences,
            relationalUpsertRequest.DocumentInfo.DescriptorReferences,
            static failureMessage => new UpsertResult.UnknownFailure(failureMessage),
            static validationFailures => new UpsertResult.UpsertFailureValidation(validationFailures),
            notFoundFailureFactory: null,
            async (mappingSet, resource) =>
                await _targetLookupResolver
                    .ResolveForPostAsync(
                        mappingSet,
                        resource,
                        relationalUpsertRequest.DocumentInfo.ReferentialId,
                        relationalUpsertRequest.DocumentUuid
                    )
                    .ConfigureAwait(false),
            static executorResult =>
                executorResult switch
                {
                    RelationalWriteExecutorResult.Upsert(var result) => result,
                    RelationalWriteExecutorResult.Update => throw new InvalidOperationException(
                        "Relational write executor returned an update result for a POST request."
                    ),
                    _ => throw new InvalidOperationException(
                        $"Relational write executor returned unsupported result type '{executorResult.GetType().Name}' for a POST request."
                    ),
                }
        );
    }

    public Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        ArgumentNullException.ThrowIfNull(getRequest);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.GetDocumentById - {TraceId}",
            getRequest.TraceId.Value
        );

        return Task.FromResult<GetResult>(
            new GetResult.UnknownFailure(
                $"Relational GET by id is not implemented for resource '{getRequest.ResourceName.Value}'."
            )
        );
    }

    public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        ArgumentNullException.ThrowIfNull(updateRequest);
        var relationalUpdateRequest = RequireRelationalRequest<IRelationalUpdateRequest>(
            updateRequest,
            nameof(updateRequest)
        );
        var mappingSet = relationalUpdateRequest.MappingSet;
        ArgumentNullException.ThrowIfNull(mappingSet);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.UpdateDocumentById - {TraceId}",
            relationalUpdateRequest.TraceId.Value
        );

        return ExecuteWriteGuardRails<UpdateResult>(
            requestBody: relationalUpdateRequest.EdfiDoc,
            traceId: relationalUpdateRequest.TraceId,
            mappingSet,
            relationalUpdateRequest.ResourceInfo,
            RelationalWriteOperationKind.Put,
            relationalUpdateRequest.DocumentInfo.DocumentReferences,
            relationalUpdateRequest.DocumentInfo.DescriptorReferences,
            static failureMessage => new UpdateResult.UnknownFailure(failureMessage),
            static validationFailures => new UpdateResult.UpdateFailureValidation(validationFailures),
            notFoundFailureFactory: static () => new UpdateResult.UpdateFailureNotExists(),
            async (mappingSet, resource) =>
                await _targetLookupResolver
                    .ResolveForPutAsync(mappingSet, resource, relationalUpdateRequest.DocumentUuid)
                    .ConfigureAwait(false),
            static executorResult =>
                executorResult switch
                {
                    RelationalWriteExecutorResult.Update(var result) => result,
                    RelationalWriteExecutorResult.Upsert => throw new InvalidOperationException(
                        "Relational write executor returned an upsert result for a PUT request."
                    ),
                    _ => throw new InvalidOperationException(
                        $"Relational write executor returned unsupported result type '{executorResult.GetType().Name}' for a PUT request."
                    ),
                }
        );
    }

    public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        ArgumentNullException.ThrowIfNull(deleteRequest);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.DeleteDocumentById - {TraceId}",
            deleteRequest.TraceId.Value
        );

        return Task.FromResult<DeleteResult>(
            new DeleteResult.UnknownFailure(
                $"Relational DELETE is not implemented for resource '{FormatResource(RelationalWriteSupport.ToQualifiedResourceName(deleteRequest.ResourceInfo))}'."
            )
        );
    }

    public Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        ArgumentNullException.ThrowIfNull(queryRequest);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.QueryDocuments - {TraceId}",
            queryRequest.TraceId.Value
        );

        return Task.FromResult<QueryResult>(
            new QueryResult.UnknownFailure(
                $"Relational query handling is not implemented for resource '{FormatResource(RelationalWriteSupport.ToQualifiedResourceName(queryRequest.ResourceInfo))}'."
            )
        );
    }

    private async Task<TResult> ExecuteWriteGuardRails<TResult>(
        System.Text.Json.Nodes.JsonNode requestBody,
        TraceId traceId,
        MappingSet mappingSet,
        ResourceInfo resourceInfo,
        RelationalWriteOperationKind operationKind,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences,
        Func<string, TResult> failureFactory,
        Func<WriteValidationFailure[], TResult> validationFailureFactory,
        Func<TResult>? notFoundFailureFactory,
        Func<
            MappingSet,
            QualifiedResourceName,
            Task<RelationalWriteTargetLookupResult>
        > resolveTargetLookupAsync,
        Func<RelationalWriteExecutorResult, TResult> executorResultProjector
    )
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(documentReferences);
        ArgumentNullException.ThrowIfNull(descriptorReferences);
        ArgumentNullException.ThrowIfNull(failureFactory);
        ArgumentNullException.ThrowIfNull(validationFailureFactory);
        ArgumentNullException.ThrowIfNull(resolveTargetLookupAsync);
        ArgumentNullException.ThrowIfNull(executorResultProjector);

        var resource = RelationalWriteSupport.ToQualifiedResourceName(resourceInfo);
        ResourceWritePlan writePlan;

        try
        {
            writePlan = mappingSet.GetWritePlanOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return failureFactory(ex.Message);
        }
        catch (MissingWritePlanLookupGuardRailException ex)
        {
            return failureFactory(ex.Message);
        }

        try
        {
            var targetLookupResult = await resolveTargetLookupAsync(mappingSet, resource)
                .ConfigureAwait(false);
            var targetLookupSelection = TranslateTargetLookupResult(
                targetLookupResult,
                operationKind,
                notFoundFailureFactory
            );

            if (targetLookupSelection.HasFailure)
            {
                return targetLookupSelection.FailureResult!;
            }

            var targetContext = targetLookupSelection.TargetContext!;
            var readPlanResult = TryGetReadPlan(targetContext, mappingSet, resource, failureFactory);

            if (readPlanResult.HasFailure)
            {
                return readPlanResult.FailureResult!;
            }

            var executorResult = await _writeExecutor
                .ExecuteAsync(
                    new RelationalWriteExecutorRequest(
                        mappingSet,
                        operationKind,
                        targetContext,
                        writePlan,
                        readPlanResult.ReadPlan,
                        requestBody,
                        traceId,
                        new ReferenceResolverRequest(
                            MappingSet: mappingSet,
                            RequestResource: resource,
                            DocumentReferences: documentReferences,
                            DescriptorReferences: descriptorReferences
                        )
                    )
                )
                .ConfigureAwait(false);

            return executorResultProjector(executorResult);
        }
        catch (RelationalWriteRequestValidationException ex)
        {
            return validationFailureFactory(ex.ValidationFailures);
        }
    }

    private static TargetLookupSelectionResult<TResult> TranslateTargetLookupResult<TResult>(
        RelationalWriteTargetLookupResult targetLookupResult,
        RelationalWriteOperationKind operationKind,
        Func<TResult>? notFoundFailureFactory
    )
    {
        ArgumentNullException.ThrowIfNull(targetLookupResult);

        return targetLookupResult switch
        {
            RelationalWriteTargetLookupResult.CreateNew(var documentUuid) =>
                new TargetLookupSelectionResult<TResult>(
                    new RelationalWriteTargetContext.CreateNew(documentUuid),
                    default,
                    false
                ),
            RelationalWriteTargetLookupResult.ExistingDocument(
                var documentId,
                var documentUuid,
                var observedContentVersion
            ) => new TargetLookupSelectionResult<TResult>(
                new RelationalWriteTargetContext.ExistingDocument(
                    documentId,
                    documentUuid,
                    observedContentVersion
                ),
                default,
                false
            ),
            RelationalWriteTargetLookupResult.NotFound when notFoundFailureFactory is not null =>
                new TargetLookupSelectionResult<TResult>(null, notFoundFailureFactory(), true),
            RelationalWriteTargetLookupResult.NotFound => throw new InvalidOperationException(
                $"Relational {operationKind} target lookup returned NotFound without a configured not-found result mapping."
            ),
            _ => throw new InvalidOperationException(
                $"Relational {operationKind} target lookup returned unsupported result type '{targetLookupResult.GetType().Name}'."
            ),
        };
    }

    private static ReadPlanSelectionResult<TResult> TryGetReadPlan<TResult>(
        RelationalWriteTargetContext targetContext,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        Func<string, TResult> failureFactory
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(failureFactory);

        if (targetContext is not RelationalWriteTargetContext.ExistingDocument)
        {
            return new ReadPlanSelectionResult<TResult>(null, default, false);
        }

        try
        {
            return new ReadPlanSelectionResult<TResult>(
                GetReadPlanOrThrow(mappingSet, resource),
                default,
                false
            );
        }
        catch (NotSupportedException ex)
        {
            return new ReadPlanSelectionResult<TResult>(null, failureFactory(ex.Message), true);
        }
        catch (InvalidOperationException ex)
        {
            return new ReadPlanSelectionResult<TResult>(null, failureFactory(ex.Message), true);
        }
    }

    private static ResourceReadPlan GetReadPlanOrThrow(MappingSet mappingSet, QualifiedResourceName resource)
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (mappingSet.ReadPlansByResource.TryGetValue(resource, out var readPlan))
        {
            return readPlan;
        }

        var concreteResourceModel =
            mappingSet.Model.ConcreteResourcesInNameOrder.SingleOrDefault(model =>
                model.RelationalModel.Resource == resource
            )
            ?? throw new KeyNotFoundException(
                $"Mapping set '{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}' does not contain resource "
                    + $"'{RelationalWriteSupport.FormatResource(resource)}' in ConcreteResourcesInNameOrder."
            );

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Read plan for resource '{RelationalWriteSupport.FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses the descriptor read path instead of compiled relational-table hydration plans. "
                    + "Next story: E08-S05 (05-descriptor-endpoints.md)."
            );
        }

        if (concreteResourceModel.StorageKind == ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Read plan lookup failed for resource '{RelationalWriteSupport.FormatResource(resource)}' in mapping set "
                    + $"'{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}': resource storage kind "
                    + $"'{ResourceStorageKind.RelationalTables}' should always have a compiled relational-table read plan, but no entry "
                    + "was found. This indicates an internal compilation/selection bug."
            );
        }

        throw new InvalidOperationException(
            $"Read plan lookup failed for resource '{RelationalWriteSupport.FormatResource(resource)}' in mapping set "
                + $"'{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}': storage kind '{concreteResourceModel.StorageKind}' "
                + "is not recognized."
        );
    }

    private sealed record ReadPlanSelectionResult<TResult>(
        ResourceReadPlan? ReadPlan,
        TResult? FailureResult,
        bool HasFailure
    );

    private sealed record TargetLookupSelectionResult<TResult>(
        RelationalWriteTargetContext? TargetContext,
        TResult? FailureResult,
        bool HasFailure
    );

    private static string FormatResource(QualifiedResourceName resource) =>
        RelationalWriteSupport.FormatResource(resource);

    private static TRelationalRequest RequireRelationalRequest<TRelationalRequest>(
        object request,
        string paramName
    )
        where TRelationalRequest : class, IRelationalWriteRequest
    {
        return request as TRelationalRequest
            ?? throw new ArgumentException(
                $"Relational repository requires requests that implement {typeof(TRelationalRequest).Name}.",
                paramName
            );
    }
}
