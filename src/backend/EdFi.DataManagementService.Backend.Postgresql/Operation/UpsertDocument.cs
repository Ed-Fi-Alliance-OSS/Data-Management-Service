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
    private static readonly string _beforeInsertReferences = "BeforeInsertReferences";

    /// <summary>
    /// Returns the ReferentialId Guids and corresponding partition keys for all of the document
    /// references in the UpsertRequest.
    /// </summary>
    public static DocumentReferenceIds DocumentReferenceIdsFrom(IUpsertRequest upsertRequest)
    {
        DocumentReference[] documentReferences = upsertRequest.DocumentInfo.DocumentReferences;
        Guid[] referentialIds = documentReferences.Select(x => x.ReferentialId.Value).ToArray();
        int[] referentialPartitionKeys = documentReferences
            .Select(x => PartitionKeyFor(x.ReferentialId).Value)
            .ToArray();
        return new(referentialIds, referentialPartitionKeys);
    }

    /// <summary>
    /// Returns the unique ResourceNames of all DocumentReferences that have the given ReferentialId Guids
    /// </summary>
    private ResourceName[] ResourceNamesFrom(DocumentReference[] documentReferences, Guid[] referentialIds)
    {
        Dictionary<Guid, string> guidToResourceNameMap =
            new(
                documentReferences.Select(x => new KeyValuePair<Guid, string>(
                    x.ReferentialId.Value,
                    x.ResourceInfo.ResourceName.Value
                ))
            );

        HashSet<string> uniqueResourceNames = [];

        foreach (Guid referentialId in referentialIds)
        {
            if (guidToResourceNameMap.TryGetValue(referentialId, out string? value))
            {
                uniqueResourceNames.Add(value);
            }
        }

        return uniqueResourceNames.Select(x => new ResourceName(x)).ToArray();
    }

    public async Task<UpsertResult> AsInsert(
        IUpsertRequest upsertRequest,
        DocumentReferenceIds documentReferenceIds,
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

        SuperclassIdentity? superclassIdentity = upsertRequest.DocumentInfo.SuperclassIdentity;

        try
        {
            // If subclass, also insert superclass version of identity into Aliases
            if (superclassIdentity != null)
            {
                await _sqlAction.InsertAlias(
                    new(
                        DocumentPartitionKey: documentPartitionKey,
                        DocumentId: newDocumentId,
                        ReferentialId: superclassIdentity.ReferentialId.Value,
                        ReferentialPartitionKey: PartitionKeyFor(superclassIdentity.ReferentialId).Value
                    ),
                    connection,
                    transaction
                );
            }
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            _logger.LogInformation(
                pe,
                "Failure: alias identity already exists - {TraceId}",
                upsertRequest.TraceId
            );

            return new UpsertResult.UpsertFailureIdentityConflict(
                upsertRequest.ResourceInfo.ResourceName,
                upsertRequest.DocumentInfo.DocumentIdentity.DocumentIdentityElements.Select(
                    d => new KeyValuePair<string, string>(
                        d.IdentityJsonPath.Value.Substring(d.IdentityJsonPath.Value.LastIndexOf('.') + 1),
                        d.IdentityValue
                    )
                )
            );
        }

        if (documentReferenceIds.ReferentialIds.Length > 0)
        {
            // Create a transaction savepoint in case insert into References fails due to invalid references
            await transaction.SaveAsync(_beforeInsertReferences);
            int numberOfRowsInserted = await _sqlAction.InsertReferences(
                new(
                    ParentDocumentPartitionKey: documentPartitionKey,
                    ParentDocumentId: newDocumentId,
                    ReferentialIds: documentReferenceIds.ReferentialIds,
                    ReferentialPartitionKeys: documentReferenceIds.ReferentialPartitionKeys
                ),
                connection,
                transaction
            );

            if (numberOfRowsInserted != documentReferenceIds.ReferentialIds.Length)
            {
                throw new InvalidOperationException("Database did not insert all references");
            }
        }

        _logger.LogDebug("Upsert success as insert - {TraceId}", upsertRequest.TraceId);
        return new UpsertResult.InsertSuccess(upsertRequest.DocumentUuid);
    }

    public async Task<UpsertResult> AsUpdate(
        long documentId,
        int documentPartitionKey,
        Guid documentUuid,
        IUpsertRequest upsertRequest,
        DocumentReferenceIds documentReferenceIds,
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

        if (documentReferenceIds.ReferentialIds.Length > 0)
        {
            // First clear out all the existing references, as they may have changed
            await _sqlAction.DeleteReferencesByDocumentUuid(
                documentPartitionKey,
                documentUuid,
                connection,
                transaction
            );

            // Create a transaction savepoint in case insert into References fails due to invalid references
            await transaction.SaveAsync(_beforeInsertReferences);
            int numberOfRowsInserted = await _sqlAction.InsertReferences(
                new(
                    ParentDocumentPartitionKey: documentPartitionKey,
                    ParentDocumentId: documentId,
                    ReferentialIds: documentReferenceIds.ReferentialIds,
                    ReferentialPartitionKeys: documentReferenceIds.ReferentialPartitionKeys
                ),
                connection,
                transaction
            );

            if (numberOfRowsInserted != documentReferenceIds.ReferentialIds.Length)
            {
                throw new InvalidOperationException("Database did not insert all references");
            }
        }

        _logger.LogDebug("Upsert success as update - {TraceId}", upsertRequest.TraceId);
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

        DocumentReferenceIds documentReferenceIds = DocumentReferenceIdsFrom(upsertRequest);

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
                return await AsInsert(upsertRequest, documentReferenceIds, connection, transaction);
            }

            long documentId =
                documentFromDb.Id
                ?? throw new InvalidOperationException("documentFromDb.Id should never be null");

            return await AsUpdate(
                documentId,
                documentFromDb.DocumentPartitionKey,
                documentFromDb.DocumentUuid,
                upsertRequest,
                documentReferenceIds,
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

            // Restore transaction savepoint to continue using transaction
            await transaction.RollbackAsync(_beforeInsertReferences);

            Guid[] invalidReferentialIds = await _sqlAction.FindInvalidReferentialIds(
                documentReferenceIds,
                connection,
                transaction
            );

            ResourceName[] invalidResourceNames = ResourceNamesFrom(
                upsertRequest.DocumentInfo.DocumentReferences,
                invalidReferentialIds
            );

            return new UpsertResult.UpsertFailureReference(invalidResourceNames);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upsert failure - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }
}
