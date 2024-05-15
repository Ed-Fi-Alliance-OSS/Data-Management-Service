// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Core.Backend;

public class PostgresqlDocumentStoreRepository(ILogger<PostgresqlDocumentStoreRepository> _logger, string _connectionString = "host=localhost;port=5432;username=postgres;database=EdFi.DataManagementService;pooling=true;minimum pool size=10;maximum pool size=50;Application Name=EdFi.DataManagementService")
    : IDocumentStoreRepository, IQueryHandler
{
    public async Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
    {
        _logger.LogInformation(_connectionString);
        _logger.LogWarning(
            "UpsertDocument(): Backend repository has been configured to always report success - {TraceId}",
            upsertRequest.TraceId
        );

        using (var conn = new NpgsqlConnection(_connectionString))
        {
            conn.Open();
            var command =
                "INSERT INTO public.documents(id, document_partition_key, document_uuid, resource_name, edfi_doc) VALUES (1, 1, 'abc', 'test', 'fake');";
            await conn.ExecuteAsync(command);
        }
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
