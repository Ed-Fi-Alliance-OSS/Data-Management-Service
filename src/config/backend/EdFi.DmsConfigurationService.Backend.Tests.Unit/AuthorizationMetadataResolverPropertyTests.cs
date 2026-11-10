// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json;
using CsCheck;
using EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.AuthorizationMetadataPropertyScenarios;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
[Category("AuthorizationMetadataProperty")]
public class Given_Generated_Authorization_Hierarchies
{
    [Test]
    public void It_matches_the_ancestor_model_under_collisions_and_catalog_or_tree_reordering(
        [Values("empty", "single", "chain", "branching", "forest")] string shape,
        [Values("defaults-only", "empty-grants", "mixed")] string mode
    )
    {
        Generate(shape, mode)
            .Sample(
                scenario =>
                {
                    var expected = Expected(scenario, Catalog);
                    var hierarchy = Build(scenario);
                    string before = JsonSerializer.Serialize(hierarchy);
                    var ordinary = AuthorizationMetadataResolver.Resolve(Catalog, hierarchy);
                    AssertReferencesAndDeduplication(ordinary);
                    SemanticPermissions(ordinary).Should().BeEquivalentTo(expected);
                    JsonSerializer.Serialize(hierarchy).Should().Be(before);

                    var comparer = new ConstantHashComparer();
                    var collisions = AuthorizationMetadataResolver.Resolve(
                        Catalog,
                        Build(scenario),
                        comparer
                    );
                    AssertReferencesAndDeduplication(collisions);
                    SemanticPermissions(collisions).Should().BeEquivalentTo(expected);
                    if (expected.Values.Any(claims => claims.Count > 0))
                    {
                        comparer.HashCalls.Should().BeGreaterThan(0);
                    }

                    var reordered = AuthorizationMetadataResolver.Resolve(
                        Catalog.Reverse(),
                        Build(scenario, reverse: true)
                    );
                    AssertReferencesAndDeduplication(reordered);
                    SemanticPermissions(reordered).Should().BeEquivalentTo(expected);
                    // IDs intentionally depend on catalog/traversal order; compare semantic permissions instead.
                    reordered.ClaimSets.Select(cs => cs.ClaimSetName).Should().Equal(Catalog.Reverse());
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine($"CsCheck 5.0.1: shape={shape}, metadata={mode}: 100 samples");
    }

    [Test]
    public void It_keeps_existing_permissions_when_an_unrelated_branch_is_added_or_changed(
        [Values("single", "chain", "branching", "forest")] string shape
    )
    {
        Generate(shape, "mixed")
            .Sample(
                scenario =>
                {
                    var expected = Expected(scenario, Catalog);
                    // A separate root precedes the existing forest and can shift every generated ID.
                    // Its grants/defaults must neither include excluded old leaves nor alter old permissions.
                    foreach (string strategy in new[] { "unrelated-first", "unrelated-changed" })
                    {
                        var extended = new Scenario([
                            new Node(
                                "unrelated",
                                -1,
                                ActionNames.Select(a => new Permission(a, [strategy])).ToImmutableArray(),
                                Catalog
                                    .Select(c => new Grant(
                                        c,
                                        ActionNames.Select(a => new Permission(a, [])).ToImmutableArray()
                                    ))
                                    .ToImmutableArray()
                            ),
                            .. scenario.Nodes.Select(n =>
                                n with
                                {
                                    Parent = n.Parent < 0 ? -1 : n.Parent + 1,
                                }
                            ),
                        ]);
                        var response = AuthorizationMetadataResolver.Resolve(Catalog, Build(extended));
                        AssertReferencesAndDeduplication(response);
                        var actual = SemanticPermissions(response);
                        actual.Should().BeEquivalentTo(Expected(extended, Catalog));
                        foreach (var claims in actual.Values)
                        {
                            claims.Remove("unrelated").Should().BeTrue();
                        }
                        actual.Should().BeEquivalentTo(expected);
                    }
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine($"CsCheck 5.0.1: unrelated branch, shape={shape}: 100 samples");
    }

    [Test]
    public void It_uses_the_nearest_grant_and_default_independently(
        [Values(0, 1, 2)] int grantDepth,
        [Values(0, 1, 2)] int defaultDepth,
        [Values(false, true)] bool hasOverride,
        [Values(false, true)] bool emptyDefault
    )
    {
        Gen.Select(Strategy, Gen.Int[0, ActionNames.Length - 1])
            .Sample(
                sample =>
                {
                    string action = ActionNames[sample.Item2];
                    string defaultOnlyAction = ActionNames[(sample.Item2 + 1) % ActionNames.Length];
                    var nodes = Enumerable
                        .Range(0, 3)
                        .Select(i => new Node($"claim-{i}", i - 1, [], []))
                        .ToArray();
                    nodes[0] = nodes[0] with
                    {
                        Defaults = [new Permission(action, ["far-default"])],
                        Grants = [new Grant("Alpha", [new Permission(action, ["far-override"])])],
                    };
                    ImmutableArray<string> defaults = emptyDefault ? [] : [$"near-default-{sample.Item1}"];
                    ImmutableArray<string> overrides = hasOverride ? [$"near-override-{sample.Item1}"] : [];
                    nodes[defaultDepth] = nodes[defaultDepth] with
                    {
                        Defaults =
                        [
                            new Permission(action, defaults),
                            new Permission(defaultOnlyAction, ["never-granted"]),
                        ],
                    };
                    nodes[grantDepth] = nodes[grantDepth] with
                    {
                        Grants = [new Grant("aLpHa", [new Permission(action, overrides)])],
                    };
                    var scenario = new Scenario(nodes.ToImmutableArray());
                    var response = AuthorizationMetadataResolver.Resolve(Catalog, Build(scenario));
                    AssertReferencesAndDeduplication(response);
                    var actual = SemanticPermissions(response);
                    actual.Should().BeEquivalentTo(Expected(scenario, Catalog));
                    // Exact single-action expectation also checks that defaults do not grant extra actions.
                    actual["Alpha"]
                        .Should()
                        .ContainSingle()
                        .Which.Should()
                        .Be(
                            new KeyValuePair<string, string>(
                                "claim-2",
                                Key([new Permission(action, hasOverride ? overrides : defaults)])
                            )
                        );
                    actual["bEtA"].Should().BeEmpty();
                    actual["missing"].Should().BeEmpty();
                },
                iter: 50,
                threads: 1,
                print: s => JsonSerializer.Serialize(new { Strategy = s.Item1, Action = s.Item2 })
            );
        TestContext.Out.WriteLine(
            $"CsCheck 5.0.1: grantDepth={grantDepth}, defaultDepth={defaultDepth}, override={hasOverride}, emptyDefault={emptyDefault}: 50 samples"
        );
    }

    [Test]
    public void It_deduplicates_equal_permissions_but_keeps_distinct_permissions_under_forced_collisions()
    {
        Strategy
            .Array[3]
            .Select(strategies => strategies.ToImmutableArray())
            .Sample(
                strategies =>
                {
                    var permissions = ActionNames
                        .Take(3)
                        .Select(
                            (name, i) =>
                                new Permission(name, [strategies[i], $"{strategies[i]}-other", strategies[i]])
                        )
                        .ToImmutableArray();
                    ImmutableArray<ImmutableArray<Permission>> variants =
                    [
                        permissions,
                        permissions
                            .Reverse()
                            .Select(p => p with { Strategies = p.Strategies.Reverse().ToImmutableArray() })
                            .ToImmutableArray(),
                        permissions.SetItem(
                            0,
                            permissions[0] with
                            {
                                Strategies = permissions[0].Strategies.Add(strategies[0]),
                            }
                        ),
                        permissions.Add(new Permission("Delete", [])),
                        permissions.SetItem(
                            0,
                            permissions[0] with
                            {
                                Strategies = permissions[0].Strategies.SetItem(0, $"{strategies[0]}-changed"),
                            }
                        ),
                        permissions.SetItem(0, permissions[0] with { Name = "Delete" }),
                    ];
                    var scenario = new Scenario(
                        variants
                            .Select(
                                (actions, i) =>
                                    new Node(
                                        $"claim-{i}",
                                        -1,
                                        [],
                                        [new Grant("alpha", actions), new Grant("BETA", actions)]
                                    )
                            )
                            .ToImmutableArray()
                    );

                    // Exercise both real hashes (equal reordered permissions must hash equally) and one forced bucket.
                    var comparer = new ConstantHashComparer();
                    var responses = new[]
                    {
                        AuthorizationMetadataResolver.Resolve(Catalog, Build(scenario)),
                        AuthorizationMetadataResolver.Resolve(Catalog, Build(scenario), comparer),
                    };
                    foreach (var response in responses)
                    {
                        AssertReferencesAndDeduplication(response);
                        SemanticPermissions(response).Should().BeEquivalentTo(Expected(scenario, Catalog));
                        foreach (var claimSet in response.ClaimSets.Take(2))
                        {
                            claimSet.Authorizations.Should().HaveCount(5);
                            claimSet
                                .Claims.Single(c => c.Name == "claim-0")
                                .AuthorizationId.Should()
                                .Be(claimSet.Claims.Single(c => c.Name == "claim-1").AuthorizationId);
                        }
                    }
                    comparer.HashCalls.Should().BeGreaterThan(0);
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine(
            "CsCheck 5.0.1: equal/reordered, strategy-multiplicity, extra-action, changed-strategy, changed-action: 100 samples"
        );
    }
}
