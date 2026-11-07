// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// A dedicated connection bound to one verified instance for its whole lifetime (a connection cannot
/// change database). The engine that opened it owns the physical connection and subclasses this type;
/// the orchestrator sees only the binding and the server session id it may have to terminate (D-22).
/// </summary>
public abstract class DmsSession(DmsInstanceIdentity instance, DmsServerSessionId serverSession)
    : IAsyncDisposable
{
    public DmsInstanceIdentity Instance { get; } = instance;

    public DmsServerSessionId ServerSession { get; } = serverSession;

    public abstract ValueTask DisposeAsync();
}
