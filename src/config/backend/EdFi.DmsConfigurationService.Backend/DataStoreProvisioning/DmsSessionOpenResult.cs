// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>The outcome of <see cref="IDmsTargetEngine.OpenSessionAsync"/>; connection failures propagate as exceptions.</summary>
public abstract record DmsSessionOpenResult
{
    public sealed record Opened(DmsSession Session) : DmsSessionOpenResult;

    /// <summary>The name no longer resolves to the requested instance; no session was kept open.</summary>
    public sealed record InstanceChanged() : DmsSessionOpenResult;

    public sealed record Absent() : DmsSessionOpenResult;

    /// <summary>
    /// The server definitely refused the connection for lack of a privilege (for example <c>CONNECT</c> revoked);
    /// nothing was opened. The diagnostic carries the type chain and provider code only.
    /// </summary>
    public sealed record AccessDenied(DmsProvisioningDiagnostic Diagnostic) : DmsSessionOpenResult;
}
