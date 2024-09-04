// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IQueryDocument
{
    public Task<QueryResult> QueryDocuments(
        IQueryRequest queryRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );
}

public class QueryDocument(ISqlAction _sqlAction, ILogger<QueryDocument> _logger) : IQueryDocument
{
    public async Task<QueryResult> QueryDocuments(
        IQueryRequest queryRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        _logger.LogDebug("Entering QueryDocument.QueryDocuments - {TraceId}", queryRequest.TraceId);
        try
        {
            string resourceName = queryRequest.ResourceInfo.ResourceName.Value;

            return new QueryResult.QuerySuccess(
                await _sqlAction.GetAllDocumentsByResourceName(
                    resourceName,
                    queryRequest.PaginationParameters,
                    connection,
                    transaction,
                    queryRequest.TraceId
                ),
                queryRequest.PaginationParameters.TotalCount
                    ? await _sqlAction.GetTotalDocumentsForResourceName(
                        resourceName,
                        connection,
                        transaction,
                        queryRequest.TraceId
                    )
                    : null
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryDocument.QueryDocuments failure - {TraceId}", queryRequest.TraceId);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
