// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Maps <see cref="ResourceKeyEntry"/> items from the effective schema to
/// <see cref="ResourceKeyRow"/> items used by the resource key validator.
/// </summary>
internal static class ResourceKeyRowMapper
{
    internal static List<ResourceKeyRow> ToResourceKeyRows(this IReadOnlyList<ResourceKeyEntry> entries) =>
        entries
            .Select(e => new ResourceKeyRow(
                e.ResourceKeyId,
                e.Resource.ProjectName,
                e.Resource.ResourceName,
                e.ResourceVersion
            ))
            .ToList();
}
