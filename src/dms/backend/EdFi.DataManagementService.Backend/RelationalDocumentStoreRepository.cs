// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

public sealed class RelationalDocumentStoreRepository(
    ILogger<RelationalDocumentStoreRepository> logger,
    IRelationalWriteTargetContextResolver targetContextResolver,
    IReferenceResolver referenceResolver
) : IDocumentStoreRepository, IQueryHandler
{
    private readonly ILogger<RelationalDocumentStoreRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRelationalWriteTargetContextResolver _targetContextResolver =
        targetContextResolver ?? throw new ArgumentNullException(nameof(targetContextResolver));
    private readonly IReferenceResolver _referenceResolver =
        referenceResolver ?? throw new ArgumentNullException(nameof(referenceResolver));

    public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        ArgumentNullException.ThrowIfNull(upsertRequest);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.UpsertDocument - {TraceId}",
            upsertRequest.TraceId.Value
        );

        return ExecuteWriteGuardRails<UpsertResult>(
            upsertRequest.MappingSet,
            upsertRequest.ResourceInfo,
            RelationalWriteOperationKind.Post,
            upsertRequest.DocumentInfo.DocumentReferences,
            upsertRequest.DocumentInfo.DescriptorReferences,
            static failureMessage => new UpsertResult.UnknownFailure(failureMessage),
            static (invalidDocumentReferences, invalidDescriptorReferences) =>
                new UpsertResult.UpsertFailureReference(
                    invalidDocumentReferences,
                    invalidDescriptorReferences
                ),
            async (mappingSet, resource) =>
                _ = await _targetContextResolver
                    .ResolveForPostAsync(
                        mappingSet,
                        resource,
                        upsertRequest.DocumentInfo.ReferentialId,
                        upsertRequest.DocumentUuid
                    )
                    .ConfigureAwait(false)
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

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.UpdateDocumentById - {TraceId}",
            updateRequest.TraceId.Value
        );

        return ExecuteWriteGuardRails<UpdateResult>(
            updateRequest.MappingSet,
            updateRequest.ResourceInfo,
            RelationalWriteOperationKind.Put,
            updateRequest.DocumentInfo.DocumentReferences,
            updateRequest.DocumentInfo.DescriptorReferences,
            static failureMessage => new UpdateResult.UnknownFailure(failureMessage),
            static (invalidDocumentReferences, invalidDescriptorReferences) =>
                new UpdateResult.UpdateFailureReference(
                    invalidDocumentReferences,
                    invalidDescriptorReferences
                ),
            async (mappingSet, resource) =>
                _ = await _targetContextResolver
                    .ResolveForPutAsync(mappingSet, resource, updateRequest.DocumentUuid)
                    .ConfigureAwait(false)
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
        MappingSet? mappingSet,
        ResourceInfo resourceInfo,
        RelationalWriteOperationKind operationKind,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences,
        Func<string, TResult> failureFactory,
        Func<DocumentReferenceFailure[], DescriptorReferenceFailure[], TResult> referenceFailureFactory,
        Func<MappingSet, QualifiedResourceName, Task> resolveTargetContextAsync
    )
    {
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(documentReferences);
        ArgumentNullException.ThrowIfNull(descriptorReferences);
        ArgumentNullException.ThrowIfNull(failureFactory);
        ArgumentNullException.ThrowIfNull(referenceFailureFactory);
        ArgumentNullException.ThrowIfNull(resolveTargetContextAsync);
        ArgumentNullException.ThrowIfNull(mappingSet);

        var resource = RelationalWriteSupport.ToQualifiedResourceName(resourceInfo);

        try
        {
            _ = RelationalWriteSupport.GetWritePlanOrThrow(mappingSet, resource);
            await resolveTargetContextAsync(mappingSet, resource).ConfigureAwait(false);

            var resolvedReferences = await _referenceResolver
                .ResolveAsync(
                    new ReferenceResolverRequest(
                        MappingSet: mappingSet,
                        RequestResource: resource,
                        DocumentReferences: documentReferences,
                        DescriptorReferences: descriptorReferences
                    )
                )
                .ConfigureAwait(false);

            if (resolvedReferences.HasFailures)
            {
                return referenceFailureFactory(
                    [.. resolvedReferences.InvalidDocumentReferences],
                    [.. resolvedReferences.InvalidDescriptorReferences]
                );
            }
        }
        catch (Exception ex)
            when (ex is NotSupportedException or InvalidOperationException or KeyNotFoundException)
        {
            return failureFactory(ex.Message);
        }

        return failureFactory(BuildUnsupportedWriteExecutionMessage(operationKind, resource));
    }

    private static string BuildUnsupportedWriteExecutionMessage(
        RelationalWriteOperationKind operationKind,
        QualifiedResourceName resource
    )
    {
        var operationLabel = operationKind switch
        {
            RelationalWriteOperationKind.Post => "POST",
            RelationalWriteOperationKind.Put => "PUT",
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };

        return $"Relational {operationLabel} write execution is not implemented for resource '{FormatResource(resource)}'. "
            + "Write-plan selection succeeded, but the relational write orchestration path is still pending.";
    }

    private static string FormatResource(QualifiedResourceName resource) =>
        RelationalWriteSupport.FormatResource(resource);
}
