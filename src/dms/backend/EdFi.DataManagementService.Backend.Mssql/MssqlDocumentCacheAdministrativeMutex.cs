// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlDocumentCacheAdministrativeMutex : IDocumentCacheAdministrativeMutex
{
    internal const string LockResource = "EdFi.DMS.DocumentProjection.Administration.v1";

    internal const string AcquireSql = """
        DECLARE @result int;

        EXEC @result = sys.sp_getapplock
            @Resource = @resource,
            @LockMode = N'Exclusive',
            @LockOwner = N'Session',
            @LockTimeout = -1,
            @DbPrincipal = N'public';

        SELECT @result;
        """;

    internal const string ReleaseSql = """
        DECLARE @result int;

        EXEC @result = sys.sp_releaseapplock
            @Resource = @resource,
            @LockOwner = N'Session',
            @DbPrincipal = N'public';

        SELECT @result;
        """;

    private readonly Func<string, DbConnection> _createConnection;
    private readonly ILogger<MssqlDocumentCacheAdministrativeMutex> _logger;

    public MssqlDocumentCacheAdministrativeMutex(ILogger<MssqlDocumentCacheAdministrativeMutex> logger)
        : this(connectionString => new SqlConnection(connectionString), logger) { }

    internal MssqlDocumentCacheAdministrativeMutex(
        Func<string, DbConnection> createConnection,
        ILogger<MssqlDocumentCacheAdministrativeMutex> logger
    )
    {
        _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RelationalProviderToken ProviderToken => RelationalProviderToken.SqlServer;

    public async Task<IDocumentCacheAdministrativeMutexLease> AcquireAsync(
        DocumentCacheTargetConnectionInput connectionInput,
        CancellationToken cancellationToken = default
    )
    {
        string connectionString = DocumentCacheAdministrativeMutexGuards.RequireConnectionString(
            connectionInput,
            ProviderToken
        );

        DbConnection connection = _createConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAcquireAsync(connection, cancellationToken).ConfigureAwait(false);
            int sessionId = await ExecuteSessionIdAsync(connection, cancellationToken).ConfigureAwait(false);
            return new MssqlDocumentCacheAdministrativeMutexLease(connection, _logger, sessionId);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAcquireAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = CreateAppLockCommand(connection, AcquireSql);
        int result = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
        );

        if (result < 0)
        {
            throw new InvalidOperationException(
                $"SQL Server DocumentCache administrative mutex acquisition failed with sp_getapplock result {result}."
            );
        }
    }

    private static async Task<int> ExecuteSessionIdAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT @@SPID;";

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static DbCommand CreateAppLockCommand(DbConnection connection, string commandText)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;

        DbParameter resourceParameter = command.CreateParameter();
        resourceParameter.ParameterName = "@resource";
        resourceParameter.Value = LockResource;
        if (resourceParameter is SqlParameter sqlParameter)
        {
            sqlParameter.SqlDbType = SqlDbType.NVarChar;
            sqlParameter.Size = 255;
        }

        command.Parameters.Add(resourceParameter);
        return command;
    }

    private sealed class MssqlDocumentCacheAdministrativeMutexLease(
        DbConnection connection,
        ILogger logger,
        int sessionId
    ) : DocumentCacheAdministrativeMutexLease(RelationalProviderToken.SqlServer, connection)
    {
        private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        private readonly int _sessionId = sessionId;

        protected override async Task ValidateSessionAsync(CancellationToken cancellationToken)
        {
            int currentSessionId = await ExecuteSessionIdAsync(Connection, cancellationToken)
                .ConfigureAwait(false);

            if (currentSessionId != _sessionId)
            {
                throw new DocumentCacheAdministrativeMutexSessionLostException(ProviderToken);
            }
        }

        protected override async Task ReleaseAsync(
            DbConnection connection,
            CancellationToken cancellationToken
        )
        {
            await using DbCommand command = CreateAppLockCommand(connection, ReleaseSql);
            int result = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            );

            if (result >= 0)
            {
                return;
            }

            _logger.LogWarning(
                "SQL Server DocumentCache administrative mutex release failed with sp_releaseapplock result {Result}.",
                result
            );

            throw new InvalidOperationException(
                $"SQL Server DocumentCache administrative mutex release failed with sp_releaseapplock result {result}."
            );
        }
    }
}
