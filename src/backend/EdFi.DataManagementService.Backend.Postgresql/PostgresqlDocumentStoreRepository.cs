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
    NpgsqlDataSource _dataSource
) : PartitionedRepository, IDocumentStoreRepository, IQueryHandler
{
    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        _logger.LogDebug("Entering UpsertDocument - {TraceId}", upsertRequest.TraceId);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();

            await using var insertDocumentCmd = new NpgsqlCommand(
                @"INSERT INTO public.Documents(DocumentPartitionKey, DocumentUuid, ResourceName, ResourceVersion, ProjectName, EdfiDoc)
                    VALUES ($1, $2::UUID, $3, $4, $5, $6);",
                conn
            )
            {
                Parameters =
                {
                    new() { Value = PartitionKeyFor(upsertRequest.DocumentUuid) },
                    new() { Value = upsertRequest.DocumentUuid.Value },
                    new() { Value = upsertRequest.ResourceInfo.ResourceName.Value },
                    new() { Value = upsertRequest.ResourceInfo.ResourceVersion.Value },
                    new() { Value = upsertRequest.ResourceInfo.ProjectName.Value },
                    new() { Value = upsertRequest.EdfiDoc.ToJsonString() },
                }
            };

            int resultCount = await insertDocumentCmd.ExecuteNonQueryAsync();
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
            await using var conn = await _dataSource.OpenConnectionAsync();

            await using NpgsqlCommand cmd =
                new(
                    @"SELECT EdfiDoc FROM public.Documents WHERE DocumentPartitionKey = $1 AND DocumentUuid = $2::UUID;",
                    conn
                )
                {
                    Parameters =
                    {
                        new() { Value = PartitionKeyFor(getRequest.DocumentUuid) },
                        new() { Value = getRequest.DocumentUuid.Value },
                    }
                };

            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();

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
            await using var conn = await _dataSource.OpenConnectionAsync();

            await using var cmd = new NpgsqlCommand(
                @"UPDATE public.documents
                    SET EdfiDoc = $1
                    WHERE DocumentPartitionKey = $2 AND DocumentUuid = $3;",
                conn
            )
            {
                Parameters =
                {
                    new() { Value = updateRequest.EdfiDoc.ToJsonString() },
                    new() { Value = PartitionKeyFor(updateRequest.DocumentUuid) },
                    new() { Value = updateRequest.DocumentUuid.Value },
                }
            };

            int rowsAffected = await cmd.ExecuteNonQueryAsync();

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
