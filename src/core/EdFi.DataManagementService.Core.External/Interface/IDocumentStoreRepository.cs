// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.External.Interface;

/// <summary>
/// The repository DMS Core uses to access a document store.
/// </summary>
public interface IDocumentStoreRepository
{
    /// <summary>
    /// Entry point for upsert document requests
    /// </summary>
    public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest);

    /// <summary>
    /// Entry point for get document by id requests
    /// </summary>
    public Task<GetResult> GetDocumentById(IGetRequest getRequest);

    /// <summary>
    /// Entry point for update document by id requests
    /// </summary>
    public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest);

    /// <summary>
    /// Entry point for delete document by id requests
    /// </summary>
    public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest);
}
