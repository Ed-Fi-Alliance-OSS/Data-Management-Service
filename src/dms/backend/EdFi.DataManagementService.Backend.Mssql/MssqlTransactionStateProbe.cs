// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Recognizes the one SQL Server state in which a client-side rollback can only throw: the server aborted
/// the batch under <c>SET XACT_ABORT ON</c>, rolled the transaction back itself, and left the client
/// transaction object detached, so <c>SqlTransaction.ZombieCheck</c> rejects every further operation on it.
/// </summary>
/// <remarks>
/// Every condition is required, and each excludes a state that must still surface rather than be tolerated.
/// An open connection excludes a connection-level fault, which also detaches the transaction but has to be
/// reported. A detached <see cref="SqlTransaction"/> is the positive proof of server-side completion, and
/// type-testing it keeps the tolerance from leaking to another provider whose transaction happens to null
/// its connection. A reported failure below the fatal severity threshold excludes a connection-terminating
/// error, where the transaction's fate is not something the client can conclude from a null reference.
/// Anything false or indeterminate falls through to the physical rollback.
/// </remarks>
internal sealed class MssqlTransactionStateProbe : IRelationalTransactionStateProbe
{
    public static readonly MssqlTransactionStateProbe Instance = new();

    /// <summary>
    /// SQL Server severities of 20 and above terminate the connection. Below that the server is still
    /// answering, so a detached transaction is a decision it made rather than a symptom of a lost link.
    /// </summary>
    private const byte FatalErrorSeverity = 20;

    private MssqlTransactionStateProbe() { }

    public bool IsAlreadyCompleted(
        DbConnection connection,
        DbTransaction transaction,
        DbException reportedFailure
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(reportedFailure);

        return connection.State == ConnectionState.Open
            && transaction is SqlTransaction { Connection: null }
            && reportedFailure is SqlException { Class: < FatalErrorSeverity };
    }
}
