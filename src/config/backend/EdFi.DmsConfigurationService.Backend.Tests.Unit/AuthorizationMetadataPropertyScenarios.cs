// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json;
using CsCheck;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

internal static class AuthorizationMetadataPropertyScenarios
{
    internal static readonly ImmutableArray<string> Catalog = ["Alpha", "bEtA", "missing"];
    internal static readonly ImmutableArray<string> ActionNames = ["Create", "Read", "Update", "Delete"];

    internal sealed record Permission(string Name, ImmutableArray<string> Strategies);

    internal sealed record Grant(string Name, ImmutableArray<Permission> Actions);

    internal sealed record Node(
        string Name,
        int Parent,
        ImmutableArray<Permission> Defaults,
        ImmutableArray<Grant> Grants
    );

    internal sealed record Scenario(ImmutableArray<Node> Nodes);

    internal static Gen<string> Strategy =>
        Gen.Int[0, 5].Select(i => new[] { "Allow", "allow", "Relationships", "雪é", "a|b=c", "\"\\\n" }[i]);

    private static Gen<ImmutableArray<Permission>> Permissions =>
        from present in Gen.Bool.Array[4]
        from strategies in Strategy.Array[0, 3].Array[4]
        select ActionNames
            .Select((name, i) => new Permission(name, strategies[i].ToImmutableArray()))
            .Where((_, i) => present[i])
            .ToImmutableArray();

    private static Gen<Node> Metadata(string mode) =>
        from defaults in Permissions
        from alpha in Permissions
        from beta in Permissions
        from includeAlpha in Gen.Bool
        from includeBeta in Gen.Bool
        from upperCase in Gen.Bool
        select new Node(
            "",
            -1,
            defaults,
            new[]
            {
                new Grant(upperCase ? "ALPHA" : "alpha", mode == "empty-grants" ? [] : alpha),
                new Grant(upperCase ? "BETA" : "beta", mode == "empty-grants" ? [] : beta),
            }
                .Where(
                    (_, i) =>
                        mode != "defaults-only"
                        && (mode == "empty-grants" || (i == 0 ? includeAlpha : includeBeta))
                )
                .ToImmutableArray()
        );

    internal static Gen<Scenario> Generate(string shape, string mode) =>
        from count in Gen.Int[
            shape switch
            {
                "empty" => 0,
                "single" => 1,
                _ => 5,
            },
            shape switch
            {
                "empty" => 0,
                "single" => 1,
                _ => 12,
            }
        ]
        from nodes in Metadata(mode).Array[count]
        from parents in Gen.Int[0, 100].Array[count]
        select new Scenario(
            nodes
                .Select(
                    (node, i) =>
                        node with
                        {
                            Name = $"claim-{i}",
                            // Every parent precedes its child, so shrinking cannot introduce a cycle.
                            Parent = (shape, i) switch
                            {
                                (_, 0) => -1,
                                ("chain", _) => i - 1,
                                ("branching", _) => (i - 1) / 2,
                                ("forest", 1) => -1,
                                ("forest", _) => parents[i] % (i + 1) - 1,
                                _ => -1,
                            },
                        }
                )
                .ToImmutableArray()
        );

    internal static List<Claim> Build(Scenario scenario, bool reverse = false)
    {
        // Fresh mutable objects for each evaluation, including every shrink attempt.
        var claims = scenario
            .Nodes.Select(node => new Claim
            {
                Name = node.Name,
                DefaultAuthorization = new DefaultAuthorization
                {
                    Actions = Ordered(node.Defaults)
                        .Select(p => new DefaultAction
                        {
                            Name = p.Name,
                            AuthorizationStrategies = Strategies(p),
                        })
                        .ToList(),
                },
                ClaimSets = Ordered(node.Grants)
                    .Select(g => new ClaimSet
                    {
                        Name = g.Name,
                        Actions = Ordered(g.Actions)
                            .Select(p => new ClaimSetAction
                            {
                                Name = p.Name,
                                AuthorizationStrategyOverrides = Strategies(p),
                            })
                            .ToList(),
                    })
                    .ToList(),
            })
            .ToArray();

        for (int i = 0; i < claims.Length; i++)
        {
            // Assign through the setter to populate all parent links.
            claims[i].Claims = Ordered(
                    Enumerable.Range(0, claims.Length).Where(child => scenario.Nodes[child].Parent == i)
                )
                .Select(child => claims[child])
                .ToList();
        }
        return Ordered(Enumerable.Range(0, claims.Length).Where(i => scenario.Nodes[i].Parent < 0))
            .Select(i => claims[i])
            .ToList();

        IEnumerable<T> Ordered<T>(IEnumerable<T> items) => reverse ? items.Reverse() : items;
        List<AuthorizationStrategy> Strategies(Permission permission) =>
            Ordered(permission.Strategies).Select(s => new AuthorizationStrategy { Name = s }).ToList();
    }

    internal static Dictionary<string, Dictionary<string, string>> Expected(
        Scenario scenario,
        IEnumerable<string> catalog
    ) => catalog.ToDictionary(name => name, name => ExpectedClaims(scenario, name));

    private static Dictionary<string, string> ExpectedClaims(Scenario scenario, string catalogName)
    {
        var result = new Dictionary<string, string>();
        for (int leaf = 0; leaf < scenario.Nodes.Length; leaf++)
        {
            if (scenario.Nodes.Any(n => n.Parent == leaf))
            {
                continue;
            }

            // Independent model: use immutable parent indices, not production Claim.Parent or traversal.
            var ancestors = new List<Node>();
            for (int index = leaf; index >= 0; index = scenario.Nodes[index].Parent)
            {
                ancestors.Add(scenario.Nodes[index]);
            }
            var grants = ancestors
                .SelectMany(n => n.Grants)
                .Where(g => StringComparer.OrdinalIgnoreCase.Equals(g.Name, catalogName))
                .ToArray();
            if (grants.Length == 0)
            {
                continue;
            }

            var actions = new List<Permission>();
            foreach (string actionName in ActionNames)
            {
                var applicable = grants.SelectMany(g => g.Actions).Where(a => a.Name == actionName).ToArray();
                if (applicable.Length == 0)
                {
                    continue; // Defaults alone never grant an action.
                }
                var defaults = ancestors
                    .SelectMany(n => n.Defaults)
                    .Where(a => a.Name == actionName)
                    .ToArray();
                var strategies = applicable[0].Strategies;
                if (strategies.IsEmpty && defaults.Length > 0)
                {
                    strategies = defaults[0].Strategies;
                }
                actions.Add(new Permission(actionName, strategies));
            }
            result.Add(scenario.Nodes[leaf].Name, Key(actions));
        }
        return result;
    }

    // Preserve multiplicity, but ignore action/strategy order. JSON avoids delimiter ambiguities.
    // Never call production Authorization.Equals/GetHashCode to compute expectations.
    internal static string Key(IEnumerable<Permission> actions) =>
        JsonSerializer.Serialize(
            actions
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => new
                {
                    a.Name,
                    Strategies = a.Strategies.Order(StringComparer.Ordinal).ToArray(),
                })
        );

    internal static string Key(ClaimSetMetadata.Authorization authorization) =>
        Key(
            authorization.Actions.Select(a => new Permission(
                a.Name,
                a.AuthorizationStrategies.Select(s => s.Name).ToImmutableArray()
            ))
        );

    internal static Dictionary<string, Dictionary<string, string>> SemanticPermissions(
        AuthorizationMetadataResponse response
    ) =>
        response.ClaimSets.ToDictionary(
            cs => cs.ClaimSetName,
            cs =>
                cs.Claims.ToDictionary(
                    claim => claim.Name,
                    claim => Key(cs.Authorizations.Single(a => a.Id == claim.AuthorizationId))
                )
        );

    internal static void AssertReferencesAndDeduplication(AuthorizationMetadataResponse response)
    {
        var ids = response.ClaimSets.SelectMany(cs => cs.Authorizations).Select(a => a.Id).ToArray();
        ids.Should().OnlyHaveUniqueItems();
        foreach (int id in ids)
        {
            id.Should().BePositive();
        }
        foreach (var claimSet in response.ClaimSets)
        {
            claimSet.Claims.Select(c => c.Name).Should().OnlyHaveUniqueItems();
            claimSet
                .Authorizations.Select(a => a.Id)
                .Should()
                .BeEquivalentTo(
                    claimSet.Claims.Select(c => c.AuthorizationId).Distinct(),
                    "every reference must resolve within its claim set and every authorization must be used"
                );
            claimSet
                .Authorizations.Select(Key)
                .Should()
                .OnlyHaveUniqueItems(
                    "equivalent permissions must reuse one authorization even under hash collisions"
                );
        }
    }

    internal sealed class ConstantHashComparer : IEqualityComparer<ClaimSetMetadata.Authorization>
    {
        // Nullable signatures implement the BCL comparer interface.
        public bool Equals(ClaimSetMetadata.Authorization? x, ClaimSetMetadata.Authorization? y) =>
            EqualityComparer<ClaimSetMetadata.Authorization>.Default.Equals(x, y);

        public int HashCalls { get; private set; }

        public int GetHashCode(ClaimSetMetadata.Authorization obj)
        {
            HashCalls++;
            return 0;
        }
    }
}
