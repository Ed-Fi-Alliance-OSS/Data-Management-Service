// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Canonical, case-sensitive set of server-generated field names excluded from
/// resource-state ETag canonicalization and not addressable by the profile DSL.
/// </summary>
internal static class ServerGeneratedFieldNames
{
    public static FrozenSet<string> Names { get; } =
        FrozenSet.Create(StringComparer.Ordinal, "id", "link", "_etag", "_lastModifiedDate");

    public static bool Contains(string name) => Names.Contains(name);
}
