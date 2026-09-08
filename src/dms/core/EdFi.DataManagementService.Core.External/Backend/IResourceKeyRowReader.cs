// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Reads all rows from the dms.ResourceKey table for slow-path resource key validation.
/// </summary>
public interface IResourceKeyRowReader
{
    /// <summary>
    /// Reads all resource key rows from the dms.ResourceKey table,
    /// ordered by ResourceKeyId ascending.
    /// </summary>
    /// <param name="connectionString">The database connection string.</param>
    /// <returns>All resource key rows ordered by ResourceKeyId.</returns>
    Task<IReadOnlyList<ResourceKeyRow>> ReadResourceKeyRowsAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    );
}
