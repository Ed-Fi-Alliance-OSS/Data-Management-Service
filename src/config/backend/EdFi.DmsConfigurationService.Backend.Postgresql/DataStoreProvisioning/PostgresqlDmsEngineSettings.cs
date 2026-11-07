// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The 4.3 settings the PostgreSQL engine reads: the lock budget and heartbeat cadence (D-12), the bound
/// for proving a session gone (D-22), the bound of one replay (D-10), and the <c>psql</c> executable
/// (fully qualified) with the major-version floor readiness holds it to (D-9, 7.3 item 5). Every value is range-checked here so the engine
/// never holds a setting outside the documented range.
/// </summary>
public sealed record PostgresqlDmsEngineSettings
{
    public static readonly TimeSpan MaximumLockTimeout = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumRestoreTimeout = TimeSpan.FromHours(6);

    /// <summary>The 4.3 default <c>psql</c> floor (D-9).</summary>
    public const int DefaultMinimumPsqlMajorVersion = 18;

    public PostgresqlDmsEngineSettings(
        TimeSpan lockAcquireTimeout,
        TimeSpan lockHeartbeatInterval,
        TimeSpan sessionTerminationTimeout,
        TimeSpan restoreTimeout,
        string psqlPath
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockAcquireTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockAcquireTimeout, MaximumLockTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockHeartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockHeartbeatInterval, MaximumLockTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sessionTerminationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            sessionTerminationTimeout,
            PostgresqlSessionQueries.MaximumDeadline
        );
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(restoreTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(restoreTimeout, MaximumRestoreTimeout);
        ArgumentException.ThrowIfNullOrWhiteSpace(psqlPath);
        if (!Path.IsPathFullyQualified(psqlPath))
        {
            throw new ArgumentException("The psql path must be fully qualified.", nameof(psqlPath));
        }

        LockAcquireTimeout = lockAcquireTimeout;
        LockHeartbeatInterval = lockHeartbeatInterval;
        SessionTerminationTimeout = sessionTerminationTimeout;
        RestoreTimeout = restoreTimeout;
        PsqlPath = psqlPath;
    }

    /// <summary><c>LockAcquireTimeout</c>: the server-side wait for the target lock (D-12).</summary>
    public TimeSpan LockAcquireTimeout { get; }

    /// <summary><c>LockHeartbeatInterval</c>: the holder session's probe cadence (D-12).</summary>
    public TimeSpan LockHeartbeatInterval { get; }

    /// <summary><c>SessionTerminationTimeout</c>: the bound for proving a session gone when the engine resolves an outcome itself (D-22).</summary>
    public TimeSpan SessionTerminationTimeout { get; }

    /// <summary><c>RestoreTimeout</c>: the bound of one <c>psql</c> replay (D-10).</summary>
    public TimeSpan RestoreTimeout { get; }

    /// <summary><c>Postgresql:PsqlPath</c>, fully qualified.</summary>
    public string PsqlPath { get; }

    /// <summary><c>Postgresql:MinimumPsqlMajorVersion</c>: the lowest <c>psql</c> major readiness accepts (D-9).</summary>
    public int MinimumPsqlMajorVersion
    {
        get => _minimumPsqlMajorVersion;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _minimumPsqlMajorVersion = value;
        }
    }

    private readonly int _minimumPsqlMajorVersion = DefaultMinimumPsqlMajorVersion;
}
