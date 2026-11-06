// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The provisioner's administrative connection to the target PostgreSQL server (4.3
/// <c>AdminConnectionString</c>, with secret references already resolved). Every connection it opens is
/// dedicated: pooling is off so a session is never shared, reused, or silently replaced (D-12); the
/// database is chosen per open and the configured value is never used; TCP keepalive is on so a dead peer
/// is noticed; the command timeout is <c>CommandTimeout</c>. The connection string is never exposed or
/// logged.
/// </summary>
public sealed class PostgresqlAdminConnection
{
    /// <summary>The maintenance database every server-level read and the holder session use (D-12).</summary>
    public const string MaintenanceDatabaseName = "postgres";

    /// <summary>The <c>application_name</c> of read and work sessions; holder and <c>psql</c> sessions carry their operation id instead.</summary>
    public const string SessionApplicationName = "dmsprov-session";

    private const int TcpKeepAliveTimeSeconds = 15;
    private const int TcpKeepAliveIntervalSeconds = 5;

    private readonly NpgsqlConnectionStringBuilder _template;

    public PostgresqlAdminConnection(string resolvedConnectionString, TimeSpan commandTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedConnectionString);
        if (commandTimeout <= TimeSpan.Zero || commandTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                "The command timeout must be positive and at most one hour."
            );
        }

        CommandTimeout = commandTimeout;
        _template = new NpgsqlConnectionStringBuilder(resolvedConnectionString)
        {
            Pooling = false,
            Multiplexing = false,
            CommandTimeout = (int)Math.Ceiling(commandTimeout.TotalSeconds),
            TcpKeepAlive = true,
            TcpKeepAliveTime = TcpKeepAliveTimeSeconds,
            TcpKeepAliveInterval = TcpKeepAliveIntervalSeconds,
        };
    }

    public TimeSpan CommandTimeout { get; }

    /// <summary>Opens a dedicated connection to <paramref name="database"/>; nothing is kept open when opening fails.</summary>
    public async Task<NpgsqlConnection> OpenAsync(
        string database,
        string applicationName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        NpgsqlConnection connection = new(ConnectionStringFor(database, applicationName));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task<NpgsqlConnection> OpenMaintenanceAsync(CancellationToken cancellationToken) =>
        OpenAsync(MaintenanceDatabaseName, SessionApplicationName, cancellationToken);

    /// <summary>The connection as <c>libpq</c> receives it (D-10), for the replay runner only; never logged.</summary>
    internal LibpqConnectionSettings ToLibpqSettings() => LibpqConnectionSettings.From(_template);

    internal string ConnectionStringFor(string database, string applicationName) =>
        new NpgsqlConnectionStringBuilder(_template.ConnectionString)
        {
            Database = database,
            ApplicationName = applicationName,
        }.ConnectionString;
}
