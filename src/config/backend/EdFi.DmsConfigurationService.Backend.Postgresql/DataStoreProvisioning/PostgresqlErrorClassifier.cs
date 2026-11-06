// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    /// The failure class a SQLSTATE proves (6.9a), or null when the outcome is in doubt: a connection-class
    /// state, a cancelled query (<c>57014</c>, cancellation after send), or a dropped database (<c>57P04</c>).
    /// Every other server-reported error came in the command's own response and proves no commit.
    /// </summary>
    public static DmsCommandFailureClass? FailureClassOf(string sqlState)
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        if (IsConnectionClass(sqlState) || sqlState is PostgresErrorCodes.QueryCanceled or "57P04")
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

    // A SQLSTATE is five ASCII digits or upper-case letters; any other text is not kept.
    private static bool IsSqlState(string? sqlState) =>
        sqlState is { Length: 5 }
        && sqlState.All(character => char.IsAsciiDigit(character) || char.IsAsciiLetterUpper(character));
}
