// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

/// <summary>
/// An option that tells a SQL action whether to acquire a pessimistic lock
/// </summary>
public enum LockOption
{
    /// <summary>
    /// No lock required
    /// </summary>
    None,

    /// <summary>
    /// A read lock that blocks update/delete of the result rows but not other reads
    /// </summary>
    BlockUpdateDelete,
}
