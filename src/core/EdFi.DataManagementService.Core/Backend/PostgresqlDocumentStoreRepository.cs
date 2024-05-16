// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Dapper;
using EdFi.DataManagementService.Core.Model;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Core.Backend;

public class PostgresqlDocumentStoreRepository(
    ILogger<PostgresqlDocumentStoreRepository> _logger,
    string _connectionString
) : IDocumentStoreRepository, IQueryHandler
{
    // Returns an integer in the range 0..15 from the last byte of a DocumentUuid
    private static int PartitionKeyFor(DocumentUuid documentUuid)
    {
        Guid asGuid = Guid.Parse(documentUuid.Value);
        byte lastByte = asGuid.ToByteArray()[^1];
        return lastByte % 16;
    }

    public async Task<UpsertResult> UpsertDocument(UpsertRequest upsertRequest)
    {
        _logger.LogDebug("Entering UpsertDocument - {TraceId}", upsertRequest.TraceId);

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            conn.Open();
            var command =
                $"INSERT INTO public.documents(document_partition_key, document_uuid, resource_name, edfi_doc) "
                + $"VALUES ({PartitionKeyFor(upsertRequest.DocumentUuid)}, '{upsertRequest.DocumentUuid.Value}', '{upsertRequest.ResourceInfo.ResourceName.Value}', '{upsertRequest.EdfiDoc.ToJsonString()}');";
            if (await conn.ExecuteAsync(command) == 1)
            {
                _logger.LogDebug("Insert Success: New Document UUID {DocumentUuid}", upsertRequest.DocumentUuid.Value);
                return new UpsertResult.InsertSuccess(upsertRequest.DocumentUuid);
            }
        }

        _logger.LogError("Unknown Error - {TraceId}", upsertRequest.TraceId);
        return new UpsertResult.UnknownFailure("Unknown Failure");
    }

    public async Task<GetResult> GetDocumentById(GetRequest getRequest)
    {
        _logger.LogDebug("Entering GetDocumentById - {TraceId}", getRequest.TraceId);

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(_connectionString);
            await using var command = dataSource.CreateCommand(
                $"SELECT edfi_doc FROM public.documents WHERE document_partition_key = {PartitionKeyFor(getRequest.DocumentUuid)} AND document_uuid = '{getRequest.DocumentUuid.Value}';"
            );
            await using var reader = await command.ExecuteReaderAsync();

            if (!reader.HasRows)
            {
                return new GetResult.GetFailureNotExists();
            }

            // Assumes only one row returned
            await reader.ReadAsync();
            JsonNode? edfiDoc = JsonNode.Parse(reader.GetString(0));

            if (edfiDoc == null)
            {
                return new GetResult.UnknownFailure("Unknown Failure");
            }

            // TODO: Documents table needs a last modified datetime
            return new GetResult.GetSuccess(getRequest.DocumentUuid, edfiDoc, DateTime.Now);
        }
        catch
        {
            _logger.LogError("Unknown Error - {TraceId}", getRequest.TraceId);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
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
        return await Task.FromResult<QueryResult>(new QueryResult.QuerySuccess(TotalCount: 0, EdfiDocs: []));
    }
}
