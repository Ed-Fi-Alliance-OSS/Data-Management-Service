// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Globalization;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Assigns each readable profile a stable, compile-time ordinal ("profileCode") within a MappingSet.
/// The ordinal is the profile name's ordinal-sort position; stability is only required within a
/// schemaEpoch, and any profile redefinition rotates schemaEpoch, so the ordinal is unambiguous.
/// </summary>
public sealed class ProfileVariantCodeRegistry
{
    private readonly FrozenDictionary<string, string> _codeByName;

    public ProfileVariantCodeRegistry(IEnumerable<string> profileNames)
    {
        ArgumentNullException.ThrowIfNull(profileNames);

        var ordered = profileNames
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        _codeByName = ordered
            .Select((name, index) => (name, code: index.ToString(CultureInfo.InvariantCulture)))
            .ToFrozenDictionary(entry => entry.name, entry => entry.code, StringComparer.Ordinal);
    }

    public string CodeFor(string? profileName)
    {
        if (profileName is null)
        {
            return VariantKey.NoProfileCode;
        }

        return _codeByName.TryGetValue(profileName, out var code)
            ? code
            : throw new KeyNotFoundException(
                $"Profile '{profileName}' is not registered for this MappingSet."
            );
    }
}
