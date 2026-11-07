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
/// logged. Built over a source, it reads the current resolved value at every open (6.1 step 3), so a secret
/// rotated between operations reaches the next connection; the parsed form is reused while the value is
/// unchanged.
/// </summary>
public sealed class PostgresqlAdminConnection
{
    /// <summary>The maintenance database every server-level read and the holder session use (D-12).</summary>
    public const string MaintenanceDatabaseName = "postgres";

    /// <summary>The <c>application_name</c> of read and work sessions; holder and <c>psql</c> sessions carry their operation id instead.</summary>
    public const string SessionApplicationName = "dmsprov-session";

    private const int TcpKeepAliveTimeSeconds = 15;
    private const int TcpKeepAliveIntervalSeconds = 5;

    private readonly Func<string> _resolvedConnectionString;
    private readonly Lock _gate = new();
    private (string Source, NpgsqlConnectionStringBuilder Template)? _parsed;

    public PostgresqlAdminConnection(string resolvedConnectionString, TimeSpan commandTimeout)
        : this(ConstantOf(resolvedConnectionString), commandTimeout)
    {
        // Parsed now, so a value the driver cannot read fails construction as it always has.
        _ = Template;
    }

    /// <param name="resolvedConnectionString">Returns the current resolved value; read at every open and never logged.</param>
    /// <param name="commandTimeout">The 4.3 <c>CommandTimeout</c>.</param>
    public PostgresqlAdminConnection(Func<string> resolvedConnectionString, TimeSpan commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(resolvedConnectionString);
        if (commandTimeout <= TimeSpan.Zero || commandTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                "The command timeout must be positive and at most one hour."
            );
        }

        CommandTimeout = commandTimeout;
        _resolvedConnectionString = resolvedConnectionString;
    }

    private static Func<string> ConstantOf(string resolvedConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedConnectionString);
        return () => resolvedConnectionString;
    }

    /// <summary>
    /// The current value parsed by the driver with the provisioning overrides applied. A value the driver
    /// cannot read throws the driver's exception, whose message is never logged.
    /// </summary>
    private NpgsqlConnectionStringBuilder Template
    {
        get
        {
            string current = _resolvedConnectionString();
            ArgumentException.ThrowIfNullOrWhiteSpace(current);
            lock (_gate)
            {
                if (_parsed is { } parsed && string.Equals(parsed.Source, current, StringComparison.Ordinal))
                {
                    return parsed.Template;
                }

                NpgsqlConnectionStringBuilder template = new(current)
                {
                    Pooling = false,
                    Multiplexing = false,
                    CommandTimeout = (int)Math.Ceiling(CommandTimeout.TotalSeconds),
                    TcpKeepAlive = true,
                    TcpKeepAliveTime = TcpKeepAliveTimeSeconds,
                    TcpKeepAliveInterval = TcpKeepAliveIntervalSeconds,
                };
                _parsed = (current, template);
                return template;
            }
        }
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

    /// <summary>
    /// The driver's per-command timeout for a statement the server itself bounds (a lock wait, a termination
    /// wait): the server bound plus the command timeout, in whole seconds, so the driver stays a backstop for
    /// an unresponsive server rather than the first bound to fire.
    /// </summary>
    internal static int DriverTimeoutSeconds(TimeSpan timeout) =>
        (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(timeout.TotalSeconds)));

    /// <summary>The connection as <c>libpq</c> receives it (D-10), for the replay runner only; never logged.</summary>
    internal LibpqConnectionSettings ToLibpqSettings() => LibpqConnectionSettings.From(Template);

    internal string ConnectionStringFor(string database, string applicationName) =>
        new NpgsqlConnectionStringBuilder(Template.ConnectionString)
        {
            Database = database,
            ApplicationName = applicationName,
        }.ConnectionString;
}
