// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Provides access to normalized name overrides.
/// </summary>
internal interface INameOverrideProvider
{
    bool TryGetNameOverride(JsonPathExpression path, NameOverrideKind kind, out string overrideName);
}

/// <summary>
/// Resolves name overrides across two sources, failing fast when both target the same canonical path.
/// </summary>
internal sealed class CompositeNameOverrideProvider : INameOverrideProvider
{
    private readonly RelationalModelBuilderContext _primary;
    private readonly RelationalModelBuilderContext _secondary;
    private readonly string _primaryLabel;
    private readonly string _secondaryLabel;

    public CompositeNameOverrideProvider(
        RelationalModelBuilderContext primary,
        RelationalModelBuilderContext secondary
    )
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
        _primaryLabel = $"{_primary.ProjectName ?? "Unknown"}:{_primary.ResourceName ?? "Unknown"}";
        _secondaryLabel = $"{_secondary.ProjectName ?? "Unknown"}:{_secondary.ResourceName ?? "Unknown"}";

        ValidateNoConflicts();
    }

    public bool TryGetNameOverride(JsonPathExpression path, NameOverrideKind kind, out string overrideName)
    {
        if (_primary.TryGetNameOverride(path, kind, out overrideName))
        {
            return true;
        }

        if (_secondary.TryGetNameOverride(path, kind, out overrideName))
        {
            return true;
        }

        overrideName = string.Empty;
        return false;
    }

    private void ValidateNoConflicts()
    {
        if (_primary.NameOverridesByPath.Count == 0 || _secondary.NameOverridesByPath.Count == 0)
        {
            return;
        }

        var duplicates = _primary
            .NameOverridesByPath.Keys.Intersect(_secondary.NameOverridesByPath.Keys, StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (duplicates.Length == 0)
        {
            return;
        }

        var duplicateList = string.Join(", ", duplicates.Select(path => $"'{path}'"));

        throw new InvalidOperationException(
            $"relational.nameOverrides entries target the same derived element on resources "
                + $"'{_primaryLabel}' and '{_secondaryLabel}': {duplicateList}."
        );
    }
}
