// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Which PostgreSQL failures count as the expected failures of establishing a connection, as opposed
/// to a defect raised inside the same boundary.
/// </summary>
/// <remarks>
/// One definition per engine, shared by the DocumentCache read-lookup adapter - where it decides an
/// unavailable cache read - and by the read-path seam guard, where it decides an unavailable database.
/// The two must agree: a failure one of them treats as a defect and the other as unavailability would
/// answer the same broken snapshot two different ways depending on whether the cache was consulted.
/// </remarks>
internal static class PostgresqlConnectionAcquisitionFailure
{
    /// <summary>
    /// True for a failure of data-source or connection construction, connection-string parsing, or the
    /// open call itself: catalog absence, authentication failure, DNS or network failure, timeout, and
    /// firewall rejection all arrive as one of these, and a connection string that is present but
    /// provider-invalid is rejected during construction or parsing, before the open is even attempted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>NotSupportedException</c> covers the provider-invalid strings Npgsql rejects for an
    /// unsupported <i>combination</i> of individually valid options, which parsing accepts and
    /// <c>Build</c> refuses - <c>Multiplexing</c> with more than one host, and any
    /// <c>Target Session Attributes</c> other than <c>any</c> on a single-host string. The latter is
    /// the reachable one: a read-only session attribute is a natural thing to put on a snapshot or
    /// read-replica connection string, and Npgsql rejects it unless the string also lists several
    /// hosts. Both are raised by validation inside the data-source construction the acquisition
    /// boundary spans, so without this arm a misconfigured snapshot answers a service-configuration
    /// 503 instead of Snapshot Not Found.
    /// </para>
    /// <para>
    /// <c>ArgumentNullException</c> is deliberately excluded: a null argument is a programming defect,
    /// not an unreachable database. It is the only listed type it derives from, which is why the
    /// exclusion is written against the <c>ArgumentException</c> arm - <c>and</c> binds tighter than
    /// <c>or</c> in a pattern, so the exclusion applies to that arm alone. That arm is kept last for
    /// the same reason: a type added after it would bind to the exclusion rather than stand alone.
    /// </para>
    /// </remarks>
    public static bool IsExpected(Exception exception) =>
        exception
            is NpgsqlException
                or TimeoutException
                or FormatException
                or NotSupportedException
                or ArgumentException
                and not ArgumentNullException;

    /// <summary>
    /// The log-safe description of a classified failure: its type, plus the server's own
    /// <c>SQLSTATE</c> when PostgreSQL raised one - <c>PostgresException(3D000)</c> for an absent
    /// catalog, <c>(28P01)</c> for a failed authentication, <c>(08006)</c> for a broken connection.
    /// </summary>
    /// <remarks>
    /// <c>SqlState</c> is a five-character standard code and nothing else, so it carries no part of the
    /// connection string - which is what makes it the one detail beyond the type that may be logged.
    /// A parse failure and a socket failure arrive as an <c>NpgsqlException</c> or an
    /// <c>ArgumentException</c> with no <c>SqlState</c>, and are described by type alone.
    /// </remarks>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string typeName = exception.GetType().Name;

        return exception is PostgresException { SqlState: { Length: > 0 } sqlState }
            ? $"{typeName}({sqlState})"
            : typeName;
    }
}
