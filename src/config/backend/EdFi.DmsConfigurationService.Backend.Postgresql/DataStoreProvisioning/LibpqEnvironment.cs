// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The administrative connection as <c>libpq</c> receives it (D-10). The validator admits only the keys
/// this record carries, so nothing the connection string says is silently lost on the way to <c>psql</c>:
/// the hosts, port, user, and password go to the password file and the command line; the SSL mode, root
/// certificate, and connect timeout go to the environment. The record is never logged or formatted.
/// </summary>
/// <param name="Host">The <c>Host</c> value, possibly a comma-separated multi-host list.</param>
/// <param name="SslMode">The <c>libpq</c> spelling of the SSL mode (<c>disable</c> … <c>verify-full</c>).</param>
/// <param name="RootCertificate">The <c>Root Certificate</c> path, or null when none is configured.</param>
/// <param name="ConnectTimeoutSeconds">The <c>Timeout</c> value, in seconds.</param>
internal sealed record LibpqConnectionSettings(
    string Host,
    int Port,
    string Username,
    string Password,
    string SslMode,
    string? RootCertificate,
    int ConnectTimeoutSeconds
)
{
    /// <summary>The hosts of a multi-host value, trimmed and in order; a single host yields one entry.</summary>
    public IReadOnlyList<string> Hosts =>
        Host.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

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
            builder.Host,
            builder.Port,
            builder.Username,
            builder.Password ?? string.Empty,
            SslModeName(builder.SslMode),
            string.IsNullOrEmpty(builder.RootCertificate) ? null : builder.RootCertificate,
            builder.Timeout
        );
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
}

/// <summary>
/// The environment every <c>psql</c> process receives (D-10): the inherited environment is discarded and
/// exactly the allowlisted variables are set, so neither a password-carrying variable nor an operator's
/// <c>PGSERVICE</c>, <c>PGOPTIONS</c>, or <c>PSQLRC</c> can reach the replay. <c>PATH</c> is copied from
/// this process so the executable's own dependencies resolve; <c>LC_ALL=C</c> fixes the message language
/// the SQLSTATE extraction relies on; <c>PGCLIENTENCODING=UTF8</c> fixes the replay encoding.
/// </summary>
internal static class LibpqEnvironment
{
    public const string PasswordFileVariable = "PGPASSFILE";
    public const string ApplicationNameVariable = "PGAPPNAME";
    public const string SslModeVariable = "PGSSLMODE";
    public const string SslRootCertificateVariable = "PGSSLROOTCERT";
    public const string ConnectTimeoutVariable = "PGCONNECT_TIMEOUT";
    public const string ClientEncodingVariable = "PGCLIENTENCODING";
    public const string LocaleVariable = "LC_ALL";
    public const string PathVariable = "PATH";

    public const string ClientEncoding = "UTF8";
    public const string Locale = "C";

    /// <summary>The only variable names a <c>psql</c> process may receive, in the order D-10 lists them.</summary>
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
    ];

    /// <summary>
    /// Replaces the environment of <paramref name="startInfo"/> with the allowlist. The connection-derived
    /// variables are set only when <paramref name="connection"/> is given (the version probe needs none),
    /// <see cref="SslRootCertificateVariable"/> only when a certificate is configured, and the password file
    /// and application name only when supplied. No value is ever the password.
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

        environment[LocaleVariable] = Locale;
        environment[ClientEncodingVariable] = ClientEncoding;

        if (connection is not null)
        {
            environment[SslModeVariable] = connection.SslMode;
            if (connection.RootCertificate is not null)
            {
                environment[SslRootCertificateVariable] = connection.RootCertificate;
            }

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
