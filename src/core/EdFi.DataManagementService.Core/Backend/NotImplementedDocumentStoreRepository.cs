// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;

namespace EdFi.DataManagementService.Core.Backend;

internal abstract class NotImplementedDocumentStoreRepository : IDocumentStoreRepository, IQueryHandler
{
    public virtual Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        throw new NotImplementedException();
    }

    public virtual Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        throw new NotImplementedException();
    }

    public virtual Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        throw new NotImplementedException();
    }

    public virtual Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        throw new NotImplementedException();
    }

    public virtual Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        throw new NotImplementedException();
    }
}
