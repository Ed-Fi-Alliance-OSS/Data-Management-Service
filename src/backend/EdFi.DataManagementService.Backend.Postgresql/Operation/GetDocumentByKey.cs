// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public class GetDocumentByKey(
    NpgsqlDataSource _dataSource,
    ISqlAction _sqlAction,
    ILogger<GetDocumentByKey> _logger) : IQueryHandler
{
    public async Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        _logger.LogDebug("Entering GetDocumentByKey.QueryDocuments - {TraceId}", queryRequest.TraceId);
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            string resourceName = queryRequest.resourceInfo.ResourceName.Value;

            return new QueryResult.QuerySuccess(await _sqlAction.GetDocumentsByKey(
                resourceName, queryRequest.paginationParameters, connection, transaction
            ), 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDocumentByKey.QueryDocuments failure - {TraceId}", queryRequest.TraceId);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}

