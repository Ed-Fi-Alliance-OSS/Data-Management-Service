// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
        _logger.LogDebug("Entering GetDocumentById.GetById - {TraceId}", getRequest.TraceId);

        try
        {
            JsonNode? edfiDoc = await _sqlAction.FindDocumentEdfiDocByDocumentUuid(
                getRequest.DocumentUuid,
                getRequest.ResourceInfo.ResourceName.Value,
                PartitionKeyFor(getRequest.DocumentUuid),
                connection,
                transaction,
                LockOption.None,
                getRequest.TraceId
            );

            if (edfiDoc == null)
            {
                return new GetResult.GetFailureNotExists();
            }

            // TODO: Documents table needs a last modified datetime
            return new GetResult.GetSuccess(getRequest.DocumentUuid, edfiDoc, DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDocumentById failure - {TraceId}", getRequest.TraceId);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }
}
