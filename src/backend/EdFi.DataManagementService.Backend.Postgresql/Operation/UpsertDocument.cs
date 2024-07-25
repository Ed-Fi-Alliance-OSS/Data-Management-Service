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
using static EdFi.DataManagementService.Backend.Postgresql.ReferenceHelper;
using static EdFi.DataManagementService.Backend.Postgresql.Operation.SqlAction;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IUpsertDocument
{
    public Task<UpsertResult> Upsert(
        IUpsertRequest upsertRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );
}

public class UpsertDocument(ILogger<UpsertDocument> _logger) : IUpsertDocument
{
    private static readonly string _beforeInsertReferences = "BeforeInsertReferences";

    public async Task<UpsertResult> AsInsert(
        IUpsertRequest upsertRequest,
        DocumentReferenceIds documentReferenceIds,
        DocumentReferenceIds descriptorReferenceIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        int documentPartitionKey = PartitionKeyFor(upsertRequest.DocumentUuid).Value;
        long newDocumentId;

        // First insert into Documents
        upsertRequest.EdfiDoc["id"] = upsertRequest.DocumentUuid.Value;
        newDocumentId = await InsertDocument(
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
        await InsertAlias(
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
                await InsertAlias(
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

        if (documentReferenceIds.ReferentialIds.Length > 0 || descriptorReferenceIds.ReferentialIds.Length > 0)
        {
            // Create a transaction save point in case insert into References fails due to invalid references
            await transaction.SaveAsync(_beforeInsertReferences);
            int numberOfRowsInserted = await InsertReferences(
                new(
                    ParentDocumentPartitionKey: documentPartitionKey,
                    ParentDocumentId: newDocumentId,
                    ReferentialIds: documentReferenceIds.ReferentialIds.Concat(descriptorReferenceIds.ReferentialIds).ToArray(),
                    ReferentialPartitionKeys: documentReferenceIds.ReferentialPartitionKeys.Concat(descriptorReferenceIds.ReferentialPartitionKeys).ToArray()
                ),
                connection,
                transaction
            );

            if (numberOfRowsInserted != documentReferenceIds.ReferentialIds.Length + descriptorReferenceIds.ReferentialIds.Length)
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
        DocumentReferenceIds descriptorReferenceIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        // Update the EdfiDoc of the Document
        upsertRequest.EdfiDoc["id"] = documentUuid;
        await UpdateDocumentEdfiDoc(
            documentPartitionKey,
            documentUuid,
            JsonSerializer.Deserialize<JsonElement>(upsertRequest.EdfiDoc),
            connection,
            transaction
        );

        if (documentReferenceIds.ReferentialIds.Length > 0 || descriptorReferenceIds.ReferentialIds.Length > 0)
        {
            // First clear out all the existing references, as they may have changed
            await DeleteReferencesByDocumentUuid(
                documentPartitionKey,
                documentUuid,
                connection,
                transaction
            );

            // Create a transaction save point in case insert into References fails due to invalid references
            await transaction.SaveAsync(_beforeInsertReferences);
            int numberOfRowsInserted = await InsertReferences(
                new(
                    ParentDocumentPartitionKey: documentPartitionKey,
                    ParentDocumentId: documentId,
                    ReferentialIds: documentReferenceIds.ReferentialIds.Concat(descriptorReferenceIds.ReferentialIds).ToArray(),
                    ReferentialPartitionKeys: documentReferenceIds.ReferentialPartitionKeys.Concat(descriptorReferenceIds.ReferentialPartitionKeys).ToArray()
                ),
                connection,
                transaction
            );

            if (numberOfRowsInserted != documentReferenceIds.ReferentialIds.Length + descriptorReferenceIds.ReferentialIds.Length)
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

        DocumentReferenceIds documentReferenceIds = DocumentReferenceIdsFrom(
            upsertRequest.DocumentInfo.DocumentReferences
        );

        DocumentReferenceIds descriptorReferenceIds = DescriptorReferenceIdsFrom(
            upsertRequest.DocumentInfo.DescriptorReferences
        );

        try
        {
            Document? documentFromDb;
            try
            {
                // Attempt to get the document, to see whether this is an insert or update
                documentFromDb = await FindDocumentByReferentialId(
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
                return await AsInsert(upsertRequest, documentReferenceIds, descriptorReferenceIds, connection, transaction);
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
                descriptorReferenceIds,
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
                && pe.ConstraintName == ReferenceValidationFkName
            )
        {
            _logger.LogDebug(pe, "Foreign key violation on Upsert - {TraceId}", upsertRequest.TraceId);

            // Restore transaction save point to continue using transaction
            await transaction.RollbackAsync(_beforeInsertReferences);

            Guid[] invalidReferentialIds = await FindInvalidReferentialIds(
                documentReferenceIds,
                descriptorReferenceIds,
                connection,
                transaction
            );

            var invalidDescriptorReferences =
                upsertRequest.DocumentInfo.DescriptorReferences.Where(d =>
                    invalidReferentialIds.Contains(d.ReferentialId.Value)).ToList();

            if (invalidDescriptorReferences.Any())
            {
                return new UpsertResult.UpsertFailureDescriptorReference(invalidDescriptorReferences);
            }

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
