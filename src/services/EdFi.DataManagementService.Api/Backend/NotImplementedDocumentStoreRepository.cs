// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Backend;

namespace EdFi.DataManagementService.Api.Core.Handler;

public abstract class NotImplementedDocumentStoreRepository : IDocumentStoreRepository
{
    public virtual Task<DeleteResult> DeleteDocumentById(DeleteRequest deleteRequest)
    {
        throw new NotImplementedException();
    }

    public virtual Task<GetResult> GetDocumentById(GetRequest getRequest)
    {
        throw new NotImplementedException();
    }

    public virtual Task<UpdateResult> UpdateDocumentById(UpdateRequest updateRequest)
    {
        throw new NotImplementedException();
    }

    public virtual Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
    {
        throw new NotImplementedException();
    }
}
