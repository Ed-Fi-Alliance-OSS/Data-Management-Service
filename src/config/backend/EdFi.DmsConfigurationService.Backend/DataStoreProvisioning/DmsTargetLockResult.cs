// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>The outcome of <see cref="IDmsTargetEngine.AcquireTargetLockAsync"/> (D-12).</summary>
public abstract record DmsTargetLockResult
{
    /// <summary>The holder session holds the target's session-level lock; disposing the holder releases it.</summary>
    public sealed record Acquired(IDmsTargetLockHolder Holder) : DmsTargetLockResult;

    /// <summary>
    /// Another session held the lock for the whole <c>LockAcquireTimeout</c>. The current holder is neither
    /// identified nor terminated; the caller reports <c>LockTimeout</c> and DMS-1437 retries later.
    /// </summary>
    public sealed record Timeout() : DmsTargetLockResult;

    /// <summary>Lock infrastructure failure (connection, command, or unexpected lock-service result).</summary>
    public sealed record Failure(DmsProvisioningDiagnostic Diagnostic) : DmsTargetLockResult;

    /// <summary>
    /// The server definitely refused the administrative login, or the lock request, for lack of access or a privilege
    /// (PostgreSQL <c>42501</c> or class <c>28</c>; SQL Server's access-refusal numbers, a failed or disabled login
    /// included). Nothing was acquired, and retrying cannot succeed until an operator changes the credentials or a
    /// grant. The diagnostic carries the type chain and the provider code only.
    /// </summary>
    public sealed record AccessDenied(DmsProvisioningDiagnostic Diagnostic) : DmsTargetLockResult;
}
