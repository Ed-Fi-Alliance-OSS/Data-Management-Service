// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>The availability of a present, unfenced database: only <see cref="Online"/> can be reconciled.</summary>
public enum DmsDatabaseState
{
    Online,

    /// <summary>SQL Server <c>RESTORING</c>: a restore that never completed.</summary>
    Restoring,

    /// <summary>Any other non-online state the engine reports.</summary>
    Other,

    /// <summary>
    /// PostgreSQL: the database is owned by a role that is neither a superuser nor the administrative login, so
    /// its objects may run that role's code when read. It is never opened, read, or changed; the operation reports
    /// it for inspection.
    /// </summary>
    UntrustedOwner,
}
