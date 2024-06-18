// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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

        // Next insert into Aliases
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

		try {	
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
		catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.UniqueViolation)
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

        DocumentReference[] documentReferences = upsertRequest.DocumentInfo.DocumentReferences;

        if (documentReferences.Length > 0)
        {
            // Next insert into References
            int numberOfRowsInserted = await _sqlAction.InsertReferences(
                new(
                    ParentDocumentPartitionKey: documentPartitionKey,
                    ParentDocumentId: newDocumentId,
                    ReferentialIds: documentReferences.Select(x => x.ReferentialId.Value).ToArray(),
                    ReferentialPartitionKeys: documentReferences
                        .Select(x => PartitionKeyFor(x.ReferentialId).Value)
                        .ToArray()
                ),
                connection,
                transaction
            );

            Trace.Assert(
                numberOfRowsInserted == documentReferences.Length,
                "Database did not insert all references"
            );
        }

        _logger.LogDebug("Upsert success as insert - {TraceId}", upsertRequest.TraceId);
        return new UpsertResult.InsertSuccess(upsertRequest.DocumentUuid);
    }

    public async Task<UpsertResult> AsUpdate(
        int documentPartitionKey,
        Guid documentUuid,
        IUpsertRequest upsertRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        // Update the EdfiDoc of the Document
        upsertRequest.EdfiDoc["id"] = documentUuid;
        await _sqlAction.UpdateDocumentEdfiDoc(
            documentPartitionKey,
            documentUuid,
            JsonSerializer.Deserialize<JsonElement>(upsertRequest.EdfiDoc),
            connection,
            transaction
        );

        _logger.LogDebug("Upsert success as insert - {TraceId}", upsertRequest.TraceId);
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
            Document? documentFromDb;
            try
            {
                // Attempt to get the document, to see whether this is an insert or update
                documentFromDb = await _sqlAction.FindDocumentByReferentialId(
                    upsertRequest.DocumentInfo.ReferentialId,
                    PartitionKeyFor(upsertRequest.DocumentInfo.ReferentialId),
                    connection,
                    transaction,
                    LockOption.BlockUpdateDelete
                );
            }
            catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                _logger.LogDebug(
                    pe,
                    "Transaction conflict on Documents table read - {TraceId}",
                    upsertRequest.TraceId
                );
                return new UpsertResult.UpsertFailureWriteConflict();
            }

            // Either get the existing document uuid or use the new one provided
            if (documentFromDb == null)
            {
                return await AsInsert(upsertRequest, connection, transaction);
            }

            return await AsUpdate(
                documentFromDb.DocumentPartitionKey,
                documentFromDb.DocumentUuid,
                upsertRequest,
                connection,
                transaction
            );
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            _logger.LogDebug(pe, "Transaction conflict on Upsert - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UpsertFailureWriteConflict();
        }
        catch (PostgresException pe)
            when (pe.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && pe.ConstraintName == "fk_references_referencedalias"
            )
        {
            _logger.LogDebug(pe, "Foreign key violation on Upsert - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UpsertFailureReference("See DMS-259");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upsert failure - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }
}
