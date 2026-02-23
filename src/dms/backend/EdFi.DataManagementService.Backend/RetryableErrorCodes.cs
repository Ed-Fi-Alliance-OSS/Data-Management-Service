// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Defines retryable SQL error codes per database engine for deadlock/serialization failures.
/// PostgreSQL codes are already handled by the Npgsql catch blocks in the operation files.
/// SQL Server codes are defined here for use when the MSSQL repository is implemented.
/// </summary>
public static class RetryableErrorCodes
{
    // PostgreSQL error codes (SqlState strings)
    public const string PgDeadlockDetected = "40P01";
    public const string PgSerializationFailure = "40001";

    // SQL Server error numbers
    public const int MssqlDeadlockVictim = 1205;
    public const int MssqlLockRequestTimeout = 1222;

    /// <summary>
    /// Returns true if the given SQL Server error number is a retryable deadlock/lock error.
    /// </summary>
    public static bool IsMssqlRetryable(int errorNumber) =>
        errorNumber is MssqlDeadlockVictim or MssqlLockRequestTimeout;
}
