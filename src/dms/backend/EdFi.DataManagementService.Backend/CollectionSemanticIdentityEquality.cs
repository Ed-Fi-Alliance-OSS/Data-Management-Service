// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Column-aware semantic-identity equality for one collection table. Each semantic-identity
/// member compares string values with <see cref="IdentityEqualityContract.ComparerFor"/> when
/// its bound column is flagged <see cref="DbColumnModel.UsesSqlServerIdentityCollation"/>, and
/// with exact value equality otherwise. On PostgreSQL no column is flagged, so both comparers
/// keep exact equality. Every site that decides whether two collection items share a semantic
/// identity (request vs request in the flattener, request vs stored in the merges) uses this
/// type so the duplicate check is never looser or stricter than the row match.
/// </summary>
internal sealed class CollectionSemanticIdentityEquality
{
    private CollectionSemanticIdentityEquality(StringComparer?[] memberComparers)
    {
        RawValues = new RawValuesComparer(memberComparers);
        PresenceAware = new PresenceAwareComparer(memberComparers);
        HasCaseInsensitiveMembers = Array.Exists(memberComparers, comparer => comparer is not null);
    }

    /// <summary>
    /// Exact equality for every member, the semantics of a table with no flagged column.
    /// </summary>
    public static CollectionSemanticIdentityEquality Ordinal { get; } = new([]);

    /// <summary>
    /// True when at least one semantic-identity member compares with the SQL Server identity
    /// comparer, so two identities can be equal without being byte-identical.
    /// </summary>
    public bool HasCaseInsensitiveMembers { get; }

    /// <summary>
    /// Storage-collapsed equality over raw semantic identity values: a missing identity
    /// property and an explicit JSON null both collapse to <c>null</c> and compare equal, and
    /// non-string scalars use <see cref="object.Equals(object?, object?)"/> (so <c>1.0m</c>
    /// equals <c>1.00m</c>).
    /// </summary>
    public IEqualityComparer<object?[]> RawValues { get; }

    /// <summary>
    /// Presence-aware equality over <see cref="SemanticIdentityPart"/> sequences: relative
    /// paths compare ordinally, a missing part never equals an explicit null, and non-flagged
    /// members compare their canonical JSON text ordinally.
    /// </summary>
    public IEqualityComparer<ImmutableArray<SemanticIdentityPart>> PresenceAware { get; }

    /// <summary>
    /// Builds the equality for a collection table from its compiled semantic-identity bindings,
    /// in binding order.
    /// </summary>
    public static CollectionSemanticIdentityEquality For(TableWritePlan tableWritePlan)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);

        var collectionMergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{tableWritePlan.TableModel.Table}' does not have a compiled collection merge plan."
            );

        var bindings = collectionMergePlan.SemanticIdentityBindings;
        var memberComparers = new StringComparer?[bindings.Length];

        for (var index = 0; index < bindings.Length; index++)
        {
            var column = tableWritePlan.ColumnBindings[bindings[index].BindingIndex].Column;

            memberComparers[index] = column.UsesSqlServerIdentityCollation
                ? IdentityEqualityContract.ComparerFor(column)
                : null;
        }

        return new CollectionSemanticIdentityEquality(memberComparers);
    }

    private sealed class RawValuesComparer(StringComparer?[] memberComparers) : IEqualityComparer<object?[]>
    {
        public bool Equals(object?[]? left, object?[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                var comparer = ComparerAt(memberComparers, index);
                var equal =
                    comparer is not null && left[index] is string leftText && right[index] is string rightText
                        ? comparer.Equals(leftText, rightText)
                        : Equals(left[index], right[index]);

                if (!equal)
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(object?[] values)
        {
            ArgumentNullException.ThrowIfNull(values);

            HashCode hashCode = new();
            hashCode.Add(values.Length);

            for (var index = 0; index < values.Length; index++)
            {
                var comparer = ComparerAt(memberComparers, index);

                if (comparer is not null && values[index] is string text)
                {
                    hashCode.Add(comparer.GetHashCode(text));
                }
                else
                {
                    hashCode.Add(values[index]);
                }
            }

            return hashCode.ToHashCode();
        }
    }

    private sealed class PresenceAwareComparer(StringComparer?[] memberComparers)
        : IEqualityComparer<ImmutableArray<SemanticIdentityPart>>
    {
        public bool Equals(
            ImmutableArray<SemanticIdentityPart> left,
            ImmutableArray<SemanticIdentityPart> right
        )
        {
            if (left.IsDefaultOrEmpty || right.IsDefaultOrEmpty)
            {
                return left.IsDefaultOrEmpty && right.IsDefaultOrEmpty;
            }

            if (left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                var leftPart = left[index];
                var rightPart = right[index];

                if (
                    !string.Equals(leftPart.RelativePath, rightPart.RelativePath, StringComparison.Ordinal)
                    || leftPart.IsPresent != rightPart.IsPresent
                    || !ValueEquals(ComparerAt(memberComparers, index), leftPart.Value, rightPart.Value)
                )
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(ImmutableArray<SemanticIdentityPart> parts)
        {
            if (parts.IsDefaultOrEmpty)
            {
                return 0;
            }

            HashCode hashCode = new();
            hashCode.Add(parts.Length);

            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                var comparer = ComparerAt(memberComparers, index);

                hashCode.Add(part.RelativePath, StringComparer.Ordinal);
                hashCode.Add(part.IsPresent);

                if (part.Value is null)
                {
                    continue;
                }

                // TryGetValue reads the string itself; ToJsonString would escape non-ASCII
                // characters and hide case variants from the comparer.
                if (comparer is not null && TryGetString(part.Value, out var text))
                {
                    hashCode.Add(comparer.GetHashCode(text));
                }
                else
                {
                    hashCode.Add(part.Value.ToJsonString(), StringComparer.Ordinal);
                }
            }

            return hashCode.ToHashCode();
        }

        private static bool ValueEquals(StringComparer? comparer, JsonNode? left, JsonNode? right)
        {
            if (left is null || right is null)
            {
                return left is null && right is null;
            }

            if (
                comparer is not null
                && TryGetString(left, out var leftText)
                && TryGetString(right, out var rightText)
            )
            {
                return comparer.Equals(leftText, rightText);
            }

            return string.Equals(left.ToJsonString(), right.ToJsonString(), StringComparison.Ordinal);
        }

        private static bool TryGetString(JsonNode node, out string text)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var stringValue))
            {
                text = stringValue;
                return true;
            }

            text = string.Empty;
            return false;
        }
    }

    // Members beyond the table's bindings (identities from another table) use exact equality.
    private static StringComparer? ComparerAt(StringComparer?[] memberComparers, int index) =>
        index < memberComparers.Length ? memberComparers[index] : null;
}
