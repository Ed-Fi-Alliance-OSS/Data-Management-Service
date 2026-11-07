// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The administrative connection string of 4.3 (<c>AdminConnectionString</c>) with its
/// <c>${secret:&lt;name&gt;}</c> references resolved. Readiness resolves it first (7.3 step 1), and every
/// create and delete resolves it again before the target lock (6.1 step 3), so a rotated secret reaches the
/// next operation through the secret cache without a restart. The engine adapter opens every connection
/// with <see cref="Current"/>. The value is never logged.
/// </summary>
public interface IDmsAdminConnectionSource
{
    /// <summary>
    /// Resolves every secret reference now, bounded by <c>SecretsSettings:ResolveTimeoutSeconds</c>. A
    /// success replaces <see cref="Current"/>; a failure leaves it as it was. Caller cancellation propagates.
    /// </summary>
    Task<DmsAdminConnectionResolution> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The most recently resolved value. Throws <see cref="InvalidOperationException"/> before the first
    /// successful resolution: readiness cannot become <c>Ready</c> without one, so no engine call reaches here
    /// first.
    /// </summary>
    string Current { get; }
}
