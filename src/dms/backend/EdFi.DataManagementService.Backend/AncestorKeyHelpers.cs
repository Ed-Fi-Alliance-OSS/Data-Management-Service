// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
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
///
/// CLR-domain helpers (<c>NormalizeClrValueForIdentity</c>, <c>ExtendAncestorContextKey</c>
/// in <c>RelationalWriteMerge.cs</c>, and <c>ExtendAncestorContextKeyFromCandidate</c> in
/// <c>NoProfileSyntheticProfileAdapter.cs</c>) are deliberately NOT consolidated here because
/// the two implementations diverge on type coverage: <c>RelationalWriteMerge</c>'s version
/// handles <c>DateOnly</c>, <c>TimeOnly</c>, <c>DateTime</c>, and <c>decimal</c> branches,
/// while <c>NoProfileSyntheticProfileAdapter</c>'s version handles <c>string</c>, <c>int</c>,
/// <c>long</c>, and <c>bool</c> with lowercase <c>"true"</c>/<c>"false"</c> serialization.
/// Unifying them would be a behavior change and is out of scope for DMS-1124. A future
/// cleanup story can revisit once equivalence tests are added.
/// </remarks>
internal static class AncestorKeyHelpers
{
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
    /// NOTE: <c>ExtendAncestorContextKey</c> in <c>RelationalWriteMerge.cs</c> operates on CLR-domain
    /// values (<c>CollectionWriteCandidate.SemanticIdentityValues</c> as <c>object?</c>) and uses
    /// <c>NormalizeClrValueForIdentity</c>. For simple types (string, int, long, bool) both
    /// serializers produce the same output. A divergence is possible for decimal, double, DateOnly,
    /// DateTime, and TimeOnly — but the write-side CLR path runs only when collection-nested
    /// separate-table scopes are present, which does not occur in any current schema. Routing the
    /// write side through this helper is deferred as a follow-on cleanup (it would require
    /// <c>CollectionWriteCandidate</c> to carry <c>JsonNode?</c> identity values instead of
    /// <c>object?</c>).
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
}
