// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IQueryDocument
{
    public Task<QueryResult> QueryDocuments(IQueryRequest queryRequest);
}

public class QueryDocument(
    ISqlAction _sqlAction,
    ILogger<QueryDocument> _logger,
    NpgsqlDataSource _dataSource,
    IOptions<DatabaseOptions> _databaseOptions
) : IQueryDocument
{
    public async Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        _logger.LogDebug("Entering QueryDocument.QueryDocuments - {TraceId}", queryRequest.TraceId.Value);
        try
        {
            string resourceName = queryRequest.ResourceInfo.ResourceName.Value;

            int? totalCount = null;

            if (queryRequest.PaginationParameters.TotalCount)
            {
                await using var countConnection = await _dataSource.OpenConnectionAsync();
                await using var countTransaction = await countConnection.BeginTransactionAsync(
                    _databaseOptions.Value.IsolationLevel
                );

                try
                {
                    totalCount = await _sqlAction.GetTotalDocumentsForResourceName(
                        resourceName,
                        queryRequest,
                        countConnection,
                        countTransaction,
                        queryRequest.TraceId
                    );
                    await countTransaction.CommitAsync();
                }
                catch
                {
                    await countTransaction.RollbackAsync();
                    throw;
                }
            }

            return new QueryResult.QuerySuccess(
                async (stream, cancellationToken) =>
                {
                    await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                    await using var transaction = await connection.BeginTransactionAsync(
                        _databaseOptions.Value.IsolationLevel,
                        cancellationToken
                    );

                    try
                    {
                        JsonWriterOptions writerOptions = new()
                        {
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        };

                        await using var writer = new Utf8JsonWriter(stream, writerOptions);
                        writer.WriteStartArray();
                        await _sqlAction.WriteAllDocumentsByResourceNameAsync(
                            resourceName,
                            queryRequest,
                            connection,
                            transaction,
                            queryRequest.TraceId,
                            writer,
                            cancellationToken
                        );
                        writer.WriteEndArray();
                        await writer.FlushAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                    }
                    catch
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                },
                totalCount
            );
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            _logger.LogDebug(pe, "Transaction deadlock on query - {TraceId}", queryRequest.TraceId.Value);
            return new QueryResult.QueryFailureRetryable();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "QueryDocument.QueryDocuments failure - {TraceId}",
                queryRequest.TraceId.Value
            );
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
