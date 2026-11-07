// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>What <see cref="IDmsTargetEngine.InspectAsync"/> finds under a name (6.1 step 6).</summary>
public abstract record DmsLiveTargetState
{
    public sealed record Absent() : DmsLiveTargetState;

    /// <summary>PostgreSQL <c>datistemplate</c>; SQL Server <c>database_id &lt;= 4</c>. Never touched.</summary>
    public sealed record Protected() : DmsLiveTargetState;

    /// <summary>
    /// PostgreSQL <c>NOT datallowconn</c>; SQL Server <c>SINGLE_USER</c>, <c>RESTRICTED_USER</c>, <c>OFFLINE</c>, or
    /// <c>RESTORING</c>. Reported for inspection, never touched.
    /// </summary>
    public sealed record Fenced(DmsInstanceIdentity Instance) : DmsLiveTargetState;

    public sealed record Present(DmsInstanceIdentity Instance, DmsDatabaseState State) : DmsLiveTargetState;
}
