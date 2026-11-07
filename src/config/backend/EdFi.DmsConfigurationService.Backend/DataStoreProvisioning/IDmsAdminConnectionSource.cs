// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The administrative connection string of 4.3 (<c>AdminConnectionString</c>) with its
/// <c>${secret:&lt;name&gt;}</c> references resolved. Readiness resolves it first (7.3 step 1), and every
/// create and delete resolves it again before the target lock (6.1 step 3), so a rotated secret reaches the
/// next operation through the secret cache without a restart. Each resolution is an immutable snapshot that
/// the caller binds to its own asynchronous flow for the whole operation, cleanup and recovery included; the
/// engine adapter connects with the snapshot bound to the flow it runs in, so overlapping operations never
/// see each other's resolution. The value is never logged.
/// </summary>
public interface IDmsAdminConnectionSource
{
    /// <summary>
    /// Resolves every secret reference now, bounded by <c>SecretsSettings:ResolveTimeoutSeconds</c>, and returns
    /// the snapshot or the failure. Nothing is bound or shared by this call. Caller cancellation propagates.
    /// </summary>
    Task<DmsAdminConnectionResolution> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Binds <paramref name="connection"/> to the calling asynchronous flow until the returned scope is disposed,
    /// which restores whatever was bound before. Call it in the operation's own method, not in a helper it
    /// awaits: a binding made inside an awaited method ends when that method returns.
    /// </summary>
    IDisposable Bind(DmsAdminConnectionString connection);

    /// <summary>
    /// The value bound to the calling asynchronous flow. Throws <see cref="InvalidOperationException"/> when
    /// none is bound, so no connection is ever opened with another operation's resolution or a stale one.
    /// </summary>
    string Current { get; }
}
