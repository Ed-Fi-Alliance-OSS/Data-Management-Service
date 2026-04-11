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
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

public sealed class RelationalDocumentStoreRepository(
    ILogger<RelationalDocumentStoreRepository> logger,
    IRelationalWriteExecutor writeExecutor,
    IRelationalWriteTargetLookupService targetLookupService,
    IDescriptorWriteHandler descriptorWriteHandler,
    IDocumentHydrator documentHydrator,
    IRelationalReadTargetLookupService readTargetLookupService,
    IRelationalReadMaterializer readMaterializer,
    IReadableProfileProjector readableProfileProjector
) : IDocumentStoreRepository, IQueryHandler
{
    private readonly ILogger<RelationalDocumentStoreRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRelationalWriteExecutor _writeExecutor =
        writeExecutor ?? throw new ArgumentNullException(nameof(writeExecutor));
    private readonly IRelationalWriteTargetLookupService _targetLookupService =
        targetLookupService ?? throw new ArgumentNullException(nameof(targetLookupService));
    private readonly IDescriptorWriteHandler _descriptorWriteHandler =
        descriptorWriteHandler ?? throw new ArgumentNullException(nameof(descriptorWriteHandler));
    private readonly IDocumentHydrator _documentHydrator =
        documentHydrator ?? throw new ArgumentNullException(nameof(documentHydrator));
    private readonly IRelationalReadTargetLookupService _readTargetLookupService =
        readTargetLookupService ?? throw new ArgumentNullException(nameof(readTargetLookupService));
    private readonly IRelationalReadMaterializer _readMaterializer =
        readMaterializer ?? throw new ArgumentNullException(nameof(readMaterializer));
    private readonly IReadableProfileProjector _readableProfileProjector =
        readableProfileProjector ?? throw new ArgumentNullException(nameof(readableProfileProjector));

    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
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

        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalUpsertRequest.ResourceInfo);

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return await _descriptorWriteHandler
                .HandlePostAsync(
                    new DescriptorWriteRequest(
                        mappingSet,
                        resource,
                        relationalUpsertRequest.EdfiDoc,
                        relationalUpsertRequest.DocumentUuid,
                        relationalUpsertRequest.DocumentInfo.ReferentialId,
                        relationalUpsertRequest.TraceId
                    )
                )
                .ConfigureAwait(false);
        }

        var profileWriteContext = relationalUpsertRequest.BackendProfileWriteContext;
        var selectedBody =
            profileWriteContext?.Request.WritableRequestBody ?? relationalUpsertRequest.EdfiDoc;

        var result = await ExecuteWriteGuardRails<UpsertResult>(
                requestBody: selectedBody,
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
                    },
                profileWriteContext
            )
            .ConfigureAwait(false);

        return result;
    }

    public Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        ArgumentNullException.ThrowIfNull(getRequest);
        var relationalGetRequest = RequireRelationalRequest<IRelationalGetRequest>(
            getRequest,
            nameof(getRequest)
        );
        var mappingSet = relationalGetRequest.MappingSet;
        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalGetRequest.ResourceInfo);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.GetDocumentById - {TraceId}",
            relationalGetRequest.TraceId.Value
        );

        if (relationalGetRequest.ResourceInfo.IsDescriptor)
        {
            return Task.FromResult<GetResult>(BuildDescriptorGetNotImplementedResult(resource));
        }

        ResourceReadPlan readPlan;

        try
        {
            readPlan = GetReadPlanOrThrow(mappingSet, resource);
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult<GetResult>(new GetResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult<GetResult>(new GetResult.UnknownFailure(ex.Message));
        }

        return GetDocumentByIdAsync(relationalGetRequest, mappingSet, resource, readPlan);
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
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

        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalUpdateRequest.ResourceInfo);

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return await _descriptorWriteHandler
                .HandlePutAsync(
                    new DescriptorWriteRequest(
                        mappingSet,
                        resource,
                        relationalUpdateRequest.EdfiDoc,
                        relationalUpdateRequest.DocumentUuid,
                        referentialId: null,
                        relationalUpdateRequest.TraceId
                    )
                )
                .ConfigureAwait(false);
        }

        var profileWriteContext = relationalUpdateRequest.BackendProfileWriteContext;
        var selectedBody =
            profileWriteContext?.Request.WritableRequestBody ?? relationalUpdateRequest.EdfiDoc;

        var result = await ExecuteWriteGuardRails<UpdateResult>(
                requestBody: selectedBody,
                traceId: relationalUpdateRequest.TraceId,
                mappingSet,
                relationalUpdateRequest.ResourceInfo,
                RelationalWriteOperationKind.Put,
                new RelationalWriteTargetRequest.Put(relationalUpdateRequest.DocumentUuid),
                relationalUpdateRequest.DocumentInfo.DocumentReferences,
                relationalUpdateRequest.DocumentInfo.DescriptorReferences,
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
                    },
                profileWriteContext
            )
            .ConfigureAwait(false);

        return result;
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
            new QueryResult.QueryFailureNotImplemented(
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
        Func<string, TResult> failureFactory,
        Func<RelationalWriteExecutorResult, TResult> executorResultProjector,
        BackendProfileWriteContext? profileWriteContext = null
    )
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(documentReferences);
        ArgumentNullException.ThrowIfNull(descriptorReferences);
        ArgumentNullException.ThrowIfNull(failureFactory);
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
                        targetContext: targetResolution.TargetContext!,
                        profileWriteContext: profileWriteContext
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

    private async Task<GetResult> GetDocumentByIdAsync(
        IRelationalGetRequest relationalGetRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan
    )
    {
        // Relational authorization is pending a later story. Do not route GET-by-id
        // through the legacy authorization handler while the relational auth seam lands.
        var targetLookupResult = await _readTargetLookupService
            .ResolveForGetByIdAsync(mappingSet, resource, relationalGetRequest.DocumentUuid)
            .ConfigureAwait(false);

        if (
            targetLookupResult
            is RelationalReadTargetLookupResult.NotFound
                or RelationalReadTargetLookupResult.WrongResource
        )
        {
            return new GetResult.GetFailureNotExists();
        }

        if (targetLookupResult is not RelationalReadTargetLookupResult.ExistingDocument existingDocument)
        {
            throw new InvalidOperationException(
                $"Relational repository GET target lookup returned unsupported result type '{targetLookupResult.GetType().Name}'."
            );
        }

        var hydratedPage = await _documentHydrator
            .HydrateAsync(readPlan, new PageKeysetSpec.Single(existingDocument.DocumentId), default)
            .ConfigureAwait(false);

        if (hydratedPage.DocumentMetadata.Count == 0)
        {
            return new GetResult.GetFailureNotExists();
        }

        if (hydratedPage.DocumentMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"Relational GET hydration for document id {existingDocument.DocumentId} returned "
                    + $"{hydratedPage.DocumentMetadata.Count} metadata rows, but exactly 1 was expected."
            );
        }

        var documentMetadata = hydratedPage.DocumentMetadata[0];

        if (documentMetadata.DocumentId != existingDocument.DocumentId)
        {
            throw new InvalidOperationException(
                $"Relational GET hydration returned metadata for document id {documentMetadata.DocumentId}, "
                    + $"but target document id was {existingDocument.DocumentId}."
            );
        }

        if (documentMetadata.DocumentUuid != existingDocument.DocumentUuid.Value)
        {
            throw new InvalidOperationException(
                $"Relational GET hydration returned document uuid '{documentMetadata.DocumentUuid}', "
                    + $"but target document uuid was '{existingDocument.DocumentUuid.Value}'."
            );
        }

        var edfiDoc = _readMaterializer.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                documentMetadata,
                hydratedPage.TableRowsInDependencyOrder,
                hydratedPage.DescriptorRowsInPlanOrder,
                relationalGetRequest.ReadMode
            )
        );

        if (ShouldApplyReadableProfileProjection(relationalGetRequest))
        {
            var projectionContext = relationalGetRequest.ReadableProfileProjectionContext!;
            edfiDoc = _readableProfileProjector.Project(
                edfiDoc,
                projectionContext.ContentTypeDefinition,
                projectionContext.IdentityPropertyNames
            );
        }

        return new GetResult.GetSuccess(
            new DocumentUuid(documentMetadata.DocumentUuid),
            edfiDoc,
            documentMetadata.ContentLastModifiedAt.UtcDateTime,
            null
        );
    }

    private static string FormatResource(QualifiedResourceName resource) =>
        RelationalWriteSupport.FormatResource(resource);

    private static GetResult BuildDescriptorGetNotImplementedResult(QualifiedResourceName resource)
    {
        return new GetResult.GetFailureNotImplemented(
            $"Relational descriptor GET by id is not implemented for resource '{FormatResource(resource)}'."
        );
    }

    private static bool ShouldApplyReadableProfileProjection(IRelationalGetRequest relationalGetRequest) =>
        relationalGetRequest.ReadMode == RelationalGetRequestReadMode.ExternalResponse
        && relationalGetRequest.ReadableProfileProjectionContext is not null;

    private static TRelationalRequest RequireRelationalRequest<TRelationalRequest>(
        object request,
        string paramName
    )
        where TRelationalRequest : class
    {
        return request as TRelationalRequest
            ?? throw new ArgumentException(
                $"Relational repository requires requests that implement {typeof(TRelationalRequest).Name}.",
                paramName
            );
    }
}
