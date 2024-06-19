// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );
}

public class UpsertDocument(ISqlAction _sqlAction, ILogger<UpsertDocument> _logger) : IUpsertDocument
{
    public async Task<UpsertResult> AsInsert(
        IUpsertRequest upsertRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        int documentPartitionKey = PartitionKeyFor(upsertRequest.DocumentUuid).Value;
        long newDocumentId;

        // First insert into Documents
        try
        {
            upsertRequest.EdfiDoc["id"] = upsertRequest.DocumentUuid.Value;
            newDocumentId = await _sqlAction.InsertDocument(
                new(
                    DocumentPartitionKey: documentPartitionKey,
                    DocumentUuid: upsertRequest.DocumentUuid.Value,
                    ResourceName: upsertRequest.ResourceInfo.ResourceName.Value,
                    ResourceVersion: upsertRequest.ResourceInfo.ResourceVersion.Value,
                    ProjectName: upsertRequest.ResourceInfo.ProjectName.Value,
                    EdfiDoc: JsonSerializer.Deserialize<JsonElement>(upsertRequest.EdfiDoc)
                ),
                connection,
                transaction
            );
        }
        catch (PostgresException pe)
        {
            if (pe.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                _logger.LogDebug(
                    pe,
                    "Transaction conflict on Documents table insert - {TraceId}",
                    upsertRequest.TraceId
                );
                return new UpsertResult.UpsertFailureWriteConflict();
            }

            _logger.LogError(pe, "Failure on Documents table insert - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Upsert failure");
        }

        // Next insert into Aliases
        try
        {
            await _sqlAction.InsertAlias(
                new(
                    DocumentPartitionKey: documentPartitionKey,
                    DocumentId: newDocumentId,
                    ReferentialId: upsertRequest.DocumentInfo.ReferentialId.Value,
                    ReferentialPartitionKey: PartitionKeyFor(upsertRequest.DocumentInfo.ReferentialId).Value
                ),
                connection,
                transaction
            );

            if (upsertRequest.DocumentInfo.SuperclassReferentialId != null)
            {
                await _sqlAction.InsertAlias(
                    new(
                        DocumentPartitionKey: documentPartitionKey,
                        DocumentId: newDocumentId,
                        ReferentialId: upsertRequest.DocumentInfo.SuperclassReferentialId.Value.Value,
                        ReferentialPartitionKey: PartitionKeyFor(upsertRequest.DocumentInfo.SuperclassReferentialId.Value).Value
                    ),
                    connection,
                    transaction
                );
            }
        }
        catch (PostgresException pe)
        {
            if (pe.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                _logger.LogDebug(
                    pe,
                    "Transaction conflict on Aliases table insert - {TraceId}",
                    upsertRequest.TraceId
                );
                return new UpsertResult.UpsertFailureWriteConflict();
            }

            if (pe.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                _logger.LogInformation(
                    "Failure: alias identity already exists - {TraceId}",
                    upsertRequest.TraceId
                );

                return new UpsertResult.UpsertFailureIdentityConflict(
                    upsertRequest.ResourceInfo.ResourceName.Value,
                    upsertRequest.DocumentInfo.DocumentIdentity.DocumentIdentityElements.Select(d =>
                        new KeyValuePair<string, string>(d.IdentityJsonPath.Value.Substring(d.IdentityJsonPath.Value.LastIndexOf('.') + 1), d.IdentityValue)
                    )
                );
            }

            _logger.LogError(pe, "Failure on Aliases table insert - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Upsert failure");
        }

        _logger.LogDebug("Upsert success as insert - {TraceId}", upsertRequest.TraceId);
        return new UpsertResult.InsertSuccess(upsertRequest.DocumentUuid);
    }

    public async Task<UpsertResult> AsUpdate(
        int documentPartitionKey,
        Guid documentUuid,
        JsonNode edfiDoc,
        ITraceId traceId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        // Update the EdfiDoc of the Document
        try
        {
            edfiDoc["id"] = documentUuid;
            await _sqlAction.UpdateDocumentEdfiDoc(
                documentPartitionKey,
                documentUuid,
                JsonSerializer.Deserialize<JsonElement>(edfiDoc),
                connection,
                transaction
            );
        }
        catch (PostgresException pe)
        {
            if (pe.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                _logger.LogDebug(pe, "Transaction conflict on Documents table update - {TraceId}", traceId);
                return new UpsertResult.UpsertFailureWriteConflict();
            }

            _logger.LogError(pe, "Failure on on Documents table update - {TraceId}", traceId);
            return new UpsertResult.UnknownFailure("Upsert failure");
        }

        _logger.LogDebug("Upsert success as insert - {TraceId}", traceId);
        return new UpsertResult.UpdateSuccess(new(documentUuid));
    }

    /// <summary>
    /// Takes an UpsertRequest and connection + transaction and returns the result of an upsert operation.
    ///
    /// Connections and transactions are always managed by the caller based on the result.
    /// </summary>
    public async Task<UpsertResult> Upsert(
        IUpsertRequest upsertRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        _logger.LogDebug("Entering UpsertDocument.Upsert - {TraceId}", upsertRequest.TraceId);

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
            if (documentFromDb == null)
            {
                return await AsInsert(upsertRequest, connection, transaction);
            }

            return await AsUpdate(
                documentFromDb.DocumentPartitionKey,
                documentFromDb.DocumentUuid,
                upsertRequest.EdfiDoc,
                upsertRequest.TraceId,
                connection,
                transaction
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upsert failure - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }
}
