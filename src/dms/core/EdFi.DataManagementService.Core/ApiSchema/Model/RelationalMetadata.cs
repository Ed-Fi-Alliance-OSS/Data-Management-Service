// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.ApiSchema.Model;

/// <summary>
/// Optional relational metadata for deterministic physical naming overrides
/// </summary>
internal class RelationalMetadata(JsonNode _relationalNode)
{
    private readonly Lazy<string?> _rootTableNameOverride = new(() =>
    {
        return _relationalNode["rootTableNameOverride"]?.GetValue<string>();
    });

    /// <summary>
    /// Optional override for the root table name
    /// </summary>
    public string? RootTableNameOverride => _rootTableNameOverride.Value;

    private readonly Lazy<IReadOnlyDictionary<string, string>> _nameOverrides = new(() =>
    {
        var overridesNode = _relationalNode["nameOverrides"];
        if (overridesNode == null)
        {
            return new Dictionary<string, string>().AsReadOnly();
        }

        return overridesNode
            .AsObject()
            .ToDictionary(
                kvp => kvp.Key,
                kvp =>
                    kvp.Value?.GetValue<string>()
                    ?? throw new InvalidOperationException($"Name override for '{kvp.Key}' has null value")
            )
            .AsReadOnly();
    });

    /// <summary>
    /// Maps JSONPath → physical base name overrides
    /// Keys: restricted JSONPath (e.g., "$.property" or "$.array[*]")
    /// Values: physical base name to use
    /// </summary>
    public IReadOnlyDictionary<string, string> NameOverrides => _nameOverrides.Value;
}
