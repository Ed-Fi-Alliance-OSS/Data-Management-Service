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
    /// provider-invalid arrives as a parse failure before the open is even attempted.
    /// </summary>
    /// <remarks>
    /// <c>ArgumentNullException</c> is deliberately excluded: a null argument is a programming defect,
    /// not an unreachable database. It is the only listed type it derives from, which is why the
    /// exclusion is written against the <c>ArgumentException</c> arm - <c>and</c> binds tighter than
    /// <c>or</c> in a pattern, so the exclusion applies to that arm alone.
    /// </remarks>
    public static bool IsExpected(Exception exception) =>
        exception
            is NpgsqlException
                or TimeoutException
                or FormatException
                or ArgumentException
                and not ArgumentNullException;
}
