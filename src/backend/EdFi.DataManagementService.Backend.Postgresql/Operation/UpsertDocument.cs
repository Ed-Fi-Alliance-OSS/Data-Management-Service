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

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IUpsertDocument
{
    public Task<UpsertResult> Upsert(
        IUpsertRequest upsertRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    );
}

public class UpsertDocument(ISqlAction _sqlAction, ILogger<UpsertDocument> _logger) : IUpsertDocument
{
    /// <summary>
    /// Determine whether invalid referentialIds were descriptors or references, and returns the
    /// appropriate failure.
    /// </summary>
    private static UpsertResult ReportReferenceFailure(
        DocumentInfo documentInfo,
        Guid[] invalidReferentialIds
    )
    {
        List<DescriptorReference> invalidDescriptorReferences = DescriptorReferencesWithReferentialIds(
            documentInfo.DescriptorReferences,
            invalidReferentialIds
        );

        if (invalidDescriptorReferences.Count != 0)
        {
            return new UpsertResult.UpsertFailureDescriptorReference(invalidDescriptorReferences);
        }

        ResourceName[] invalidResourceNames = ResourceNamesFrom(
            documentInfo.DocumentReferences,
            invalidReferentialIds
        );

        return new UpsertResult.UpsertFailureReference(invalidResourceNames);
    }

    public async Task<UpsertResult> AsInsert(
        IUpsertRequest upsertRequest,
        DocumentReferenceIds documentReferenceIds,
        DocumentReferenceIds descriptorReferenceIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        short documentPartitionKey = PartitionKeyFor(upsertRequest.DocumentUuid).Value;
        long newDocumentId;

        // First insert into Documents
        upsertRequest.EdfiDoc["id"] = upsertRequest.DocumentUuid.Value;
        newDocumentId = await _sqlAction.InsertDocument(
            new(
                DocumentPartitionKey: documentPartitionKey,
                DocumentUuid: upsertRequest.DocumentUuid.Value,
                ResourceName: upsertRequest.ResourceInfo.ResourceName.Value,
                ResourceVersion: upsertRequest.ResourceInfo.ResourceVersion.Value,
                IsDescriptor: upsertRequest.ResourceInfo.IsDescriptor,
                ProjectName: upsertRequest.ResourceInfo.ProjectName.Value,
                EdfiDoc: JsonSerializer.Deserialize<JsonElement>(upsertRequest.EdfiDoc)
            ),
            connection,
            transaction,
            traceId
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
            transaction,
            traceId
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
                    transaction,
                    traceId
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

        if (
            documentReferenceIds.ReferentialIds.Length > 0
            || descriptorReferenceIds.ReferentialIds.Length > 0
        )
        {
            Guid[] invalidReferentialIds = await _sqlAction.InsertReferences(
                new(
                    ParentDocumentPartitionKey: documentPartitionKey,
                    ParentDocumentId: newDocumentId,
                    ReferentialIds: documentReferenceIds
                        .ReferentialIds.Concat(descriptorReferenceIds.ReferentialIds)
                        .ToArray(),
                    ReferentialPartitionKeys: documentReferenceIds
                        .ReferentialPartitionKeys.Concat(descriptorReferenceIds.ReferentialPartitionKeys)
                        .ToArray()
                ),
                connection,
                transaction,
                traceId
            );

            if (invalidReferentialIds.Length > 0)
            {
                _logger.LogDebug(
                    "Foreign key violation on Upsert as Insert - {TraceId}",
                    upsertRequest.TraceId
                );
                return ReportReferenceFailure(upsertRequest.DocumentInfo, invalidReferentialIds);
            }
        }

        _logger.LogDebug("Upsert success as insert - {TraceId}", upsertRequest.TraceId);
        return new UpsertResult.InsertSuccess(upsertRequest.DocumentUuid);
    }

    public async Task<UpsertResult> AsUpdate(
        long documentId,
        short documentPartitionKey,
        Guid documentUuid,
        IUpsertRequest upsertRequest,
        DocumentReferenceIds documentReferenceIds,
        DocumentReferenceIds descriptorReferenceIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        // Update the EdfiDoc of the Document
        upsertRequest.EdfiDoc["id"] = documentUuid;
        await _sqlAction.UpdateDocumentEdfiDoc(
            documentPartitionKey,
            documentUuid,
            JsonSerializer.Deserialize<JsonElement>(upsertRequest.EdfiDoc),
            connection,
            transaction,
            traceId
        );

        if (
            documentReferenceIds.ReferentialIds.Length > 0
            || descriptorReferenceIds.ReferentialIds.Length > 0
        )
        {
            // First clear out all the existing references, as they may have changed
            await _sqlAction.DeleteReferencesByDocumentUuid(
                documentPartitionKey,
                documentUuid,
                connection,
                transaction,
                traceId
            );

            Guid[] invalidReferentialIds = await _sqlAction.InsertReferences(
                new(
                    ParentDocumentPartitionKey: documentPartitionKey,
                    ParentDocumentId: documentId,
                    ReferentialIds: documentReferenceIds
                        .ReferentialIds.Concat(descriptorReferenceIds.ReferentialIds)
                        .ToArray(),
                    ReferentialPartitionKeys: documentReferenceIds
                        .ReferentialPartitionKeys.Concat(descriptorReferenceIds.ReferentialPartitionKeys)
                        .ToArray()
                ),
                connection,
                transaction,
                traceId
            );

            if (invalidReferentialIds.Length > 0)
            {
                _logger.LogDebug(
                    "Foreign key violation on Upsert as Update - {TraceId}",
                    upsertRequest.TraceId
                );
                return ReportReferenceFailure(upsertRequest.DocumentInfo, invalidReferentialIds);
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
        NpgsqlTransaction transaction,
        TraceId traceId
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
                documentFromDb = await _sqlAction.FindDocumentByReferentialId(
                    upsertRequest.DocumentInfo.ReferentialId,
                    PartitionKeyFor(upsertRequest.DocumentInfo.ReferentialId),
                    connection,
                    transaction,
                    traceId
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
            catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.DeadlockDetected)
            {
                _logger.LogDebug(
                    pe,
                    "Transaction deadlock on Documents table read - {TraceId}",
                    upsertRequest.TraceId
                );
                return new UpsertResult.UpsertFailureWriteConflict();
            }

            // Either get the existing document uuid or use the new one provided
            if (documentFromDb == null)
            {
                return await AsInsert(
                    upsertRequest,
                    documentReferenceIds,
                    descriptorReferenceIds,
                    connection,
                    transaction,
                    traceId
                );
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
                transaction,
                traceId
            );
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            _logger.LogDebug(pe, "Transaction conflict on Upsert - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UpsertFailureWriteConflict();
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            _logger.LogDebug(pe, "Transaction deadlock on Upsert - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UpsertFailureWriteConflict();
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            _logger.LogInformation(pe, "Failure: identity already exists - {TraceId}", upsertRequest.TraceId);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upsert failure - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }
}
