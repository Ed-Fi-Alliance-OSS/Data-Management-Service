// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Backend;

public class PostgresqlDocumentStoreRepository : IDocumentStoreRepository
{
    public Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
    {
        throw new NotImplementedException();
    }

    public Task<GetResult> GetDocumentById(GetRequest getRequest)
    {
        throw new NotImplementedException();
    }

    public Task<UpdateResult> UpdateDocumentById(UpdateRequest updateRequest)
    {
        throw new NotImplementedException();
    }

    public Task<DeleteResult> DeleteDocumentById(DeleteRequest deleteRequest)
    {
        throw new NotImplementedException();
    }
}
