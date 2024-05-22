// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

public class PostgresqlDocumentStoreRepository(
    ILogger<PostgresqlDocumentStoreRepository> _logger,
    string _connectionString
) : PartitionedRepository, IDocumentStoreRepository, IQueryHandler
{
    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        _logger.LogDebug("Entering UpsertDocument - {TraceId}", upsertRequest.TraceId);

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO public.documents(document_partition_key, document_uuid, resource_name, edfi_doc)
                    VALUES (@document_partition_key, @document_uuid, @resource_name, @edfi_doc);",
                conn
            )
            {
                Parameters =
                {
                    new("document_partition_key", PartitionKeyFor(upsertRequest.DocumentUuid)),
                    new("document_uuid", upsertRequest.DocumentUuid.Value),
                    new("resource_name", upsertRequest.ResourceInfo.ResourceName.Value),
                    new("edfi_doc", upsertRequest.EdfiDoc.ToJsonString())
                }
            };

            int resultCount = await cmd.ExecuteNonQueryAsync();
            if (resultCount == 1)
            {
                _logger.LogDebug("Upsert success - {TraceId}", upsertRequest.TraceId);
                return new UpsertResult.InsertSuccess(upsertRequest.DocumentUuid);
            }

            _logger.LogError(
                "Upsert result count was {ResultCount} for {DocumentUuid}, cause is unknown - {TraceId}",
                resultCount,
                upsertRequest.DocumentUuid,
                upsertRequest.TraceId
            );
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upsert failed - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        _logger.LogDebug("Entering GetDocumentById - {TraceId}", getRequest.TraceId);

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var command = new NpgsqlCommand(
                @"SELECT edfi_doc FROM public.documents
                WHERE document_partition_key = @document_partition_key AND document_uuid = @document_uuid;",
                conn
            )
            {
                Parameters =
                {
                    new("document_partition_key", PartitionKeyFor(getRequest.DocumentUuid)),
                    new("document_uuid", getRequest.DocumentUuid.Value)
                }
            };

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
                return new GetResult.UnknownFailure(
                    $"Unable to parse JSON for document {getRequest.DocumentUuid}."
                );
            }

            // TODO: Documents table needs a last modified datetime
            return new GetResult.GetSuccess(getRequest.DocumentUuid, edfiDoc, DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDocumentById failed - {TraceId}", getRequest.TraceId);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        _logger.LogDebug("Entering UpdateDocumentById - {TraceId}", updateRequest.TraceId);

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"UPDATE public.documents
                    SET edfi_doc = @edfi_doc
                    WHERE document_partition_key = @document_partition_key AND document_uuid = @document_uuid;",
                conn
            )
            {
                Parameters =
                {
                    new("document_partition_key", PartitionKeyFor(updateRequest.DocumentUuid)),
                    new("document_uuid", updateRequest.DocumentUuid.Value),
                    new("edfi_doc", updateRequest.EdfiDoc.ToJsonString())
                }
            };

            var result = await cmd.ExecuteNonQueryAsync();

            switch (result)
            {
                case 1:
                    return await Task.FromResult<UpdateResult>(new UpdateResult.UpdateSuccess());
                case 0:
                    _logger.LogError(
                        "Error: Record to update does not exist - {TraceId}",
                        updateRequest.TraceId
                    );
                    return await Task.FromResult<UpdateResult>(new UpdateResult.UpdateFailureNotExists());
                default:
                    _logger.LogError(
                        "UpdateDocumentById result count was '{Result}' for {DocumentUuid} - {TraceId}",
                        result,
                        updateRequest.DocumentUuid,
                        updateRequest.TraceId
                    );
                    return await Task.FromResult<UpdateResult>(
                        new UpdateResult.UnknownFailure("Unknown Failure")
                    );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateDocumentById failed - {TraceId}", updateRequest.TraceId);
            return await Task.FromResult<UpdateResult>(new UpdateResult.UnknownFailure("Unknown Failure"));
        }
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
