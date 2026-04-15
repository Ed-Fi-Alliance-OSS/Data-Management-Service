// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Shared JSON-domain helpers for building ancestor-collection-instance keys used by both
/// <see cref="RelationalWriteMergeSynthesizer"/> and
/// <c>ProfileWriteContractValidator</c>.
/// </summary>
/// <remarks>
/// Only JSON-domain helpers live here (<see cref="JsonNode"/>-based identity values,
/// <see cref="ScopeInstanceAddress"/>-based ancestor keys).
/// </remarks>
internal static class AncestorKeyHelpers
{
    internal static JsonNode? ConvertClrValueToSemanticIdentityJsonNode(
        object? clrValue,
        RelationalScalarType? scalarType = null
    ) =>
        scalarType?.Kind switch
        {
            ScalarKind.String => JsonValue.Create((string?)clrValue),
            ScalarKind.Int32 => clrValue is null ? null : JsonValue.Create((int)clrValue),
            ScalarKind.Int64 => clrValue is null ? null : JsonValue.Create((long)clrValue),
            ScalarKind.Decimal => clrValue is null ? null : JsonValue.Create((decimal)clrValue),
            ScalarKind.Boolean => clrValue is null ? null : JsonValue.Create((bool)clrValue),
            ScalarKind.Date => clrValue switch
            {
                null => null,
                DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                DateTime dt => JsonValue.Create(
                    DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ),
                _ => JsonValue.Create(clrValue.ToString()!),
            },
            ScalarKind.DateTime => clrValue switch
            {
                null => null,
                DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
                DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
                _ => JsonValue.Create(clrValue.ToString()!),
            },
            ScalarKind.Time => clrValue switch
            {
                null => null,
                TimeOnly t => JsonValue.Create(t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
                TimeSpan ts => JsonValue.Create(
                    new TimeOnly(ts.Ticks).ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                ),
                _ => JsonValue.Create(clrValue.ToString()!),
            },
            _ => clrValue switch
            {
                null => null,
                string s => JsonValue.Create(s),
                int n => JsonValue.Create(n),
                long n => JsonValue.Create(n),
                decimal dec => JsonValue.Create(dec),
                double d => JsonValue.Create(d),
                bool b => JsonValue.Create(b),
                DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                TimeOnly t => JsonValue.Create(t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
                DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
                DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
                TimeSpan ts => JsonValue.Create(
                    new TimeOnly(ts.Ticks).ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                ),
                _ => JsonValue.Create(clrValue.ToString()!),
            },
        };

    /// <summary>
    /// Extracts the raw string value from a JsonNode without JSON-encoding artifacts.
    /// JsonNode.ToString() on a JsonValue wrapping a string produces quoted JSON (e.g. "\"foo\""),
    /// so this method extracts the underlying value directly.
    /// </summary>
    internal static string ExtractJsonNodeStringValue(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var s))
            {
                return s;
            }
            if (jsonValue.TryGetValue<long>(out var l))
            {
                return l.ToString(CultureInfo.InvariantCulture);
            }
            if (jsonValue.TryGetValue<int>(out var i))
            {
                return i.ToString(CultureInfo.InvariantCulture);
            }
            if (jsonValue.TryGetValue<decimal>(out var dec))
            {
                return dec.ToString(CultureInfo.InvariantCulture);
            }
            if (jsonValue.TryGetValue<double>(out var d))
            {
                return d.ToString(CultureInfo.InvariantCulture);
            }
            if (jsonValue.TryGetValue<bool>(out var b))
            {
                return b ? "True" : "False";
            }
        }

        return node.ToString();
    }

    /// <summary>
    /// Canonical ancestor key builder. Operates on <see cref="AncestorCollectionInstance"/> values
    /// (JSON-domain, <see cref="SemanticIdentityPart.Value"/> is <see cref="JsonNode?"/>) and uses
    /// <see cref="ExtractJsonNodeStringValue"/> for serialization.
    ///
    /// This is the single implementation shared by all three JsonNode-based key-building sites:
    /// <see cref="BuildAncestorKeyFromScopeInstanceAddress"/>,
    /// <c>MergeScopeLookup.BuildAncestorKey</c>, and
    /// <c>MergeCollectionLookup.BuildAncestorKeyFromAddress</c>.
    ///
    /// </summary>
    internal static string BuildAncestorKeyFromInstances(ImmutableArray<AncestorCollectionInstance> ancestors)
    {
        if (ancestors.IsEmpty)
        {
            return "";
        }

        var sb = new System.Text.StringBuilder();

        foreach (var ancestor in ancestors)
        {
            if (sb.Length > 0)
            {
                sb.Append('\0');
            }

            sb.Append(ancestor.JsonScope);

            foreach (var part in ancestor.SemanticIdentityInOrder)
            {
                sb.Append('\0');
                sb.Append(part.IsPresent ? '1' : '0');
                sb.Append('\0');
                sb.Append(ExtractJsonNodeStringValue(part.Value));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the ancestor context key from a ScopeInstanceAddress for use in the
    /// visitedScopeKeys canonical key format. Delegates to
    /// <see cref="BuildAncestorKeyFromInstances"/> so all JSON-domain key sites share one
    /// implementation.
    /// </summary>
    internal static string BuildAncestorKeyFromScopeInstanceAddress(ScopeInstanceAddress address) =>
        BuildAncestorKeyFromInstances(address.AncestorCollectionInstances);

    internal static string ExtendAncestorKey(
        string currentKey,
        string collectionJsonScope,
        IReadOnlyList<JsonNode?> semanticIdentityValues,
        IReadOnlyList<bool> semanticIdentityPresenceFlags
    )
    {
        if (semanticIdentityValues.Count != semanticIdentityPresenceFlags.Count)
        {
            throw new InvalidOperationException(
                $"Semantic identity values ({semanticIdentityValues.Count}) and presence flags ({semanticIdentityPresenceFlags.Count}) must have equal length."
            );
        }

        var sb = new System.Text.StringBuilder();

        if (currentKey.Length > 0)
        {
            sb.Append(currentKey);
            sb.Append('\0');
        }

        sb.Append(collectionJsonScope);

        for (var i = 0; i < semanticIdentityValues.Count; i++)
        {
            sb.Append('\0');
            sb.Append(semanticIdentityPresenceFlags[i] ? '1' : '0');
            sb.Append('\0');
            sb.Append(ExtractJsonNodeStringValue(semanticIdentityValues[i]));
        }

        return sb.ToString();
    }
}
