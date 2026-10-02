// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Identity;

namespace Acme.IdentityFixture;

/// <summary>
/// A tenant and route-qualifier context under the equality rules of <see cref="IdentityRequestContext"/>:
/// tenant, qualifier names and qualifier values are compared with <see cref="StringComparer.OrdinalIgnoreCase"/>,
/// a null tenant equals only null, and the qualifier set is order-independent. It copies the qualifiers
/// into its own storage, so it never relies on the comparer of the dictionary the host passed in.
/// </summary>
public sealed class FixtureContextKey : IEquatable<FixtureContextKey>
{
    private readonly Dictionary<string, string> _qualifiers;

    public FixtureContextKey(string? tenant, IEnumerable<KeyValuePair<string, string>> qualifiers)
    {
        Tenant = tenant;
        _qualifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in qualifiers)
        {
            _qualifiers[name] = value;
        }
    }

    public string? Tenant { get; }

    public static FixtureContextKey From(IdentityRequestContext context) =>
        new(context.Tenant, context.RouteQualifiers);

    public bool Equals(FixtureContextKey? other)
    {
        if (
            other is null
            || !TenantsEqual(Tenant, other.Tenant)
            || _qualifiers.Count != other._qualifiers.Count
        )
        {
            return false;
        }

        foreach ((string name, string value) in _qualifiers)
        {
            if (
                !other._qualifiers.TryGetValue(name, out string? otherValue)
                || !string.Equals(value, otherValue, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as FixtureContextKey);

    public override int GetHashCode()
    {
        int qualifierHash = 0;
        foreach ((string name, string value) in _qualifiers)
        {
            // Summed so enumeration order does not matter.
            qualifierHash += HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value)
            );
        }

        return HashCode.Combine(
            Tenant is null ? 0 : 1,
            Tenant is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Tenant),
            qualifierHash
        );
    }

    /// <summary>Tenant equality alone: OrdinalIgnoreCase, with null equal only to null.</summary>
    public static bool TenantsEqual(string? left, string? right) =>
        left is null
            ? right is null
            : right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The full ownership context of an async job: the issuing context plus the client, which is
/// compared ordinally. The trace id is deliberately absent.
/// </summary>
public sealed record FixtureJobOwner(FixtureContextKey Context, string ClientId)
{
    public static FixtureJobOwner From(IdentityRequestContext context) =>
        new(FixtureContextKey.From(context), context.ClientId);
}
