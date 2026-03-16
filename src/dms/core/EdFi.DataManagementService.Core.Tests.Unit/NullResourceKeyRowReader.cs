// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Testing-only no-op implementation of IResourceKeyRowReader that always returns an empty list.
/// Used in pipeline construction tests where resource key validation is not under test.
/// </summary>
internal sealed class NullResourceKeyRowReader : IResourceKeyRowReader
{
    public Task<IReadOnlyList<ResourceKeyRow>> ReadResourceKeyRowsAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IReadOnlyList<ResourceKeyRow>>([]);
}
