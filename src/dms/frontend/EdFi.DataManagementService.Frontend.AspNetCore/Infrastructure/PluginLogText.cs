// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Renders the strings a plugin supplies so that neither the inventory event nor the registration
/// guard can be made to carry a forged log record.
/// </summary>
/// <remarks>
/// Plugin names, file names, assembly names and type names all originate in a directory a third party
/// published, and log records are line-oriented. Only control characters are stripped: a nested or
/// generic type name stays searchable in the source it came from, which a heavier escaping would
/// destroy.
/// </remarks>
internal static class PluginLogText
{
    /// <summary>A value from outside the process, rendered so it cannot forge a log record.</summary>
    public static string? Loggable(string? value) =>
        value is null ? null : string.Concat(value.Where(static character => !char.IsControl(character)));

    /// <summary>A type name for a log record, from the same metadata the loader read.</summary>
    public static string TypeName(Type type) => Loggable(type.FullName ?? type.Name)!;
}
