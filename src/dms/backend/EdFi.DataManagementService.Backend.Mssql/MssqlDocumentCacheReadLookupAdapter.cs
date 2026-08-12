// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlDocumentCacheReadLookupAdapter : DocumentCacheReadLookupAdapterBase
{
    private readonly Func<string, DbConnection> _createConnection;
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier;
    private readonly IDocumentCacheProviderCommandTimeoutClassifier _providerCommandTimeoutClassifier;
    private readonly ILogger<MssqlDocumentCacheReadLookupAdapter> _logger;

    public MssqlDocumentCacheReadLookupAdapter(
        IRelationalWriteExceptionClassifier writeExceptionClassifier,
        IDocumentCacheProviderCommandTimeoutClassifier providerCommandTimeoutClassifier,
        ILogger<MssqlDocumentCacheReadLookupAdapter> logger,
        IServedEtagComposer servedEtagComposer,
        IDocumentCacheReadResponseShaper responseShaper
    )
        : base(servedEtagComposer, responseShaper)
    {
        _createConnection = connectionString => new SqlConnection(connectionString);
        _writeExceptionClassifier =
            writeExceptionClassifier ?? throw new ArgumentNullException(nameof(writeExceptionClassifier));
        _providerCommandTimeoutClassifier =
            providerCommandTimeoutClassifier
            ?? throw new ArgumentNullException(nameof(providerCommandTimeoutClassifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal MssqlDocumentCacheReadLookupAdapter(
        Func<string, DbConnection> createConnection,
        IRelationalWriteExceptionClassifier writeExceptionClassifier,
        IDocumentCacheProviderCommandTimeoutClassifier providerCommandTimeoutClassifier,
        ILogger<MssqlDocumentCacheReadLookupAdapter> logger,
        IServedEtagComposer servedEtagComposer,
        IDocumentCacheReadResponseShaper responseShaper
    )
        : base(servedEtagComposer, responseShaper)
    {
        _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        _writeExceptionClassifier =
            writeExceptionClassifier ?? throw new ArgumentNullException(nameof(writeExceptionClassifier));
        _providerCommandTimeoutClassifier =
            providerCommandTimeoutClassifier
            ?? throw new ArgumentNullException(nameof(providerCommandTimeoutClassifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override SqlDialect Dialect => SqlDialect.Mssql;

    protected override RelationalProviderToken ProviderToken => RelationalProviderToken.SqlServer;

    protected override async Task<TResult> ExecuteReaderAsync<TResult>(
        DocumentCacheTargetExecutionContext targetContext,
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(readAsync);

        _logger.LogDebug(
            "Executing SQL Server DocumentCache read lookup for target {TargetKey} with {ParameterCount} parameters",
            LogSanitizer.SanitizeForLog(targetContext.TargetKey.ToString()),
            command.Parameters.Count
        );

        await using DbConnection connection = CreateTargetConnection(targetContext.ConnectionInput.Value);
        await OpenTargetConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using DbCommand dbCommand = connection.CreateCommand();
        dbCommand.CommandText = command.CommandText;

        AddParameters(dbCommand, command.Parameters);

        await using var reader = new DbRelationalCommandReader(
            await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)
        );

        return await readAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    protected override bool IsCacheUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return _providerCommandTimeoutClassifier.IsProviderCommandTimeout(exception)
            || exception is DbException dbException
                && _writeExceptionClassifier.IsTransientFailure(dbException);
    }

    private DbConnection CreateTargetConnection(string connectionString)
    {
        try
        {
            return _createConnection(connectionString);
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedConnectionAcquisitionFailure(exception))
        {
            throw new DocumentCacheReadAcquisitionUnavailableException(
                "SQL Server DocumentCache read lookup connection construction failed.",
                exception
            );
        }
    }

    private static async Task OpenTargetConnectionAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedConnectionAcquisitionFailure(exception))
        {
            throw new DocumentCacheReadAcquisitionUnavailableException(
                "SQL Server DocumentCache read lookup connection open failed.",
                exception
            );
        }
    }

    private static bool IsExpectedConnectionAcquisitionFailure(Exception exception) =>
        exception
            is DbException
                or TimeoutException
                or FormatException
                or ArgumentException
                and not ArgumentNullException;

    private static void AddParameters(DbCommand dbCommand, IReadOnlyList<RelationalParameter> parameters)
    {
        foreach (RelationalParameter parameter in parameters)
        {
            DbParameter dbParameter = dbCommand.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            parameter.ConfigureParameter?.Invoke(dbParameter);
            dbCommand.Parameters.Add(dbParameter);
        }
    }
}
