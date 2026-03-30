// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

public sealed class RelationalDocumentStoreRepository(ILogger<RelationalDocumentStoreRepository> logger)
    : IDocumentStoreRepository,
        IQueryHandler
{
    private readonly ILogger<RelationalDocumentStoreRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private const string DescriptorWriteStoryRef = "E07-S06 (06-descriptor-writes.md)";

    public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        ArgumentNullException.ThrowIfNull(upsertRequest);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.UpsertDocument - {TraceId}",
            upsertRequest.TraceId.Value
        );

        return Task.FromResult<UpsertResult>(
            ExecuteWriteGuardRails(
                upsertRequest.MappingSet,
                upsertRequest.ResourceInfo,
                RelationalWriteOperationKind.Post,
                static failureMessage => new UpsertResult.UnknownFailure(failureMessage)
            )
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

        return Task.FromResult<UpdateResult>(
            ExecuteWriteGuardRails(
                updateRequest.MappingSet,
                updateRequest.ResourceInfo,
                RelationalWriteOperationKind.Put,
                static failureMessage => new UpdateResult.UnknownFailure(failureMessage)
            )
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
                $"Relational DELETE is not implemented for resource '{FormatResource(ToQualifiedResourceName(deleteRequest.ResourceInfo))}'."
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
                $"Relational query handling is not implemented for resource '{FormatResource(ToQualifiedResourceName(queryRequest.ResourceInfo))}'."
            )
        );
    }

    private static TResult ExecuteWriteGuardRails<TResult>(
        MappingSet? mappingSet,
        ResourceInfo resourceInfo,
        RelationalWriteOperationKind operationKind,
        Func<string, TResult> failureFactory
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(failureFactory);

        var resource = ToQualifiedResourceName(resourceInfo);

        try
        {
            _ = GetWritePlanOrThrow(mappingSet, resource);
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

    private static QualifiedResourceName ToQualifiedResourceName(BaseResourceInfo resourceInfo) =>
        new(resourceInfo.ProjectName.Value, resourceInfo.ResourceName.Value);

    private static string FormatResource(QualifiedResourceName resource) =>
        $"{resource.ProjectName}.{resource.ResourceName}";

    private static ResourceWritePlan GetWritePlanOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        if (mappingSet.WritePlansByResource.TryGetValue(resource, out var writePlan))
        {
            return writePlan;
        }

        var concreteResourceModel = mappingSet.Model.ConcreteResourcesInNameOrder.SingleOrDefault(model =>
            model.ResourceKey.Resource == resource
        );

        if (concreteResourceModel is null)
        {
            throw new KeyNotFoundException(
                $"Mapping set '{FormatMappingSetKey(mappingSet.Key)}' does not contain resource '{FormatResource(resource)}' in ConcreteResourcesInNameOrder."
            );
        }

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Write plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses the descriptor write path instead of compiled relational-table write plans. "
                    + $"Next story: {DescriptorWriteStoryRef}."
            );
        }

        if (concreteResourceModel.StorageKind == ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Write plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                    + $"'{FormatMappingSetKey(mappingSet.Key)}': resource storage kind "
                    + $"'{ResourceStorageKind.RelationalTables}' should always have a compiled relational-table write plan, but no entry "
                    + "was found. This indicates an internal compilation/selection bug."
            );
        }

        throw new InvalidOperationException(
            $"Write plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                + $"'{FormatMappingSetKey(mappingSet.Key)}': storage kind '{concreteResourceModel.StorageKind}' "
                + "is not recognized."
        );
    }

    private static string FormatMappingSetKey(MappingSetKey key) =>
        $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
}
