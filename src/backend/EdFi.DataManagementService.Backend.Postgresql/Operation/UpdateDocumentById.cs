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

public interface IUpdateDocumentById
{
    public Task<UpdateResult> UpdateById(
        IUpdateRequest updateRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );
}

public class UpdateDocumentById(ISqlAction _sqlAction, ILogger<UpdateDocumentById> _logger)
    : IUpdateDocumentById
{

    /// <summary>
    /// Returns the ReferentialId Guids and corresponding partition keys for all of the document
    /// references in the UpdateRequest.
    /// </summary>
    public static DocumentReferenceIds DocumentReferenceIdsFrom(IUpdateRequest updateRequest)
    {
        DocumentReference[] documentReferences = updateRequest.DocumentInfo.DocumentReferences;
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

    /// <summary>
    /// Takes an UpdateRequest and connection + transaction and returns the result of an update operation.
    ///
    /// Connections and transactions are always managed by the caller based on the result.
    /// </summary>
    public async Task<UpdateResult> UpdateById(
        IUpdateRequest updateRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        _logger.LogDebug("Entering UpdateDocumentById.UpdateById - {TraceId}", updateRequest.TraceId);
        var documentPartitionKey = PartitionKeyFor(updateRequest.DocumentUuid);

        try
        {

            DocumentReferenceIds documentReferenceIds = DocumentReferenceIdsFrom(updateRequest);

            if (documentReferenceIds != null)
            {
                Guid[] invalidReferentialIds = await _sqlAction.FindInvalidReferentialIds(
                    documentReferenceIds,
                    connection,
                    transaction
                );

                ResourceName[] invalidResourceNames = ResourceNamesFrom(
                    updateRequest.DocumentInfo.DocumentReferences,
                    invalidReferentialIds
                );

                return new UpdateResult.UpdateFailureReference(invalidResourceNames);
            }

            var validationResult = await _sqlAction.UpdateDocumentValidation(
                updateRequest.DocumentUuid,
                documentPartitionKey,
                updateRequest.DocumentInfo.ReferentialId,
                PartitionKeyFor(updateRequest.DocumentInfo.ReferentialId),
                connection,
                transaction,
                LockOption.BlockAll
            );

            if (!validationResult.DocumentExists)
            {
                // Document does not exist
                return new UpdateResult.UpdateFailureNotExists();
            }

            if (!validationResult.ReferentialIdExists)
            {
                // Extracted referential id does not match stored. Must be attempting to change natural key.
                _logger.LogInformation(
                    "Failure: Natural key does not match on update - {TraceId}",
                    updateRequest.TraceId
                );
                return new UpdateResult.UpdateFailureImmutableIdentity(
                    $"Identifying values for the {updateRequest.ResourceInfo.ResourceName.Value} resource cannot be changed. Delete and recreate the resource item instead."
                );
            }

            int rowsAffected = await _sqlAction.UpdateDocumentEdfiDoc(
                PartitionKeyFor(updateRequest.DocumentUuid).Value,
                updateRequest.DocumentUuid.Value,
                JsonSerializer.Deserialize<JsonElement>(updateRequest.EdfiDoc),
                connection,
                transaction
            );

            switch (rowsAffected)
            {
                case 1:
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
