// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>Await points the orchestrator passes through (D-23); each is the last await before the named mutation.</summary>
internal enum DmsProvisioningHookPoint
{
    AfterRestoreStarted,
    BeforeRename,
    BeforeMarker,
    BeforeMarkerDispatch,
    BeforeFence,
    BeforeDrop,
}

/// <summary>Client-side faults a test can ask for (D-23).</summary>
internal enum DmsProvisioningFaultPoint
{
    FailAfterMarkerExecuted,
    DropConnectionDuringRestore,
}

/// <summary>
/// Deterministic seams for tests (D-23): pause points where a test injects a foreign database or holds a
/// worker, and fault points that simulate client-side failures after the server committed. Production
/// registers <see cref="NoOpProvisioningTestHooks"/>, which returns immediately and injects nothing.
/// </summary>
internal interface IDmsProvisioningTestHooks
{
    ValueTask AtAsync(
        DmsProvisioningHookPoint point,
        DmsOperationId operation,
        CancellationToken cancellationToken
    );

    bool ShouldInjectFault(DmsProvisioningFaultPoint point, DmsOperationId operation);
}

internal sealed class NoOpProvisioningTestHooks : IDmsProvisioningTestHooks
{
    public static NoOpProvisioningTestHooks Instance { get; } = new();

    public ValueTask AtAsync(
        DmsProvisioningHookPoint point,
        DmsOperationId operation,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public bool ShouldInjectFault(DmsProvisioningFaultPoint point, DmsOperationId operation) => false;
}
