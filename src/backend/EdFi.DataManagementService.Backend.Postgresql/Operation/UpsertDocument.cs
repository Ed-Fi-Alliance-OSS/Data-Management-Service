// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Postgresql.Model;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Npgsql;
using static EdFi.DataManagementService.Backend.PartitionUtility;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IUpsertDocument
{
    public Task<UpsertResult> Upsert(
        IUpsertRequest upsertRequest,
        NpgsqlDataSource dataSource
    );
}

public class UpsertDocument(ISqlAction _sqlAction, ILogger<UpsertDocument> _logger) : IUpsertDocument
{
    public async Task<UpsertResult> Upsert(IUpsertRequest upsertRequest, NpgsqlDataSource dataSource)
    {
        _logger.LogDebug("Entering UpsertDocument.Upsert - {TraceId}", upsertRequest.TraceId);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Attempt to get the document, to see whether this is an insert or update
            Document? documentFromDb = await _sqlAction.FindDocumentByReferentialId(
                upsertRequest.DocumentInfo.ReferentialId,
                PartitionKeyFor(upsertRequest.DocumentInfo.ReferentialId),
                connection,
                transaction
            );

            // Either get the existing document uuid or use the new one provided
            DocumentUuid documentUuid =
                documentFromDb == null
                    ? upsertRequest.DocumentUuid
                    : new DocumentUuid(documentFromDb.DocumentUuid);

            //// Continue here - decide update or insert
            try
            {
                int resultCount = await _sqlAction.InsertDocument(
                    new(
                        DocumentPartitionKey: PartitionKeyFor(documentUuid).Value,
                        DocumentUuid: documentUuid.Value,
                        ResourceName: upsertRequest.ResourceInfo.ResourceName.Value,
                        ResourceVersion: upsertRequest.ResourceInfo.ResourceVersion.Value,
                        ProjectName: upsertRequest.ResourceInfo.ProjectName.Value,
                        EdfiDoc: JsonSerializer.Deserialize<JsonElement>(upsertRequest.EdfiDoc)
                    ),
                    connection,
                    transaction
                );

                if (resultCount != 1)
                {
                    _logger.LogError(
                        "Upsert result count was {ResultCount} for {DocumentUuid}, cause is unknown - {TraceId}",
                        resultCount,
                        upsertRequest.DocumentUuid,
                        upsertRequest.TraceId
                    );
                    await transaction.RollbackAsync();
                    return new UpsertResult.UnknownFailure("Unknown Failure");
                }
            }
            catch (PostgresException pe)
            {
                if (
                    pe.SqlState == PostgresErrorCodes.IntegrityConstraintViolation
                    || pe.SqlState == PostgresErrorCodes.UniqueViolation
                )
                {
                    _logger.LogError(
                        pe,
                        "Upsert failure due to duplicate DocumentUuids on insert. This shouldn't happen - {TraceId}",
                        upsertRequest.TraceId
                    );
                    await transaction.RollbackAsync();
                    return new UpsertResult.UnknownFailure(
                        "Upsert failure due to duplicate DocumentUuids on insert"
                    );
                }
            }

            _logger.LogDebug("Upsert success as insert - {TraceId}", upsertRequest.TraceId);
            await transaction.CommitAsync();
            return new UpsertResult.InsertSuccess(upsertRequest.DocumentUuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upsert failure - {TraceId}", upsertRequest.TraceId);
            await transaction.RollbackAsync();
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }
}
