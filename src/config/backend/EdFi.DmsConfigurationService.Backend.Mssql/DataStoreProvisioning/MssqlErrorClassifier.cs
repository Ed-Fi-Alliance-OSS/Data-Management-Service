// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The only reader of <see cref="SqlException"/> in the provisioner. It keeps the error number and severity
/// of the first SQL Server error in an exception chain and never reads the message, procedure, server, or
/// any field that can carry a value, and it classifies an error number by what it proves about the
/// command's effect (6.9a): a server-reported error whose number rules out a commit is a definite failure;
/// a connection-class error, a client timeout, or a cancellation leaves the outcome in doubt.
/// </summary>
internal static class MssqlErrorClassifier
{
    /// <summary>SqlClient's own number for a command that exceeded its client-side timeout: sent, outcome unknown.</summary>
    public const int ClientTimeout = -2;

    /// <summary>SqlClient's number for a request it cancelled after sending (<c>Operation cancelled by user</c>).</summary>
    public const int ClientCancelled = 0;

    public const int InvalidObjectName = 208;
    public const int ObjectPermissionDenied = 229;
    public const int ColumnPermissionDenied = 230;
    public const int DatabasePermissionDenied = 262;
    public const int NoPermissionForAction = 297;
    public const int ServerPermissionDenied = 300;
    public const int DatabaseDoesNotExist = 911;
    public const int PrincipalCannotAccessDatabase = 916;
    public const int DatabaseAlreadyExists = 1801;
    public const int DatabaseInUseForDrop = 3702;
    public const int DatabaseInUseForRestore = 3101;
    public const int RestoreTerminatedAbnormally = 3013;
    public const int RestoreNoPermission = 3110;
    public const int RestoreOverExistingFile = 3142;
    public const int RestoreTailLogNotBackedUp = 3159;
    public const int BackupDeviceCannotOpen = 3201;
    public const int BackupMediaFamilyMalformed = 3241;
    public const int BackupNotAValidBackupSet = 3242;
    public const int BatchAborted = 3980;
    public const int CannotOpenRequestedDatabase = 4060;
    public const int AlterDatabaseNoPermission = 5011;
    public const int DatabaseNotExclusivelyLocked = 5030;
    public const int AlterDatabaseLockNotPlaced = 5061;
    public const int KillNoPermission = 6102;
    public const int KillNoSuchSession = 6106;
    public const int ActionNoPermission = 15247;
    public const int LoginFailed = 18456;

    /// <summary>The lowest severity at which SQL Server ends the session: the connection is gone.</summary>
    public const byte FatalSeverity = 20;

    /// <summary>
    /// Client-side transport failures SqlClient reports as error numbers (named pipes, TCP, SNI): the
    /// connection never existed or is gone.
    /// </summary>
    private static readonly HashSet<int> _transportNumbers =
    [
        -1,
        2,
        40,
        53,
        64,
        121,
        233,
        10053,
        10054,
        10060,
        11001,
    ];

    /// <summary>The first <see cref="SqlException"/> in the chain, if any.</summary>
    private static SqlException? FirstSqlException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql;
            }
        }

        return null;
    }

    /// <summary>The number of the first SQL Server error in the chain (its first reported error), or null when there is none.</summary>
    public static int? ErrorNumberOf(Exception exception) => FirstSqlException(exception)?.Number;

    /// <summary>
    /// Whether an error with this number and severity proves the session's connection failed (D-12 loss
    /// evidence): a severity that ends the session, or a client-side transport failure.
    /// </summary>
    public static bool IsConnectionClass(int number, byte severity) =>
        severity >= FatalSeverity || _transportNumbers.Contains(number);

    /// <summary>
    /// Whether <paramref name="exception"/> proves the session's connection failed: the first SQL Server error
    /// in the chain is connection class, or, when there is none, a transport failure (<see cref="IOException"/>,
    /// <see cref="SocketException"/>) sits anywhere in the chain.
    /// </summary>
    public static bool IsConnectionFailure(Exception exception)
    {
        SqlException? sql = FirstSqlException(exception);
        if (sql is not null)
        {
            return IsConnectionClass(sql.Number, sql.Class);
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

    /// <summary>The missing-object error a catalog read maps to "table missing": an invalid object name, which covers a missing schema too.</summary>
    public static bool IsMissingRelation(int number) => number == InvalidObjectName;

    /// <summary>
    /// A definite refusal for lack of a privilege or of access: a denied permission on an object, column,
    /// database, or server; a principal that cannot access the database; a failed login. Nothing was read or
    /// opened, and retrying without an operator changing a grant cannot succeed. <c>4060</c> is not here: it
    /// also reports a database that does not exist, so a caller resolves it by inspection.
    /// </summary>
    public static bool IsAccessRefusal(int number) =>
        number
            is ObjectPermissionDenied
                or ColumnPermissionDenied
                or DatabasePermissionDenied
                or NoPermissionForAction
                or ServerPermissionDenied
                or PrincipalCannotAccessDatabase
                or RestoreNoPermission
                or AlterDatabaseNoPermission
                or KillNoPermission
                or ActionNoPermission
                or LoginFailed;

    /// <summary>
    /// The failure class an error proves (6.9a), or null when the outcome is in doubt: a connection-class
    /// error, a client timeout (<c>-2</c>), a client cancellation after send (<c>0</c>), or an aborted batch
    /// (<c>3980</c>). Any other server-reported error came in the command's own response as a rejection of
    /// that command, which is treated as proof of no commit; a number that does not say so is added here,
    /// never to the definite set.
    /// </summary>
    public static DmsCommandFailureClass? FailureClassOf(int number, byte severity)
    {
        if (IsConnectionClass(number, severity) || number is ClientTimeout or ClientCancelled or BatchAborted)
        {
            return null;
        }

        if (IsAccessRefusal(number))
        {
            return DmsCommandFailureClass.PrivilegeMissing;
        }

        return number switch
        {
            DatabaseAlreadyExists => DmsCommandFailureClass.DuplicateName,
            AlterDatabaseLockNotPlaced
            or DatabaseNotExclusivelyLocked
            or DatabaseInUseForDrop
            or DatabaseInUseForRestore => DmsCommandFailureClass.ObjectInUse,
            DatabaseDoesNotExist or InvalidObjectName or KillNoSuchSession =>
                DmsCommandFailureClass.DoesNotExist,
            _ => DmsCommandFailureClass.Rejected,
        };
    }

    /// <summary>A diagnostic carrying the exception's type chain and its first SQL Server error number, nothing else.</summary>
    public static DmsProvisioningDiagnostic Diagnostic(Exception exception, string operation) =>
        DmsProvisioningDiagnostic.From(
            exception,
            operation,
            ErrorNumberOf(exception)?.ToString(CultureInfo.InvariantCulture)
        );
}
