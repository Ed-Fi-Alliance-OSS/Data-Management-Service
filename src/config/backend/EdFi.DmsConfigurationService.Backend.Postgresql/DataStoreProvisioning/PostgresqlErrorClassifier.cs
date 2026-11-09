// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The only reader of <see cref="PostgresException"/> in the provisioner. It keeps the SQLSTATE of the first
/// PostgreSQL error in an exception chain and never reads the message, detail, hint, or any field that can
/// carry a value, and it classifies a SQLSTATE by what it proves about the command's effect (6.9a): a
/// server-reported error whose class rules out a commit is a definite failure; a connection-class or
/// cancellation-class state leaves the outcome in doubt.
/// </summary>
internal static class PostgresqlErrorClassifier
{
    /// <summary>The SQLSTATE of the first <see cref="PostgresException"/> in the chain, when it has the five-character shape; otherwise null.</summary>
    public static string? SqlStateOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return IsSqlState(postgres.SqlState) ? postgres.SqlState : null;
            }
        }

        return null;
    }

    /// <summary>Connection exceptions (class 08) and operator-intervention shutdowns: the session is gone or never existed.</summary>
    public static bool IsConnectionClass(string sqlState)
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        return sqlState.StartsWith("08", StringComparison.Ordinal)
            || sqlState
                is PostgresErrorCodes.AdminShutdown
                    or PostgresErrorCodes.CrashShutdown
                    or PostgresErrorCodes.CannotConnectNow;
    }

    /// <summary>The missing-object states a catalog read maps to "table missing": an undefined relation or an undefined schema.</summary>
    public static bool IsMissingRelation(string sqlState)
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        return sqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName;
    }

    /// <summary>
    /// A definite refusal for lack of a privilege: <c>42501</c> (a denied read, or <c>CONNECT</c> denied on the
    /// database) or class <c>28</c> (the server's authentication rules reject this role for this database).
    /// Nothing was read or opened, and retrying without an operator changing a grant cannot succeed.
    /// </summary>
    public static bool IsAccessRefusal(string sqlState)
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        return sqlState == PostgresErrorCodes.InsufficientPrivilege
            || sqlState.StartsWith("28", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> proves the session's connection failed (D-12 loss evidence): the
    /// first PostgreSQL error in the chain is connection class, or, when there is none, a transport failure
    /// (<see cref="IOException"/>, <see cref="SocketException"/>) sits anywhere in the chain. A cancelled
    /// statement (<c>57014</c>), a driver timeout the server acknowledged, and every other server-reported
    /// error leave the connection usable and are not loss.
    /// </summary>
    public static bool IsConnectionFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string? sqlState = SqlStateOf(exception);
        if (sqlState is not null)
        {
            return IsConnectionClass(sqlState);
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The failure class a SQLSTATE proves (6.9a), or null when the outcome is in doubt: a connection-class
    /// state, a cancelled query (<c>57014</c>, cancellation after send), a dropped database (<c>57P04</c>), or
    /// the server's own statement of doubt (<c>40003</c>, <c>statement_completion_unknown</c>). Any other
    /// server-reported error came in the command's own response as a rejection of that command, which is
    /// treated as proof of no commit; a state that does not say so is added here, never to the definite set.
    /// </summary>
    public static DmsCommandFailureClass? FailureClassOf(string sqlState)
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        if (
            IsConnectionClass(sqlState)
            || sqlState
                is PostgresErrorCodes.QueryCanceled
                    or PostgresErrorCodes.DatabaseDropped
                    or PostgresErrorCodes.StatementCompletionUnknown
        )
        {
            return null;
        }

        return sqlState switch
        {
            PostgresErrorCodes.DuplicateDatabase => DmsCommandFailureClass.DuplicateName,
            PostgresErrorCodes.ObjectInUse => DmsCommandFailureClass.ObjectInUse,
            PostgresErrorCodes.InsufficientPrivilege => DmsCommandFailureClass.PrivilegeMissing,
            PostgresErrorCodes.InvalidCatalogName or PostgresErrorCodes.UndefinedTable =>
                DmsCommandFailureClass.DoesNotExist,
            _ => DmsCommandFailureClass.Rejected,
        };
    }

    /// <summary>A diagnostic carrying the exception's type chain and its SQLSTATE, nothing else.</summary>
    public static DmsProvisioningDiagnostic Diagnostic(Exception exception, string operation) =>
        DmsProvisioningDiagnostic.From(exception, operation, SqlStateOf(exception));

    /// <summary>
    /// Whether a definite rejection names a condition that clears without any change to the request, the
    /// artifact, or the server's configuration, so the same request can succeed later: a serialization failure
    /// (<c>40001</c>), a detected deadlock (<c>40P01</c>), a lock not available (<c>55P03</c>), or temporary
    /// resource exhaustion (<c>53000</c>, <c>53100</c> disk full, <c>53200</c> out of memory, <c>53300</c> too
    /// many connections). This answers only whether a retry can succeed; whether the command committed is
    /// <see cref="FailureClassOf"/>'s question, and every state here is a definite failure there.
    /// </summary>
    public static bool IsRetryableRejection(string sqlState)
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        return sqlState
            is PostgresErrorCodes.SerializationFailure
                or PostgresErrorCodes.DeadlockDetected
                or PostgresErrorCodes.LockNotAvailable
                or PostgresErrorCodes.InsufficientResources
                or PostgresErrorCodes.DiskFull
                or PostgresErrorCodes.OutOfMemory
                or PostgresErrorCodes.TooManyConnections;
    }

    /// <summary>A SQLSTATE is five ASCII digits or upper-case letters; any other text is not kept, whether it came from the driver or from <c>psql</c> output.</summary>
    public static bool IsSqlState(string? sqlState) =>
        sqlState is { Length: 5 }
        && sqlState.All(character => char.IsAsciiDigit(character) || char.IsAsciiLetterUpper(character));
}
