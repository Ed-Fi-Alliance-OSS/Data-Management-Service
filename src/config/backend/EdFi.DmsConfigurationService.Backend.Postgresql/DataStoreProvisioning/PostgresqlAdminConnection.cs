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
/// logged. Built over a source, it reads the source at every open, so each operation connects with the
/// resolution it bound (6.1 step 3) and a secret rotated between operations reaches the next one; the parsed
/// form is reused while the value is unchanged.
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

    /// <param name="resolvedConnectionString">Returns the value for the calling operation; read at every open and never logged.</param>
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
    /// cannot read throws the driver's exception, whose message is never logged. Every open, the
    /// <c>psql</c> handoff, and readiness pass through here, so a value the handoff cannot carry is refused
    /// before anything connects, whether it was configured or arrived by a rotated secret (D-10): see
    /// <see cref="RefuseLineBreaks"/> and <see cref="AlignTrust"/>.
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
                RefuseLineBreaks(template);
                AlignTrust(template);
                _parsed = (current, template);
                return template;
            }
        }
    }

    /// <summary>
    /// The <c>libpq</c> password file has no way to represent a line break, so a host, user, or password
    /// carrying one would let Npgsql connect while every replay failed. The refusal names no value.
    /// </summary>
    private static void RefuseLineBreaks(NpgsqlConnectionStringBuilder template)
    {
        if (
            Array.Exists(
                [template.Host, template.Username, template.Password],
                value => value is not null && value.AsSpan().ContainsAny('\r', '\n')
            )
        )
        {
            throw new InvalidOperationException(
                "The administrative connection string has a line break in its Host, Username, or Password, which the psql password file cannot represent."
            );
        }
    }

    /// <summary>
    /// Makes Npgsql and <c>psql</c> trust the same roots. A <c>Root Certificate</c> is made a full path against
    /// this process's working directory, where Npgsql resolves it, because <c>psql</c> runs in the operation's
    /// directory and would resolve a relative path there. A verifying mode without one is refused: Npgsql
    /// would then fall back to <c>PGSSLROOTCERT</c>, a default file, or the machine store, none of which the
    /// replay's environment carries the same way, so readiness could pass while every replay fails. A
    /// <c>Root Certificate</c> with a mode that does not verify is refused too: Npgsql then validates nothing,
    /// while <c>libpq</c> verifies the server against any root certificate file it is given (with
    /// <c>require</c>, as <c>verify-ca</c> does), so an unrelated server certificate would pass readiness and
    /// fail every replay. The operator chooses <c>VerifyCA</c> or <c>VerifyFull</c>, or removes the file.
    /// </summary>
    private static void AlignTrust(NpgsqlConnectionStringBuilder template)
    {
        bool verifying = template.SslMode is SslMode.VerifyCA or SslMode.VerifyFull;
        if (string.IsNullOrEmpty(template.RootCertificate))
        {
            if (verifying)
            {
                throw new InvalidOperationException(
                    "The administrative connection string verifies the server certificate without a Root Certificate."
                );
            }

            return;
        }

        if (!verifying)
        {
            throw new InvalidOperationException(
                "The administrative connection string names a Root Certificate with an SSL Mode that does not verify the server certificate; use VerifyCA or VerifyFull."
            );
        }

        template.RootCertificate = Path.GetFullPath(template.RootCertificate);
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
