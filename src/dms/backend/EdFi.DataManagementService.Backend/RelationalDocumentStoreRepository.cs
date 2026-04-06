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
    IRelationalWriteExecutor writeExecutor,
    IRelationalWriteTargetLookupService targetLookupService,
    IDescriptorWriteHandler descriptorWriteHandler
) : IDocumentStoreRepository, IQueryHandler
{
    private const string ProfileAwareRelationalWritesPendingMessage =
        "profile-aware relational writes pending DMS-1123/DMS-1105/DMS-1124";

    private readonly ILogger<RelationalDocumentStoreRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRelationalWriteExecutor _writeExecutor =
        writeExecutor ?? throw new ArgumentNullException(nameof(writeExecutor));
    private readonly IRelationalWriteTargetLookupService _targetLookupService =
        targetLookupService ?? throw new ArgumentNullException(nameof(targetLookupService));
    private readonly IDescriptorWriteHandler _descriptorWriteHandler =
        descriptorWriteHandler ?? throw new ArgumentNullException(nameof(descriptorWriteHandler));

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

        if (relationalUpsertRequest.BackendProfileWriteContext is not null)
        {
            return Task.FromResult<UpsertResult>(
                new UpsertResult.UnknownFailure(ProfileAwareRelationalWritesPendingMessage)
            );
        }

        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalUpsertRequest.ResourceInfo);

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return _descriptorWriteHandler.HandlePostAsync(
                new DescriptorWriteRequest(
                    mappingSet,
                    resource,
                    relationalUpsertRequest.EdfiDoc,
                    relationalUpsertRequest.DocumentUuid,
                    relationalUpsertRequest.DocumentInfo.ReferentialId,
                    relationalUpsertRequest.TraceId
                )
            );
        }

        return ExecuteWriteGuardRails<UpsertResult>(
            requestBody: relationalUpsertRequest.EdfiDoc,
            traceId: relationalUpsertRequest.TraceId,
            mappingSet,
            relationalUpsertRequest.ResourceInfo,
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetRequest.Post(
                relationalUpsertRequest.DocumentInfo.ReferentialId,
                relationalUpsertRequest.DocumentUuid
            ),
            relationalUpsertRequest.DocumentInfo.DocumentReferences,
            relationalUpsertRequest.DocumentInfo.DescriptorReferences,
            relationalUpsertRequest.BackendProfileWriteContext,
            static failureMessage => new UpsertResult.UnknownFailure(failureMessage),
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

        if (relationalUpdateRequest.BackendProfileWriteContext is not null)
        {
            return Task.FromResult<UpdateResult>(
                new UpdateResult.UnknownFailure(ProfileAwareRelationalWritesPendingMessage)
            );
        }

        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalUpdateRequest.ResourceInfo);

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return _descriptorWriteHandler.HandlePutAsync(
                new DescriptorWriteRequest(
                    mappingSet,
                    resource,
                    relationalUpdateRequest.EdfiDoc,
                    relationalUpdateRequest.DocumentUuid,
                    referentialId: null,
                    relationalUpdateRequest.TraceId
                )
            );
        }

        return ExecuteWriteGuardRails<UpdateResult>(
            requestBody: relationalUpdateRequest.EdfiDoc,
            traceId: relationalUpdateRequest.TraceId,
            mappingSet,
            relationalUpdateRequest.ResourceInfo,
            RelationalWriteOperationKind.Put,
            new RelationalWriteTargetRequest.Put(relationalUpdateRequest.DocumentUuid),
            relationalUpdateRequest.DocumentInfo.DocumentReferences,
            relationalUpdateRequest.DocumentInfo.DescriptorReferences,
            relationalUpdateRequest.BackendProfileWriteContext,
            static failureMessage => new UpdateResult.UnknownFailure(failureMessage),
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

        if (deleteRequest.ResourceInfo.IsDescriptor)
        {
            return _descriptorWriteHandler.HandleDeleteAsync(
                deleteRequest.DocumentUuid,
                deleteRequest.TraceId
            );
        }

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
        RelationalWriteTargetRequest targetRequest,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences,
        BackendProfileWriteContext? backendProfileWriteContext,
        Func<string, TResult> failureFactory,
        Func<RelationalWriteExecutorResult, TResult> executorResultProjector
    )
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(documentReferences);
        ArgumentNullException.ThrowIfNull(descriptorReferences);
        ArgumentNullException.ThrowIfNull(failureFactory);
        ArgumentNullException.ThrowIfNull(executorResultProjector);

        if (backendProfileWriteContext is not null)
        {
            return failureFactory(ProfileAwareRelationalWritesPendingMessage);
        }

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

        var readPlanPreparation = PrepareExistingDocumentReadPlan(mappingSet, resource);

        for (var attemptIndex = 0; attemptIndex < 2; attemptIndex++)
        {
            var targetResolution = await ResolveTargetContextAsync(
                    mappingSet,
                    resource,
                    operationKind,
                    targetRequest
                )
                .ConfigureAwait(false);

            if (targetResolution.ImmediateResult is not null)
            {
                return executorResultProjector(targetResolution.ImmediateResult);
            }

            if (
                targetResolution.TargetContext is RelationalWriteTargetContext.ExistingDocument
                && readPlanPreparation.ReadPlan is null
            )
            {
                return failureFactory(
                    readPlanPreparation.FailureMessage
                        ?? RelationalWriteSupport.BuildMissingExistingDocumentReadPlanMessage(resource)
                );
            }

            var executorResult = await _writeExecutor
                .ExecuteAsync(
                    new RelationalWriteExecutorRequest(
                        mappingSet,
                        operationKind,
                        targetRequest,
                        writePlan,
                        readPlanPreparation.ReadPlan,
                        requestBody,
                        resourceInfo.AllowIdentityUpdates,
                        traceId,
                        new ReferenceResolverRequest(
                            MappingSet: mappingSet,
                            RequestResource: resource,
                            DocumentReferences: documentReferences,
                            DescriptorReferences: descriptorReferences
                        ),
                        targetContext: targetResolution.TargetContext!
                    )
                )
                .ConfigureAwait(false);

            if (
                executorResult.AttemptOutcome is RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare
                && attemptIndex == 0
            )
            {
                continue;
            }

            return executorResultProjector(executorResult);
        }

        throw new InvalidOperationException(
            $"Relational {operationKind} write retry loop exited without a final executor result."
        );
    }

    private async Task<TargetContextResolution> ResolveTargetContextAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalWriteOperationKind operationKind,
        RelationalWriteTargetRequest targetRequest
    )
    {
        var targetLookupResult = targetRequest switch
        {
            RelationalWriteTargetRequest.Post(var referentialId, var candidateDocumentUuid) =>
                await _targetLookupService
                    .ResolveForPostAsync(mappingSet, resource, referentialId, candidateDocumentUuid)
                    .ConfigureAwait(false),
            RelationalWriteTargetRequest.Put(var documentUuid) => await _targetLookupService
                .ResolveForPutAsync(mappingSet, resource, documentUuid)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Relational repository target lookup does not support target request type '{targetRequest.GetType().Name}'."
            ),
        };

        var targetContext = RelationalWriteSupport.TryTranslateTargetContext(targetLookupResult);

        if (targetContext is not null)
        {
            return new TargetContextResolution(targetContext, null);
        }

        if (
            operationKind == RelationalWriteOperationKind.Put
            && targetLookupResult is RelationalWriteTargetLookupResult.NotFound
        )
        {
            return new TargetContextResolution(
                null,
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
            );
        }

        throw new InvalidOperationException(
            $"Relational {operationKind} repository target lookup returned unsupported result type '{targetLookupResult.GetType().Name}'."
        );
    }

    private static ExistingDocumentReadPlanPreparation PrepareExistingDocumentReadPlan(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        try
        {
            return new ExistingDocumentReadPlanPreparation(GetReadPlanOrThrow(mappingSet, resource), null);
        }
        catch (NotSupportedException ex)
        {
            return new ExistingDocumentReadPlanPreparation(null, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new ExistingDocumentReadPlanPreparation(null, ex.Message);
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

    private sealed record ExistingDocumentReadPlanPreparation(
        ResourceReadPlan? ReadPlan,
        string? FailureMessage
    );

    private sealed record TargetContextResolution(
        RelationalWriteTargetContext? TargetContext,
        RelationalWriteExecutorResult? ImmediateResult
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
