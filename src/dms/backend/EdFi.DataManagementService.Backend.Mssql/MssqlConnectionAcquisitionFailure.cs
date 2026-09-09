// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Which SQL Server failures count as the expected failures of establishing a connection, as opposed
/// to a defect raised inside the same boundary.
/// </summary>
/// <remarks>
/// One definition per engine, shared by the DocumentCache read-lookup adapter - where it decides an
/// unavailable cache read - and by the read-path seam guard, where it decides an unavailable database.
/// The two must agree: a failure one of them treats as a defect and the other as unavailability would
/// answer the same broken snapshot two different ways depending on whether the cache was consulted.
/// </remarks>
internal static class MssqlConnectionAcquisitionFailure
{
    /// <summary>
    /// True for a failure of connection construction, connection-string parsing, or the open call
    /// itself: catalog absence, authentication failure, DNS or network failure, timeout, and firewall
    /// rejection all arrive as one of these, and a connection string that is present but
    /// provider-invalid arrives as a parse failure before the open is even attempted.
    /// </summary>
    /// <remarks>
    /// <c>DbException</c> rather than <c>SqlException</c>, so a failure surfaced by a wrapping provider
    /// is classified the same way the driver's own is. <c>ArgumentNullException</c> is deliberately
    /// excluded: a null argument is a programming defect, not an unreachable database. It is the only
    /// listed type it derives from, which is why the exclusion is written against the
    /// <c>ArgumentException</c> arm - <c>and</c> binds tighter than <c>or</c> in a pattern, so the
    /// exclusion applies to that arm alone.
    /// </remarks>
    public static bool IsExpected(Exception exception) =>
        exception
            is DbException
                or TimeoutException
                or FormatException
                or ArgumentException
                and not ArgumentNullException;

    /// <summary>
    /// The log-safe description of a classified failure: its type, plus the server's own error number
    /// when SQL Server raised one - <c>SqlException(4060)</c> for a catalog the login cannot open,
    /// <c>(18456)</c> for a failed login, <c>(-2)</c> for a connection timeout.
    /// </summary>
    /// <remarks>
    /// <c>SqlException.Number</c> rather than <c>DbException.SqlState</c>: SqlClient does not populate
    /// the engine-agnostic property, so reading it here would silently describe every SQL Server
    /// failure by type alone. The number is an integer and carries no part of the connection string,
    /// which is what makes it the one detail beyond the type that may be logged. A parsing
    /// <c>ArgumentException</c> and a wrapping provider's plain <c>DbException</c> have no number and
    /// are described by type alone.
    /// </remarks>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string typeName = exception.GetType().Name;

        return exception is SqlException sqlException ? $"{typeName}({sqlException.Number})" : typeName;
    }
}
