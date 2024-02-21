// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.Model;

namespace EdFi.DataManagementService.Api.Backend;

/// <summary>
/// A document store repository that does nothing but returns success
/// </summary>
public class NoDocumentStoreRepository(ILogger<NoDocumentStoreRepository> _logger) : IDocumentStoreRepository
{
    public async Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
    {
        _logger.LogWarning("UpsertDocument(): No backend repository has been configured");
        return await Task.FromResult<UpsertResult>(new UpsertResult.InsertSuccess());
    }

    public async Task<GetResult> GetDocumentById(GetRequest getRequest)
    {
        _logger.LogWarning("GetDocumentById(): No backend repository has been configured");
        return await Task.FromResult<GetResult>(new GetResult.GetSuccess(
            DocumentUuid: No.DocumentUuid,
            EdfiDoc: new JsonObject(),
            LastModifiedDate: DateTime.Now
        ));
    }

    public async Task<UpdateResult> UpdateDocumentById(UpdateRequest updateRequest)
    {
        _logger.LogWarning("UpdateDocumentById(): No backend repository has been configured");
        return await Task.FromResult<UpdateResult>(new UpdateResult.UpdateSuccess());
    }

    public async Task<DeleteResult> DeleteDocumentById(DeleteRequest deleteRequest)
    {
        _logger.LogWarning("DeleteDocumentById(): No backend repository has been configured");
        return await Task.FromResult<DeleteResult>(new DeleteResult.DeleteSuccess());
    }
}
