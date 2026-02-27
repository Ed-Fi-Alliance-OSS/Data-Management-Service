// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Retryable SQL Server error codes for deadlock/serialization failures.
/// Defined per DMS-996 task 2 and the design doc
/// (transactions-and-concurrency.md, "Deadlock + retry policy").
///
/// Not yet referenced — will be wired into the MSSQL repository's catch blocks
/// (mirroring the PostgreSQL backend's use of Npgsql's PostgresErrorCodes constants)
/// when that repository is implemented.
/// </summary>
public static class RetryableErrorCodes
{
    public const int DeadlockVictim = 1205;
    public const int LockRequestTimeout = 1222;

    /// <summary>
    /// Returns true if the given SQL Server error number is a retryable deadlock/lock error.
    /// </summary>
    public static bool IsRetryable(int errorNumber) => errorNumber is DeadlockVictim or LockRequestTimeout;
}
