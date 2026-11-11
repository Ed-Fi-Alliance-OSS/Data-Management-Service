// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The settings the SQL Server engine reads: the lock budget and heartbeat cadence (D-12), the bound for
/// proving a session gone when the engine resolves a restore itself (D-22), the restore bound, and the staging
/// routine's settings (D-18). Every value is range-checked so the engine never holds a setting outside the
/// documented range.
/// </summary>
public sealed record MssqlDmsEngineSettings
{
    public static readonly TimeSpan MaximumLockTimeout = TimeSpan.FromHours(1);

    public static readonly TimeSpan MaximumSessionTerminationTimeout = TimeSpan.FromHours(1);

    public static readonly TimeSpan MaximumRestoreTimeout = TimeSpan.FromHours(6);

    public MssqlDmsEngineSettings(
        TimeSpan lockAcquireTimeout,
        TimeSpan lockHeartbeatInterval,
        TimeSpan sessionTerminationTimeout,
        TimeSpan restoreTimeout,
        MssqlStagingSettings staging
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockAcquireTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockAcquireTimeout, MaximumLockTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockHeartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockHeartbeatInterval, MaximumLockTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sessionTerminationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            sessionTerminationTimeout,
            MaximumSessionTerminationTimeout
        );
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(restoreTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(restoreTimeout, MaximumRestoreTimeout);
        ArgumentNullException.ThrowIfNull(staging);
        LockAcquireTimeout = lockAcquireTimeout;
        LockHeartbeatInterval = lockHeartbeatInterval;
        SessionTerminationTimeout = sessionTerminationTimeout;
        RestoreTimeout = restoreTimeout;
        Staging = staging;
    }

    /// <summary><c>LockAcquireTimeout</c>: the server-side wait for the target lock (D-12).</summary>
    public TimeSpan LockAcquireTimeout { get; }

    /// <summary><c>LockHeartbeatInterval</c>: the holder session's probe cadence (D-12).</summary>
    public TimeSpan LockHeartbeatInterval { get; }

    /// <summary><c>SessionTerminationTimeout</c>: the bound for proving the restore's work session gone when the engine resolves a restore in doubt (D-7b, D-22).</summary>
    public TimeSpan SessionTerminationTimeout { get; }

    /// <summary><c>RestoreTimeout</c>: the bound of one <c>RESTORE DATABASE</c>, on the engine's clock and as the driver timeout.</summary>
    public TimeSpan RestoreTimeout { get; }

    /// <summary>The staging directory, its server view, the staged file mode, and the deletion bounds (D-18, D-19).</summary>
    public MssqlStagingSettings Staging { get; }
}
