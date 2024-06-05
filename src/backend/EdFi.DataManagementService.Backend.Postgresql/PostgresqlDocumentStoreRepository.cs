// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Postgresql;

public class PostgresqlDocumentStoreRepository(
    ILogger<PostgresqlDocumentStoreRepository> _logger,
    IGetDocumentById _getDocumentById,
    IUpdateDocumentById _updateDocumentById,
    IUpsertDocument _upsertDocument,
    IDeleteDocumentById _deleteDocumentById
) : IDocumentStoreRepository, IQueryHandler
{
    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.UpsertDocument - {TraceId}",
            upsertRequest.TraceId
        );

        try
        {
            return await _upsertDocument.Upsert(upsertRequest);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught Upsert failure - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.GetDocumentById - {TraceId}",
            getRequest.TraceId
        );

        try
        {
            return await _getDocumentById.GetById(getRequest);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught GetById failure - {TraceId}", getRequest.TraceId);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.UpdateDocumentById - {TraceId}",
            updateRequest.TraceId
        );

        try
        {
            return await _updateDocumentById.UpdateById(updateRequest);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught UpdateById failure - {TraceId}", updateRequest.TraceId);
            return new UpdateResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.DeleteDocumentById  - {TraceId}",
            deleteRequest.TraceId
        );

        try
        {
            return await _deleteDocumentById.DeleteById(deleteRequest);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught DeleteById failure - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        _logger.LogWarning(
            "QueryDocuments(): Backend repository has been configured to always report success - {TraceId}",
            queryRequest.TraceId
        );
        return await Task.FromResult<QueryResult>(new QueryResult.QuerySuccess(TotalCount: 0, EdfiDocs: []));
    }
}
