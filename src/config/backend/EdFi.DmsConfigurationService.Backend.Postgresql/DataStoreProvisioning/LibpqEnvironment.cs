// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>One server endpoint as <c>libpq</c> names it: a bare host (no IPv6 brackets) and its port.</summary>
internal sealed record LibpqEndpoint(string Host, int Port);

/// <summary>
/// The administrative connection as <c>libpq</c> receives it (D-10). The validator admits only the keys
/// this record carries, so nothing the connection string says is silently lost on the way to <c>psql</c>:
/// the endpoints, user, and password go to the password file and the command line; the SSL mode, root
/// certificate, and connect timeout go to the environment. Npgsql's <c>Host</c> syntax (a comma list whose
/// entries may carry their own <c>:port</c>) is normalized into the separate host and port lists
/// <c>libpq</c> requires, with the same per-entry rule Npgsql applies, so <c>psql</c> dials exactly the
/// endpoints the administrative connection dials. The record is never logged or formatted.
/// </summary>
/// <param name="SslMode">The <c>libpq</c> spelling of the SSL mode (<c>disable</c> … <c>verify-full</c>).</param>
/// <param name="RootCertificate">The <c>Root Certificate</c> path, or null when none is configured.</param>
/// <param name="ConnectTimeoutSeconds">The <c>Timeout</c> value, in seconds.</param>
internal sealed record LibpqConnectionSettings(
    IReadOnlyList<LibpqEndpoint> Endpoints,
    string Username,
    string Password,
    string SslMode,
    string? RootCertificate,
    int ConnectTimeoutSeconds
)
{
    private const int MaximumPort = 65535;

    /// <summary>The <c>--host</c> value: the hosts in order, comma-separated.</summary>
    public string HostArgument => string.Join(',', Endpoints.Select(endpoint => endpoint.Host));

    /// <summary>The <c>--port</c> value: one port per host in the same order, comma-separated.</summary>
    public string PortArgument =>
        string.Join(',', Endpoints.Select(endpoint => endpoint.Port.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Reads the admitted keys from a parsed connection string; a missing host or user cannot be handed to <c>psql</c>.</summary>
    public static LibpqConnectionSettings From(NpgsqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            throw new InvalidOperationException("The administrative connection string names no host.");
        }

        if (string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException("The administrative connection string names no user.");
        }

        return new LibpqConnectionSettings(
            ParseEndpoints(builder.Host, builder.Port),
            builder.Username,
            builder.Password ?? string.Empty,
            SslModeName(builder.SslMode),
            string.IsNullOrEmpty(builder.RootCertificate) ? null : builder.RootCertificate,
            builder.Timeout
        );
    }

    /// <summary>
    /// Splits a <c>Host</c> value as Npgsql does: on commas, each entry trimmed; an entry's trailing
    /// <c>:port</c> is taken when that colon is the entry's only colon, or when it follows the closing
    /// bracket of an IPv6 address, so a bare IPv6 address is never split; a lone entry that names a Unix
    /// socket directory (rooted, or <c>@</c>-abstract) is never split either. IPv6 brackets are removed,
    /// because <c>libpq</c>'s keyword form takes the bare address. An empty entry or a port that is not a
    /// number in 1–65535 is refused, where Npgsql would fail at connect time.
    /// </summary>
    public static IReadOnlyList<LibpqEndpoint> ParseEndpoints(string hostValue, int defaultPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostValue);
        string[] entries = hostValue.Split(',');
        List<LibpqEndpoint> endpoints = new(entries.Length);
        foreach (string entry in entries)
        {
            string trimmed = entry.Trim();
            if (trimmed.Length == 0)
            {
                throw new InvalidOperationException(
                    "The administrative connection string names an empty host entry."
                );
            }

            string host = trimmed;
            int port = defaultPort;
            if (
                !(entries.Length == 1 && IsUnixSocket(trimmed))
                && TrySplitHostPort(trimmed, out string splitHost, out string portText)
            )
            {
                if (
                    !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                    || port is < 1 or > MaximumPort
                )
                {
                    throw new InvalidOperationException(
                        "The administrative connection string names a host entry whose port is not a valid port number."
                    );
                }

                host = splitHost;
            }

            endpoints.Add(new LibpqEndpoint(StripBrackets(host), port));
        }

        return endpoints;
    }

    /// <summary>The <c>sslmode</c> spelling of an Npgsql mode; the two libraries name the same six modes.</summary>
    public static string SslModeName(SslMode mode) =>
        mode switch
        {
            Npgsql.SslMode.Disable => "disable",
            Npgsql.SslMode.Allow => "allow",
            Npgsql.SslMode.Prefer => "prefer",
            Npgsql.SslMode.Require => "require",
            Npgsql.SslMode.VerifyCA => "verify-ca",
            Npgsql.SslMode.VerifyFull => "verify-full",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), "The SSL mode has no libpq spelling."),
        };

    /// <summary>Records print their members; this one carries a password, so it prints only its type name.</summary>
    public override string ToString() => nameof(LibpqConnectionSettings);

    // Npgsql's TrySplitHostPort: the last colon separates a port when no other colon precedes it, or when the
    // last colon sits after the last ']' and every other colon sits inside the brackets.
    private static bool TrySplitHostPort(string entry, out string host, out string port)
    {
        int portSeparator = entry.LastIndexOf(':');
        if (portSeparator != -1)
        {
            int otherColon = entry.AsSpan(0, portSeparator).LastIndexOf(':');
            int ipv6End = entry.LastIndexOf(']');
            if (otherColon == -1 || (portSeparator > ipv6End && otherColon < ipv6End))
            {
                host = entry[..portSeparator];
                port = entry[(portSeparator + 1)..];
                return true;
            }
        }

        host = string.Empty;
        port = string.Empty;
        return false;
    }

    /// <summary>Whether a host names a Unix socket directory (rooted, or <c>@</c>-abstract) rather than a network host.</summary>
    internal static bool IsUnixSocket(string entry) => entry[0] is '/' or '@';

    private static string StripBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
}

/// <summary>
/// The environment every <c>psql</c> process receives (D-10): the inherited environment is discarded and
/// exactly the allowlisted variables are set, so neither a password-carrying variable nor an operator's
/// <c>PGSERVICE</c>, <c>PGOPTIONS</c>, or <c>PSQLRC</c> can reach the replay. <c>PATH</c> is copied from
/// this process so the executable's own dependencies resolve; <c>LC_ALL=C</c> fixes the language of
/// <c>psql</c>'s own messages (the server's are in its <c>lc_messages</c>); <c>PGCLIENTENCODING=UTF8</c> fixes
/// the replay encoding. On Windows
/// <c>SystemRoot</c> is copied too, because the system's socket and name resolution libraries cannot load
/// without it (host names then fail with Winsock error 11003); it carries no credential.
/// <para>
/// Clearing the environment does not stop <c>libpq</c> from reading the TLS files of the user's home
/// directory: it finds that directory through <c>getpwuid</c> when <c>HOME</c> is unset (<c>%APPDATA%</c> on
/// Windows), and an empty variable counts as unset. The two defaults Npgsql never reads are therefore set
/// explicitly: the root certificate is the configured one or <see cref="AbsentFilePath"/>, so an unrelated
/// <c>root.crt</c> cannot turn <c>require</c> into <c>verify-ca</c> (Npgsql verifies nothing in that mode and
/// reads only the configured file in the verifying ones); the revocation list is always
/// <see cref="AbsentFilePath"/>, as Npgsql loads none. No client certificate is sent
/// (<c>sslcertmode=disable</c>, so no key is read either): Npgsql's choice depends on this process's
/// <c>PGSSLCERT</c> and <c>HOME</c>, which the cleared environment cannot reproduce, so readiness refuses any
/// configuration in which Npgsql would send one (<see cref="PostgresqlEnvironment.ClientCertificateProbe"/>).
/// </para>
/// <para>
/// GSS encryption is disabled (<c>gssencmode=disable</c>): <c>libpq</c> prefers it to TLS whenever usable
/// Kerberos credentials exist, regardless of <c>sslmode</c>, which would bypass the configured TLS
/// verification; Npgsql 8 never negotiates it.
/// </para>
/// </summary>
internal static class LibpqEnvironment
{
    public const string PasswordFileVariable = "PGPASSFILE";
    public const string ApplicationNameVariable = "PGAPPNAME";
    public const string SslModeVariable = "PGSSLMODE";
    public const string SslRootCertificateVariable = "PGSSLROOTCERT";
    public const string SslRevocationListVariable = "PGSSLCRL";
    public const string SslCertificateModeVariable = "PGSSLCERTMODE";
    public const string GssEncryptionModeVariable = "PGGSSENCMODE";
    public const string ConnectTimeoutVariable = "PGCONNECT_TIMEOUT";
    public const string ClientEncodingVariable = "PGCLIENTENCODING";
    public const string LocaleVariable = "LC_ALL";
    public const string PathVariable = "PATH";
    public const string SystemRootVariable = "SystemRoot";

    public const string ClientEncoding = "UTF8";
    public const string Locale = "C";
    public const string SslCertificateModeDisable = "disable";
    public const string GssEncryptionModeDisable = "disable";

    /// <summary>The only variable names a <c>psql</c> process may receive, in the order D-10 lists them, then the revocation list, certificate mode and GSS encryption mode, then <c>SystemRoot</c> on Windows.</summary>
    public static IReadOnlyList<string> AllowedVariables { get; } =
    [
        PasswordFileVariable,
        ApplicationNameVariable,
        SslModeVariable,
        SslRootCertificateVariable,
        ConnectTimeoutVariable,
        ClientEncodingVariable,
        LocaleVariable,
        PathVariable,
        SslRevocationListVariable,
        SslCertificateModeVariable,
        GssEncryptionModeVariable,
        .. OperatingSystem.IsWindows() ? [SystemRootVariable] : Array.Empty<string>(),
    ];

    /// <summary>
    /// A path no file can occupy while the replay runs: beneath the password file, which is a regular file for
    /// the whole run. <c>libpq</c> treats a root certificate it cannot <c>stat</c> as absent and ignores a
    /// revocation list it cannot load.
    /// </summary>
    public static string AbsentFilePath(string passwordFilePath) => Path.Combine(passwordFilePath, "absent");

    /// <summary>
    /// Replaces the environment of <paramref name="startInfo"/> with the allowlist. The connection-derived
    /// variables, the root certificate, revocation list, certificate mode and GSS encryption mode among them, are set only when
    /// <paramref name="connection"/> is given (the version probe needs none), which requires
    /// <paramref name="passwordFilePath"/>; the application name only when supplied. No value is ever the password.
    /// </summary>
    public static void Apply(
        ProcessStartInfo startInfo,
        LibpqConnectionSettings? connection,
        string? passwordFilePath,
        string? applicationName
    )
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        IDictionary<string, string?> environment = startInfo.Environment;
        environment.Clear();

        string? path = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrEmpty(path))
        {
            environment[PathVariable] = path;
        }

        string? systemRoot = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(SystemRootVariable)
            : null;
        if (!string.IsNullOrEmpty(systemRoot))
        {
            environment[SystemRootVariable] = systemRoot;
        }

        environment[LocaleVariable] = Locale;
        environment[ClientEncodingVariable] = ClientEncoding;

        if (connection is not null)
        {
            ArgumentNullException.ThrowIfNull(passwordFilePath);
            environment[SslModeVariable] = connection.SslMode;
            environment[SslRootCertificateVariable] =
                connection.RootCertificate ?? AbsentFilePath(passwordFilePath);
            environment[SslRevocationListVariable] = AbsentFilePath(passwordFilePath);
            environment[SslCertificateModeVariable] = SslCertificateModeDisable;
            environment[GssEncryptionModeVariable] = GssEncryptionModeDisable;
            environment[ConnectTimeoutVariable] = connection.ConnectTimeoutSeconds.ToString(
                CultureInfo.InvariantCulture
            );
        }

        if (passwordFilePath is not null)
        {
            environment[PasswordFileVariable] = passwordFilePath;
        }

        if (applicationName is not null)
        {
            environment[ApplicationNameVariable] = applicationName;
        }
    }
}
