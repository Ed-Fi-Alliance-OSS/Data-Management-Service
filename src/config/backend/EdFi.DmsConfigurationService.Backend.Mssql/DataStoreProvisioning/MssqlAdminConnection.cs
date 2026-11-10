// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The provisioner's administrative connection to the target SQL Server (4.3 <c>AdminConnectionString</c>,
/// with secret references already resolved). Every connection it opens is dedicated: pooling is off so a
/// session is never shared or reused, and connection resiliency is off (<c>ConnectRetryCount = 0</c>) so a
/// broken session is never silently replaced by a new one under the same object (D-12); MARS is off so a
/// connection carries one request at a time; the database is chosen per open and the configured value is
/// never used; the command timeout is <c>CommandTimeout</c>. SqlClient relies on the operating system's TCP
/// keepalive (D-12). The connection string is never exposed or logged. Built over a source, it reads the
/// source at every open, so each operation connects with the resolution it bound (6.1 step 3) and a secret
/// rotated between operations reaches the next one; the parsed form is reused while the value is unchanged.
/// Every open's wait ends within <c>CommandTimeout</c> on <see cref="TimeProvider"/>, and the driver's own login
/// ends within <c>Connect Timeout</c>, which is never zero or longer than <c>CommandTimeout</c>
/// (<see cref="OpenAsync"/>).
/// </summary>
public sealed class MssqlAdminConnection
{
    /// <summary>The maintenance database every server-level read and the holder session use (D-12).</summary>
    public const string MaintenanceDatabaseName = "master";

    /// <summary>The <c>Application Name</c> of read and work sessions; holder sessions carry their operation id instead.</summary>
    public const string SessionApplicationName = "dmsprov-session";

    private readonly Func<string> _resolvedConnectionString;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private (string Source, SqlConnectionStringBuilder Template)? _parsed;

    public MssqlAdminConnection(
        string resolvedConnectionString,
        TimeSpan commandTimeout,
        TimeProvider? timeProvider = null
    )
        : this(ConstantOf(resolvedConnectionString), commandTimeout, timeProvider)
    {
        // Parsed now, so a value the driver cannot read fails construction.
        _ = Template;
    }

    /// <param name="resolvedConnectionString">Returns the value for the calling operation; read at every open and never logged.</param>
    /// <param name="commandTimeout">The 4.3 <c>CommandTimeout</c>.</param>
    /// <param name="timeProvider">The clock that bounds every open; the system clock when omitted.</param>
    public MssqlAdminConnection(
        Func<string> resolvedConnectionString,
        TimeSpan commandTimeout,
        TimeProvider? timeProvider = null
    )
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
        _timeProvider = timeProvider ?? TimeProvider.System;
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
    private SqlConnectionStringBuilder Template
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

                SqlConnectionStringBuilder template = new(current)
                {
                    Pooling = false,
                    ConnectRetryCount = 0,
                    MultipleActiveResultSets = false,
                    CommandTimeout = DriverTimeoutSeconds(CommandTimeout),
                };

                // Only Connect Timeout ends SqlClient's physical login: cancelling OpenAsync, or abandoning it,
                // ends the caller's wait, while the login keeps its worker thread, its socket and one of
                // SqlClient's process-wide nonpooled-open slots until it ends. Zero means never, so the login is
                // bounded by CommandTimeout as well; a shorter positive value is kept.
                int loginBound = DriverTimeoutSeconds(CommandTimeout);
                if (template.ConnectTimeout == 0 || template.ConnectTimeout > loginBound)
                {
                    template.ConnectTimeout = loginBound;
                }

                _parsed = (current, template);
                return template;
            }
        }
    }

    public TimeSpan CommandTimeout { get; }

    /// <summary>The command timeout in whole seconds, for a <see cref="SqlCommand"/> the engine creates.</summary>
    public int CommandTimeoutSeconds => DriverTimeoutSeconds(CommandTimeout);

    /// <summary>
    /// Opens a dedicated connection to <paramref name="database"/>; nothing is kept open when opening fails. The
    /// wait ends at <c>CommandTimeout</c> on <see cref="TimeProvider"/>, or at the caller's cancellation, even
    /// when the driver ignores both: SqlClient does not honour cancellation while a login stalls
    /// (<see cref="DmsBoundedConnectionOpen"/>). Abandoning the wait does not end the driver's physical login;
    /// its <c>Connect Timeout</c>, at most <c>CommandTimeout</c>, does, and a shorter one ends the open first.
    /// An expired bound is a <see cref="TimeoutException"/>, raised before anything was sent on a connection, and
    /// a connection the abandoned open still produces is disposed unused.
    /// </summary>
    public async Task<SqlConnection> OpenAsync(
        string database,
        string applicationName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        // Read now, so the open connects with the resolution the calling operation bound.
        string connectionString = ConnectionStringFor(database, applicationName);
        return await DmsBoundedConnectionOpen.OpenAsync(
            token => OpenDriverAsync(connectionString, token),
            CommandTimeout,
            _timeProvider,
            cancellationToken
        );
    }

    private static async Task<SqlConnection> OpenDriverAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        SqlConnection connection = new(connectionString);
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

    public Task<SqlConnection> OpenMaintenanceAsync(CancellationToken cancellationToken) =>
        OpenAsync(MaintenanceDatabaseName, SessionApplicationName, cancellationToken);

    /// <summary>A whole-second driver timeout of at least one second; SqlClient reads zero as "wait forever".</summary>
    internal static int DriverTimeoutSeconds(TimeSpan timeout) =>
        (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(timeout.TotalSeconds)));

    internal string ConnectionStringFor(string database, string applicationName) =>
        new SqlConnectionStringBuilder(Template.ConnectionString)
        {
            InitialCatalog = database,
            ApplicationName = applicationName,
        }.ConnectionString;
}
