// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The credential handoff of D-10: a <c>libpq</c> password file written inside the operation's private
/// directory with mode <c>0600</c> (<see cref="PrivateOperationDirectory.CreateFile"/>), named through
/// <c>PGPASSFILE</c>, so the password is never an argument or an environment value. One line per endpoint
/// of the administrative connection, each <c>host:port:database:user:password</c> with <c>:</c> and
/// <c>\</c> escaped by a backslash as <c>libpq</c> requires. A write that fails after the file was created
/// deletes it before the failure propagates; an existing entry of that name is refused untouched. The
/// runner deletes the file as soon as the replay ends and the directory's disposal removes it in any case.
/// </summary>
internal static class PgPassFile
{
    public const string FileName = PrivateOperationDirectory.CredentialFileName;

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes the file for <paramref name="database"/> and returns its path. The handle is closed before the
    /// method returns, because the file is created with no sharing and <c>psql</c> must be able to open it.
    /// </summary>
    public static Task<string> WriteAsync(
        PrivateOperationDirectory directory,
        LibpqConnectionSettings connection,
        string database,
        CancellationToken cancellationToken
    ) => WriteAsync(directory, connection, database, beforeWrite: null, cancellationToken);

    /// <summary>
    /// The write with a test seam: <paramref name="beforeWrite"/> runs once the file exists and before any
    /// byte is written, the point where a write failure leaves a created file behind.
    /// </summary>
    internal static async Task<string> WriteAsync(
        PrivateOperationDirectory directory,
        LibpqConnectionSettings connection,
        string database,
        Action<FileStream>? beforeWrite,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        StringBuilder content = new();
        foreach (LibpqEndpoint endpoint in connection.Endpoints)
        {
            content
                .Append(
                    FormatLine(
                        endpoint.Host,
                        endpoint.Port,
                        database,
                        connection.Username,
                        connection.Password
                    )
                )
                .Append('\n');
        }

        // CreateFile refuses an existing entry before anything is created, so a failure there deletes nothing.
        FileStream stream = directory.CreateFile(FileName);
        string path = directory.PathOf(FileName);
        try
        {
            await using (stream)
            {
                beforeWrite?.Invoke(stream);
                await stream.WriteAsync(_utf8.GetBytes(content.ToString()), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return path;
    }

    /// <summary>One <c>libpq</c> password file line; every text field is escaped, the port is decimal.</summary>
    public static string FormatLine(string host, int port, string database, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        return string.Join(
            ':',
            Escape(host),
            port.ToString(CultureInfo.InvariantCulture),
            Escape(database),
            Escape(username),
            Escape(password)
        );
    }

    /// <summary>
    /// Prefixes <c>:</c> and <c>\</c> with a backslash. A line break cannot be represented in the format at
    /// all and is refused, so a password that <c>libpq</c> could not read back is a loud failure here rather
    /// than an authentication failure on the server.
    /// </summary>
    public static string Escape(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.AsSpan().ContainsAny('\n', '\r'))
        {
            throw new ArgumentException("A password file field cannot contain a line break.", nameof(field));
        }

        StringBuilder escaped = new(field.Length);
        foreach (char character in field)
        {
            if (character is ':' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    /// <summary>Deletes the file on a best-effort basis; true when it is gone afterwards.</summary>
    public static bool TryDelete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return !File.Exists(path);
        }
        catch (UnauthorizedAccessException)
        {
            return !File.Exists(path);
        }
    }
}
