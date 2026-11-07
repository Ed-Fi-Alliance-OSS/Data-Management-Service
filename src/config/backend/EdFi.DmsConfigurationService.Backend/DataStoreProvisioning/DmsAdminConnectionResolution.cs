// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>The outcome of <see cref="IDmsAdminConnectionSource.ResolveAsync"/>.</summary>
public abstract record DmsAdminConnectionResolution
{
    /// <summary>The snapshot the operation binds for its whole duration (<see cref="IDmsAdminConnectionSource.Bind"/>).</summary>
    public sealed record Resolved(DmsAdminConnectionString Connection) : DmsAdminConnectionResolution;

    /// <summary>
    /// A reference could not be resolved (<c>SecretResolutionFailed</c>). The diagnostic names the fixed
    /// operation and the exception type chain only; never a secret name, value, or resolver message.
    /// </summary>
    public sealed record Failed(DmsProvisioningDiagnostic Diagnostic) : DmsAdminConnectionResolution;
}
