// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// A document store repository that does nothing but return success
/// </summary>
internal class SuccessDocumentStoreRepository(ILogger<SuccessDocumentStoreRepository> _logger)
    : IDocumentStoreRepository,
        IQueryHandler
{
    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        _logger.LogWarning(
            "UpsertDocument(): Backend repository has been configured to always report success - {TraceId}",
            upsertRequest.TraceId
        );
        return await Task.FromResult<UpsertResult>(
            new UpsertResult.InsertSuccess(upsertRequest.DocumentUuid)
        );
    }

    public async Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        _logger.LogWarning(
            "GetDocumentById(): Backend repository has been configured to always report success - {TraceId}",
            getRequest.TraceId
        );
        return await Task.FromResult<GetResult>(
            new GetResult.GetSuccess(
                DocumentUuid: No.DocumentUuid,
                EdfiDoc: new JsonObject(),
                LastModifiedDate: DateTime.Now,
                getRequest.TraceId.Value
            )
        );
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        _logger.LogWarning(
            "UpdateDocumentById(): Backend repository has been configured to always report success - {TraceId}",
            updateRequest.TraceId
        );
        return await Task.FromResult<UpdateResult>(new UpdateResult.UpdateSuccess(updateRequest.DocumentUuid));
    }

    public async Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        _logger.LogWarning(
            "DeleteDocumentById(): Backend repository has been configured to always report success  - {TraceId}",
            deleteRequest.TraceId
        );
        return await Task.FromResult<DeleteResult>(new DeleteResult.DeleteSuccess());
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
