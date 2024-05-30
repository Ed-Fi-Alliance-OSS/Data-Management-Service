// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

public class PostgresqlDocumentStoreRepository(
    NpgsqlDataSource _dataSource,
    ILogger<PostgresqlDocumentStoreRepository> _logger,
    IUpsertDocument _upsertDocument
) : PartitionedRepository, IDocumentStoreRepository, IQueryHandler
{
    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        _logger.LogDebug("Entering PostgresqlDocumentStoreRepository.UpsertDocument - {TraceId}", upsertRequest.TraceId);

        try
        {
            return await _upsertDocument.Upsert(upsertRequest, _dataSource);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught Upsert failure - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        _logger.LogDebug("Entering GetDocumentById - {TraceId}", getRequest.TraceId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();

            await using NpgsqlCommand command =
                new(
                    @"SELECT EdfiDoc FROM public.Documents WHERE DocumentPartitionKey = $1 AND DocumentUuid = $2;",
                    connection
                )
                {
                    Parameters =
                    {
                        new() { Value = PartitionKeyFor(getRequest.DocumentUuid) },
                        new() { Value = getRequest.DocumentUuid.Value },
                    }
                };

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

            if (!reader.HasRows)
            {
                return new GetResult.GetFailureNotExists();
            }

            // Assumes only one row returned
            await reader.ReadAsync();
            JsonNode? edfiDoc = (await reader.GetFieldValueAsync<JsonElement>(0)).Deserialize<JsonNode>();

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
            _logger.LogError(ex, "GetDocumentById failure - {TraceId}", getRequest.TraceId);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        _logger.LogDebug("Entering UpdateDocumentById - {TraceId}", updateRequest.TraceId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();

            await using var command = new NpgsqlCommand(
                @"UPDATE public.documents
                    SET EdfiDoc = $1
                    WHERE DocumentPartitionKey = $2 AND DocumentUuid = $3;",
                connection
            )
            {
                Parameters =
                {
                    new() { Value = updateRequest.EdfiDoc.ToJsonString() },
                    new() { Value = PartitionKeyFor(updateRequest.DocumentUuid) },
                    new() { Value = updateRequest.DocumentUuid.Value },
                }
            };

            int rowsAffected = await command.ExecuteNonQueryAsync();

            switch (rowsAffected)
            {
                case 1:
                    return await Task.FromResult<UpdateResult>(new UpdateResult.UpdateSuccess());
                case 0:
                    _logger.LogInformation(
                        "Failure: Record to update does not exist - {TraceId}",
                        updateRequest.TraceId
                    );
                    return await Task.FromResult<UpdateResult>(new UpdateResult.UpdateFailureNotExists());
                default:
                    _logger.LogError(
                        "UpdateDocumentById rows affected was '{RowsAffected}' for {DocumentUuid} - {TraceId}",
                        rowsAffected,
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
            _logger.LogError(ex, "UpdateDocumentById failure - {TraceId}", updateRequest.TraceId);
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
