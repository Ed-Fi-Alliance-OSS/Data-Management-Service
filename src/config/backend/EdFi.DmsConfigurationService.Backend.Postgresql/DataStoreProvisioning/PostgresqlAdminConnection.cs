// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The provisioner's administrative connection to the target PostgreSQL server (the configured
/// <c>AdminConnectionString</c>, with secret references already resolved). Every connection it opens is
/// dedicated: pooling is off so a session is never shared, reused, or silently replaced (D-12); the
/// database is chosen per open and the configured value is never used; TCP keepalive is on so a dead peer
/// is noticed; the command timeout is <c>CommandTimeout</c>. The connection string is never exposed or
/// logged. Built over a source, it reads the source at every open, so each operation connects with the
/// resolution it bound and a secret rotated between operations reaches the next one; the parsed
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
    /// <param name="commandTimeout">The <c>CommandTimeout</c>.</param>
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
    /// <see cref="RefuseLineBreaks"/>, <see cref="AlignTrust"/> and <see cref="RefuseWhatPsqlCannotReceive"/>.
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
                RefuseWhatPsqlCannotReceive(template);
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
    /// Makes Npgsql and <c>psql</c> trust the same roots; the trust itself is enforced for Npgsql by
    /// <see cref="IsTrustedServerCertificate"/>. A <c>Root Certificate</c> is made a full path against
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

    /// <summary>
    /// Refuses a value the <c>psql</c> handoff (<see cref="LibpqConnectionSettings.From"/>) cannot carry, so every
    /// open refuses it too and readiness reports it rather than passing while every replay fails. In particular a
    /// missing <c>Username</c>: Npgsql 8 would take the user from <c>PGUSER</c>, the Kerberos principal, or the
    /// operating-system user, none of which the replay's cleared environment carries. The password's ambient
    /// sources are a readiness rule instead (<see cref="PostgresqlEnvironment.AmbientPasswordProbe"/>), because
    /// Npgsql cannot be told to ignore them through the connection string.
    /// <para>
    /// A <c>Host</c> naming more than one server is refused too. Each connection of an operation (the lock holder,
    /// the inspection and work sessions, <c>psql</c>, and outcome resolution) is opened separately, and Npgsql and
    /// <c>libpq</c> each move to the next host of a list whenever one cannot be reached, so the sessions of one
    /// operation could reach different servers: an inspection could find the target absent on one while the lock
    /// and the database are on another.
    /// </para>
    /// </summary>
    private static void RefuseWhatPsqlCannotReceive(NpgsqlConnectionStringBuilder template)
    {
        if (LibpqConnectionSettings.From(template).Endpoints.Count != 1)
        {
            throw new InvalidOperationException(
                "The administrative connection string names more than one host; every session of an operation must reach the same server."
            );
        }
    }

    private static bool IsVerifying(SslMode mode) => mode is SslMode.VerifyCA or SslMode.VerifyFull;

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
        NpgsqlConnectionStringBuilder template = Template;
        NpgsqlConnection connection = new(DriverConnectionString(template, database, applicationName))
        {
            UserCertificateValidationCallback = ServerCertificateValidation(template),
        };
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

    /// <summary>The value <see cref="OpenAsync"/> hands to Npgsql, which it pairs with <see cref="ServerCertificateValidation"/>.</summary>
    internal string ConnectionStringFor(string database, string applicationName) =>
        DriverConnectionString(Template, database, applicationName);

    /// <summary>
    /// A verifying mode reaches Npgsql as <c>Require</c> without the <c>Root Certificate</c>, because Npgsql 8 takes
    /// a validation callback only in that form; it sends the same SSL request and refuses a server without TLS the
    /// same way in all three modes, so only the certificate check, <see cref="ServerCertificateValidation"/>, changes.
    /// </summary>
    private static string DriverConnectionString(
        NpgsqlConnectionStringBuilder template,
        string database,
        string applicationName
    )
    {
        NpgsqlConnectionStringBuilder builder = new(template.ConnectionString)
        {
            Database = database,
            ApplicationName = applicationName,
        };
        if (IsVerifying(template.SslMode))
        {
            builder.SslMode = SslMode.Require;
            builder.RootCertificate = null;
        }

        return builder.ConnectionString;
    }

    /// <summary>The certificate check of a verifying mode, over the full <c>Root Certificate</c> path; null for any other mode.</summary>
    private static RemoteCertificateValidationCallback? ServerCertificateValidation(
        NpgsqlConnectionStringBuilder template
    )
    {
        if (!IsVerifying(template.SslMode))
        {
            return null;
        }

        string rootCertificatePath = template.RootCertificate!;
        bool verifyHostName = template.SslMode == SslMode.VerifyFull;
        return (_, certificate, chain, errors) =>
            IsTrustedServerCertificate(certificate, chain, errors, rootCertificatePath, verifyHostName);
    }

    /// <summary>
    /// The server-certificate check <c>libpq</c> makes with <c>sslrootcert</c> naming the file, so readiness and
    /// every Npgsql session trust exactly what the replay trusts. Npgsql 8's own check accepts a certificate the
    /// machine store trusts before it reads the file, so a server trusted through another root would pass
    /// readiness and fail every replay. Here the chain must end at a certificate of the file, read as PEM as
    /// <c>libpq</c> reads it (a file holding no PEM certificate trusts nothing), without a revocation check, as
    /// neither client is given a revocation list. <c>VerifyFull</c> also requires the host name the TLS layer
    /// checked against the host dialled; <c>VerifyCA</c>, as in <c>libpq</c>, does not.
    /// <para>
    /// The chain must also be made only of what <c>libpq</c> can build it from: the certificates the server sent,
    /// which the TLS layer places in the chain's <c>ExtraStore</c>, and those of the file. .NET completes a chain
    /// from more: the issuer download location a certificate names (Authority Information Access), fetched by the
    /// TLS layer's own build before this check runs and kept in the platform's URL cache, and the user and system
    /// intermediate stores. Downloads are disabled for the rebuild, and a chain holding any other certificate is
    /// refused, so a server that omits an intermediate fails here as it fails in <c>psql</c>.
    /// </para>
    /// </summary>
    internal static bool IsTrustedServerCertificate(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        string rootCertificatePath,
        bool verifyHostName
    )
    {
        if (
            certificate is not X509Certificate2 serverCertificate
            || chain is null
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)
            || (verifyHostName && errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        )
        {
            return false;
        }

        X509Certificate2Collection roots = new();
        try
        {
            roots.ImportFromPemFile(rootCertificatePath);
            if (roots.Count == 0)
            {
                return false;
            }

            X509Certificate2Collection sent = [.. chain.ChainPolicy.ExtraStore];
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Clear();
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.ExtraStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            return chain.Build(serverCertificate) && IsBuiltOnlyFrom(chain, sent, roots);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (X509Certificate2 root in roots)
            {
                root.Dispose();
            }
        }
    }

    /// <summary>Whether every certificate of the built chain above the server's own is one of <paramref name="sent"/> or <paramref name="roots"/>, compared byte for byte.</summary>
    private static bool IsBuiltOnlyFrom(
        X509Chain chain,
        X509Certificate2Collection sent,
        X509Certificate2Collection roots
    )
    {
        for (int index = 1; index < chain.ChainElements.Count; index++)
        {
            ReadOnlyMemory<byte> element = chain.ChainElements[index].Certificate.RawDataMemory;
            if (!IsAmong(element, sent) && !IsAmong(element, roots))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAmong(ReadOnlyMemory<byte> certificate, X509Certificate2Collection candidates) =>
        candidates.Any(candidate => certificate.Span.SequenceEqual(candidate.RawDataMemory.Span));
}
