// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The fixed <see cref="DmsProvisioningDiagnostic.Operation"/> names an engine puts on a definite
/// command failure when the orchestrator must tell the cause apart without reading a provider code or a
/// message (6.9a): what ended a replay whose transaction was found rolled back, and the D-16 role that
/// exists but is not locked down. An engine emits exactly these spellings; the orchestrator maps them to
/// failure codes and categories (6.3 step 5, 6.10).
/// </summary>
public static class DmsEngineOperations
{
    /// <summary>The replay's session ended in doubt (a connection-class error) and its transaction was found rolled back: <c>RestoreFailed</c>, transient.</summary>
    public const string RestoreRolledBack = "RestoreIntoStaging.RolledBack";

    /// <summary>The restore bound ended the replay and its transaction was found rolled back: <c>TimedOut</c>.</summary>
    public const string RestoreTimedOut = "RestoreIntoStaging.TimedOut";

    /// <summary>The caller's cancellation ended the replay and its transaction was found rolled back: the cancellation propagates after cleanup.</summary>
    public const string RestoreCancelled = "RestoreIntoStaging.Cancelled";

    /// <summary>The D-16 role exists but is not in the locked-down state the template expects: <c>RoleStateUnsafe</c>.</summary>
    public const string RoleStateUnsafe = "EnsureEnqueueOwner.RoleStateUnsafe";
}
