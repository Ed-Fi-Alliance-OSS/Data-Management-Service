// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The 4.3 settings the SQL Server engine reads: the lock budget and heartbeat cadence (D-12). The steps that
/// read further settings add them here. Every value is range-checked so the engine never holds a setting
/// outside the documented range.
/// </summary>
public sealed record MssqlDmsEngineSettings
{
    public static readonly TimeSpan MaximumLockTimeout = TimeSpan.FromHours(1);

    public MssqlDmsEngineSettings(TimeSpan lockAcquireTimeout, TimeSpan lockHeartbeatInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockAcquireTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockAcquireTimeout, MaximumLockTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockHeartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockHeartbeatInterval, MaximumLockTimeout);
        LockAcquireTimeout = lockAcquireTimeout;
        LockHeartbeatInterval = lockHeartbeatInterval;
    }

    /// <summary><c>LockAcquireTimeout</c>: the server-side wait for the target lock (D-12).</summary>
    public TimeSpan LockAcquireTimeout { get; }

    /// <summary><c>LockHeartbeatInterval</c>: the holder session's probe cadence (D-12).</summary>
    public TimeSpan LockHeartbeatInterval { get; }
}
