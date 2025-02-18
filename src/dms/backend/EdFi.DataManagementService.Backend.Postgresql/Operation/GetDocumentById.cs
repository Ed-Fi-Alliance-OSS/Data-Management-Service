// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Postgresql.Model;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;
using static EdFi.DataManagementService.Backend.PartitionUtility;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IGetDocumentById
{
    public Task<GetResult> GetById(
        IGetRequest getRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );
}

public class GetDocumentById(ISqlAction _sqlAction, ILogger<GetDocumentById> _logger) : IGetDocumentById
{
    /// <summary>
    /// Takes a GetRequest and connection + transaction and returns the result of a get by id query.
    ///
    /// Connections and transactions are always managed by the caller based on the result.
    /// </summary>
    public async Task<GetResult> GetById(
        IGetRequest getRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        _logger.LogDebug("Entering GetDocumentById.GetById - {TraceId}", getRequest.TraceId.Value);

        try
        {
            DocumentSummary? documentSummary = await _sqlAction.FindDocumentEdfiDocByDocumentUuid(
                getRequest.DocumentUuid,
                getRequest.ResourceInfo.ResourceName.Value,
                PartitionKeyFor(getRequest.DocumentUuid),
                connection,
                transaction,
                getRequest.TraceId
            );

            if (documentSummary == null)
            {
                return new GetResult.GetFailureNotExists();
            }

            JsonNode securityElements = documentSummary.SecurityElements.Deserialize<JsonNode>()!;

            var getAuthorizationResult = getRequest.ResourceAuthorizationHandler.Authorize(securityElements);

            if (getAuthorizationResult is ResourceAuthorizationResult.NotAuthorized notAuthorized)
            {
                return new GetResult.GetFailureNotAuthorized(notAuthorized.ErrorMessages);
            }

            return new GetResult.GetSuccess(
                getRequest.DocumentUuid,
                documentSummary.EdfiDoc.Deserialize<JsonNode>()!,
                documentSummary.LastModifiedAt,
                documentSummary.LastModifiedTraceId
            );
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            _logger.LogDebug(pe, "Transaction deadlock on query - {TraceId}", getRequest.TraceId.Value);
            return new GetResult.GetFailureRetryable();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDocumentById failure - {TraceId}", getRequest.TraceId.Value);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }
}
