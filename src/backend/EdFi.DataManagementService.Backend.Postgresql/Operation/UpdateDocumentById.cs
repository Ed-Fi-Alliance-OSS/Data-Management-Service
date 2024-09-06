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

public interface IUpdateDocumentById
{
    public Task<UpdateResult> UpdateById(
        IUpdateRequest updateRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    );
}

public class UpdateDocumentById(ISqlAction _sqlAction, ILogger<UpdateDocumentById> _logger)
    : IUpdateDocumentById
{
    /// <summary>
    /// Determine whether invalid referentialIds were descriptors or references, and returns the
    /// appropriate failure.
    /// </summary>
    private static UpdateResult ReportReferenceFailure(
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
            return new UpdateResult.UpdateFailureDescriptorReference(invalidDescriptorReferences);
        }

        ResourceName[] invalidResourceNames = ResourceNamesFrom(
            documentInfo.DocumentReferences,
            invalidReferentialIds
        );

        return new UpdateResult.UpdateFailureReference(invalidResourceNames);
    }

    /// <summary>
    /// Takes an UpdateRequest and connection + transaction and returns the result of an update operation.
    ///
    /// Connections and transactions are always managed by the caller based on the result.
    /// </summary>
    public async Task<UpdateResult> UpdateById(
        IUpdateRequest updateRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        _logger.LogDebug("Entering UpdateDocumentById.UpdateById - {TraceId}", updateRequest.TraceId);
        var documentPartitionKey = PartitionKeyFor(updateRequest.DocumentUuid);

        DocumentReferenceIds documentReferenceIds = DocumentReferenceIdsFrom(
            updateRequest.DocumentInfo.DocumentReferences
        );

        DocumentReferenceIds descriptorReferenceIds = DescriptorReferenceIdsFrom(
            updateRequest.DocumentInfo.DescriptorReferences
        );

        try
        {
            var validationResult = await _sqlAction.UpdateDocumentValidation(
                updateRequest.DocumentUuid,
                documentPartitionKey,
                updateRequest.DocumentInfo.ReferentialId,
                PartitionKeyFor(updateRequest.DocumentInfo.ReferentialId),
                connection,
                transaction,
                LockOption.BlockAll,
                traceId
            );

            if (!validationResult.DocumentExists)
            {
                // Document does not exist
                return new UpdateResult.UpdateFailureNotExists();
            }

            if (!validationResult.ReferentialIdUnchanged)
            {
                // Extracted referential id does not match stored. Must be attempting to change identity.
                if (updateRequest.ResourceInfo.AllowIdentityUpdates)
                {
                    // Identity update is allowed
                    _logger.LogInformation("Updating Identity - {TraceId}", updateRequest.TraceId);

                    int aliasRowsAffected = await _sqlAction.UpdateAliasReferentialIdByDocumentUuid(
                        PartitionKeyFor(updateRequest.DocumentInfo.ReferentialId).Value,
                        updateRequest.DocumentInfo.ReferentialId.Value,
                        PartitionKeyFor(updateRequest.DocumentUuid).Value,
                        updateRequest.DocumentUuid.Value,
                        connection,
                        transaction,
                        traceId
                    );

                    if (aliasRowsAffected == 0)
                    {
                        _logger.LogInformation(
                            "Failure: Alias record to update does not exist - {TraceId}",
                            updateRequest.TraceId
                        );
                        return new UpdateResult.UpdateFailureNotExists();
                    }
                }
                else
                {
                    // Identity update not allowed
                    _logger.LogInformation(
                        "Failure: Identity does not match on update - {TraceId}",
                        updateRequest.TraceId
                    );
                    return new UpdateResult.UpdateFailureImmutableIdentity(
                        $"Identifying values for the {updateRequest.ResourceInfo.ResourceName.Value} resource cannot be changed. Delete and recreate the resource item instead."
                    );
                }
            }

            int rowsAffected = await _sqlAction.UpdateDocumentEdfiDoc(
                PartitionKeyFor(updateRequest.DocumentUuid).Value,
                updateRequest.DocumentUuid.Value,
                JsonSerializer.Deserialize<JsonElement>(updateRequest.EdfiDoc),
                connection,
                transaction,
                traceId
            );

            switch (rowsAffected)
            {
                case 1:
                    if (
                        documentReferenceIds.ReferentialIds.Length > 0
                        || descriptorReferenceIds.ReferentialIds.Length > 0
                    )
                    {
                        Document? documentFromDb;
                        try
                        {
                            // Attempt to get the document, to get the ID for references
                            documentFromDb = await _sqlAction.FindDocumentByReferentialId(
                                updateRequest.DocumentInfo.ReferentialId,
                                PartitionKeyFor(updateRequest.DocumentInfo.ReferentialId),
                                connection,
                                transaction,
                                LockOption.BlockUpdateDelete,
                                traceId
                            );
                        }
                        catch (PostgresException pe)
                            when (pe.SqlState == PostgresErrorCodes.SerializationFailure)
                        {
                            _logger.LogDebug(
                                pe,
                                "Transaction conflict on Documents table read - {TraceId}",
                                updateRequest.TraceId
                            );
                            return new UpdateResult.UpdateFailureWriteConflict();
                        }

                        long documentId = 0;
                        if (documentFromDb != null)
                        {
                            documentId =
                                documentFromDb.Id
                                ?? throw new InvalidOperationException(
                                    "documentFromDb.Id should never be null"
                                );
                        }

                        await _sqlAction.DeleteReferencesByDocumentUuid(
                            documentPartitionKey.Value,
                            updateRequest.DocumentUuid.Value,
                            connection,
                            transaction,
                            traceId
                        );

                        Guid[] invalidReferentialIds = await _sqlAction.InsertReferences(
                            new(
                                ParentDocumentPartitionKey: documentPartitionKey.Value,
                                ParentDocumentId: documentId,
                                ReferentialIds: documentReferenceIds
                                    .ReferentialIds.Concat(descriptorReferenceIds.ReferentialIds)
                                    .ToArray(),
                                ReferentialPartitionKeys: documentReferenceIds
                                    .ReferentialPartitionKeys.Concat(
                                        descriptorReferenceIds.ReferentialPartitionKeys
                                    )
                                    .ToArray()
                            ),
                            connection,
                            transaction,
                            traceId
                        );

                        if (invalidReferentialIds.Length > 0)
                        {
                            _logger.LogDebug(
                                "Foreign key violation on Update - {TraceId}",
                                updateRequest.TraceId
                            );
                            return ReportReferenceFailure(updateRequest.DocumentInfo, invalidReferentialIds);
                        }
                    }

                    return new UpdateResult.UpdateSuccess(updateRequest.DocumentUuid);

                case 0:
                    _logger.LogInformation(
                        "Failure: Record to update does not exist - {TraceId}",
                        updateRequest.TraceId
                    );
                    return new UpdateResult.UpdateFailureNotExists();
                default:
                    _logger.LogCritical(
                        "UpdateDocumentById rows affected was '{RowsAffected}' for {DocumentUuid} - Should never happen - {TraceId}",
                        rowsAffected,
                        updateRequest.DocumentUuid,
                        updateRequest.TraceId
                    );
                    return new UpdateResult.UnknownFailure("Unknown Failure");
            }
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            _logger.LogDebug(pe, "Transaction conflict on UpdateById - {TraceId}", updateRequest.TraceId);
            return new UpdateResult.UpdateFailureWriteConflict();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure on Documents table update - {TraceId}", updateRequest.TraceId);
            return new UpdateResult.UnknownFailure("Update failure");
        }
    }
}
