// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// A document store repository that does nothing but return success
/// </summary>
internal class SuccessDocumentStoreRepository(ILogger<SuccessDocumentStoreRepository> _logger)
    : IDocumentStoreRepository, IQueryHandler
{
    public async Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
    {
        _logger.LogWarning(
            "UpsertDocument(): Backend repository has been configured to always report success - {TraceId}",
            upsertRequest.TraceId
        );
        return await Task.FromResult<UpsertResult>(new UpsertResult.InsertSuccess());
    }

    public async Task<GetResult> GetDocumentById(GetRequest getRequest)
    {
        _logger.LogWarning(
            "GetDocumentById(): Backend repository has been configured to always report success - {TraceId}",
            getRequest.TraceId
        );
        return await Task.FromResult<GetResult>(
            new GetResult.GetSuccess(
                DocumentUuid: No.DocumentUuid,
                EdfiDoc: new JsonObject(),
                LastModifiedDate: DateTime.Now
            )
        );
    }

    public async Task<UpdateResult> UpdateDocumentById(UpdateRequest updateRequest)
    {
        _logger.LogWarning(
            "UpdateDocumentById(): Backend repository has been configured to always report success - {TraceId}",
            updateRequest.TraceId
        );
        return await Task.FromResult<UpdateResult>(new UpdateResult.UpdateSuccess());
    }

    public async Task<DeleteResult> DeleteDocumentById(DeleteRequest deleteRequest)
    {
        _logger.LogWarning(
            "DeleteDocumentById(): Backend repository has been configured to always report success  - {TraceId}",
            deleteRequest.TraceId
        );
        return await Task.FromResult<DeleteResult>(new DeleteResult.DeleteSuccess());
    }

    public async Task<QueryResult> QueryDocuments(QueryRequest queryRequest)
    {
        _logger.LogWarning(
            "QueryDocuments(): Backend repository has been configured to always report success - {TraceId}",
            queryRequest.TraceId
        );
        return await Task.FromResult<QueryResult>(
            new QueryResult.QuerySuccess(
                TotalCount: 0,
                EdfiDocs: []
            )
        );
    }
}
