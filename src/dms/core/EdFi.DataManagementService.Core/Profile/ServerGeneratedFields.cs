// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Canonical, case-sensitive set of server-generated field names that are not
/// addressable by the profile DSL. Single source of truth consumed by
/// <see cref="ProfileDataValidator"/>, <see cref="ReadableProfileProjector"/>,
/// <see cref="ProfileResponseFilter"/>, and the OpenAPI schema filter.
/// </summary>
internal static class ServerGeneratedFields
{
    public static FrozenSet<string> Names { get; } =
        FrozenSet.Create(StringComparer.Ordinal, "id", "link", "_etag", "_lastModifiedDate");

    public static bool Contains(string name) => Names.Contains(name);
}
