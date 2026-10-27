// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;
using FluentAssertions;
using MetadataAction = EdFi.DmsConfigurationService.Backend.AuthorizationMetadata.ClaimSetMetadata.Action;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal static class EffectivePermissionsFixtures
{
    internal static AuthorizationMetadataValidationResult.Success Validate(
        IEnumerable<ClaimSetMetadata> sets
    ) =>
        (AuthorizationMetadataValidationResult.Success)
            AuthorizationMetadataValidator.Validate(
                JsonSerializer.Serialize(sets, ResolvedMetadataSerializerFixtures.JsonOptions)
            );

    internal static MetadataAction Action(string name, params string[] strategies) =>
        new(
            name,
            strategies.Select(strategy => new ClaimSetMetadata.AuthorizationStrategy(strategy)).ToArray()
        );

    internal static ClaimSetMetadata Set(string name, string claim, params MetadataAction[] actions) =>
        new(name, [new(claim, 7)], [new(7, actions)]);

    internal static EffectivePermissionsComparison Compare(
        ClaimSetMetadata expected,
        ClaimSetMetadata actual
    ) => EffectivePermissionsComparer.Compare(Validate([expected]), Validate([actual]));
}

[TestFixture("en-US")]
[TestFixture("tr-TR")]
[TestFixture("sv-SE")]
public class Given_EffectivePermissionsComparer_unordered_metadata(string culture)
{
    private CultureInfo _originalCulture = null!;
    private EffectivePermissionsComparison _comparison = null!;
    private string _before = string.Empty;
    private string _after = string.Empty;

    [SetUp]
    public void Setup()
    {
        _originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        var input = ResolvedMetadataSerializerFixtures.Input().ClaimSets;
        var permuted = input
            .AsEnumerable()
            .Reverse()
            .Select(set => new ClaimSetMetadata(
                set.ClaimSetName.ToUpperInvariant(),
                set.Claims.AsEnumerable()
                    .Reverse()
                    .Select(claim => claim with { AuthorizationId = -claim.AuthorizationId - 100 })
                    .ToList(),
                set.Authorizations.AsEnumerable()
                    .Reverse()
                    .Select(authorization =>
                        authorization with
                        {
                            Id = -authorization.Id - 100,
                            Actions = authorization.Actions.Reverse().ToArray(),
                        }
                    )
                    .ToList()
            ));
        var expected = EffectivePermissionsFixtures.Validate(input);
        var actual = EffectivePermissionsFixtures.Validate(permuted);
        _before = JsonSerializer.Serialize(new[] { expected, actual });
        _comparison = EffectivePermissionsComparer.Compare(expected, actual);
        _after = JsonSerializer.Serialize(new[] { expected, actual });
    }

    [TearDown]
    public void TearDown() => CultureInfo.CurrentCulture = _originalCulture;

    [Test]
    public void It_ignores_ids_and_every_unordered_collection_and_catalog_case() =>
        _comparison.IsMatch.Should().BeTrue();

    [Test]
    public void It_has_no_differences() => _comparison.Differences.Should().BeEmpty();

    [Test]
    public void It_does_not_mutate_either_validated_input() => _after.Should().Be(_before);
}

[TestFixture]
public class Given_EffectivePermissionsComparer_exact_names
{
    [TestCase("set", "Policy", "policy", true)]
    [TestCase("set", "Policy", " Policy", false)]
    [TestCase("claim", "urn:Claim", "urn:claim", false)]
    [TestCase("claim", "https://host/claim", "https://HOST/claim", false)]
    [TestCase("claim", "urn:claim", "urn:claim/", false)]
    [TestCase("claim", "urn:claim", " urn:claim", false)]
    [TestCase("action", "Read", "read", false)]
    [TestCase("action", "Read", "Read ", false)]
    [TestCase("strategy", "Namespace", "namespace", false)]
    [TestCase("strategy", "Namespace", "Namespace ", false)]
    public void It_applies_only_the_specified_name_comparers(
        string level,
        string expectedName,
        string actualName,
        bool isMatch
    )
    {
        ClaimSetMetadata Build(string name) =>
            EffectivePermissionsFixtures.Set(
                level == "set" ? name : "Policy",
                level == "claim" ? name : "urn:claim",
                EffectivePermissionsFixtures.Action(
                    level == "action" ? name : "Read",
                    level == "strategy" ? name : "Namespace"
                )
            );
        var comparison = EffectivePermissionsFixtures.Compare(Build(expectedName), Build(actualName));
        comparison.IsMatch.Should().Be(isMatch);
        var expectedKinds = level switch
        {
            "set" => new[]
            {
                EffectivePermissionsDifferenceKind.MissingClaimSet,
                EffectivePermissionsDifferenceKind.UnexpectedClaimSet,
            },
            "claim" =>
            [
                EffectivePermissionsDifferenceKind.MissingClaim,
                EffectivePermissionsDifferenceKind.UnexpectedClaim,
            ],
            "action" =>
            [
                EffectivePermissionsDifferenceKind.MissingAction,
                EffectivePermissionsDifferenceKind.UnexpectedAction,
            ],
            _ => [EffectivePermissionsDifferenceKind.StrategySequence],
        };
        comparison
            .Differences.Select(difference => difference.Kind)
            .Should()
            .Equal(isMatch ? [] : expectedKinds);
        if (!isMatch)
        {
            comparison.Differences.SelectMany(difference => difference.Expected).Should().Equal(expectedName);
            comparison.Differences.SelectMany(difference => difference.Actual).Should().Equal(actualName);
        }
    }
}

[TestFixture]
public class Given_EffectivePermissionsComparer_strategy_sequences
{
    [TestCase(new[] { "Namespace", "Custom" }, new[] { "Custom", "Namespace" })]
    [TestCase(new[] { "Namespace" }, new[] { "Custom" })]
    [TestCase(new[] { "Namespace" }, new[] { "Namespace", "Namespace" })]
    [TestCase(new[] { "Namespace", "Custom" }, new[] { "Namespace" })]
    [TestCase(new string[] { }, new[] { "Namespace" })]
    [TestCase(new[] { "Namespace" }, new string[] { })]
    [TestCase(new[] { "A,B" }, new[] { "A", "B" })]
    public void It_preserves_exact_values_positions_multiplicity_and_context(
        string[] expected,
        string[] actual
    )
    {
        var comparison = EffectivePermissionsFixtures.Compare(
            EffectivePermissionsFixtures.Set(
                "Policy",
                "urn:claim",
                EffectivePermissionsFixtures.Action("Read", expected)
            ),
            EffectivePermissionsFixtures.Set(
                "POLICY",
                "urn:claim",
                EffectivePermissionsFixtures.Action("Read", actual)
            )
        );
        comparison.IsMatch.Should().BeFalse();
        comparison
            .Differences.Should()
            .BeEquivalentTo(
                new[]
                {
                    new EffectivePermissionsDifference(
                        EffectivePermissionsDifferenceKind.StrategySequence,
                        "Policy",
                        "urn:claim",
                        "Read",
                        expected,
                        actual
                    ),
                },
                options => options.WithStrictOrdering()
            );
    }

    [TestCase(0)]
    [TestCase(2)]
    public void It_matches_identical_empty_or_repeated_sequences(int count)
    {
        string[] sequence = Enumerable.Repeat("Namespace", count).ToArray();
        var set = EffectivePermissionsFixtures.Set(
            "Policy",
            "urn:claim",
            EffectivePermissionsFixtures.Action("Read", sequence)
        );
        EffectivePermissionsFixtures.Compare(set, set).IsMatch.Should().BeTrue();
    }
}

[TestFixture]
public class Given_EffectivePermissionsComparer_empty_and_unused_permissions
{
    [Test]
    public void It_compares_empty_catalogs() =>
        EffectivePermissionsComparer.Compare(new([]), new([])).IsMatch.Should().BeTrue();

    [Test]
    public void It_compares_empty_claim_sets() =>
        EffectivePermissionsFixtures
            .Compare(new("Empty", [], []), new("EMPTY", [], []))
            .IsMatch.Should()
            .BeTrue();

    [Test]
    public void It_distinguishes_an_empty_claim_from_an_absent_claim()
    {
        var comparison = EffectivePermissionsFixtures.Compare(
            EffectivePermissionsFixtures.Set("Policy", "urn:empty"),
            new("Policy", [], [])
        );
        comparison
            .Differences.Should()
            .BeEquivalentTo(
                new[]
                {
                    new EffectivePermissionsDifference(
                        EffectivePermissionsDifferenceKind.MissingClaim,
                        "Policy",
                        "urn:empty",
                        "",
                        ["urn:empty"],
                        []
                    ),
                }
            );
    }

    [Test]
    public void It_ignores_valid_unreferenced_authorizations()
    {
        var expected = EffectivePermissionsFixtures.Set(
            "Policy",
            "urn:claim",
            EffectivePermissionsFixtures.Action("Read", "Namespace")
        );
        var actual = expected with
        {
            Authorizations =
            [
                .. expected.Authorizations,
                new(99, [EffectivePermissionsFixtures.Action("Delete", "Unused")]),
            ],
        };
        EffectivePermissionsFixtures.Compare(expected, actual).IsMatch.Should().BeTrue();
    }

    [Test]
    public void It_ignores_nonpermission_properties()
    {
        const string metadata =
            """[{"claimSetName":"Policy","claims":[],"authorizations":[],"isSystemReserved":true}]""";
        var expected = (AuthorizationMetadataValidationResult.Success)
            AuthorizationMetadataValidator.Validate(metadata);
        var actual = (AuthorizationMetadataValidationResult.Success)
            AuthorizationMetadataValidator.Validate(metadata.Replace("true", "false"));
        EffectivePermissionsComparer.Compare(expected, actual).IsMatch.Should().BeTrue();
    }
}

[TestFixture]
public class Given_EffectivePermissionsComparer_equivalent_hierarchies
{
    private EffectivePermissionsComparison _comparison = null!;

    [SetUp]
    public void Setup()
    {
        const string inherited = """
            [{"name":"urn:parent","claimSets":[{"name":"Policy","actions":[
                {"name":"Read","authorizationStrategyOverrides":[{"name":"Namespace"}]}
            ]}],"claims":[{"name":"urn:leaf"}]}]
            """;
        const string explicitPolicy = """
            [{"name":"urn:leaf","claimSets":[{"name":"Policy","actions":[
                {"name":"Read","authorizationStrategyOverrides":[{"name":"Namespace"}]}
            ]}]}]
            """;
        var expected = AuthorizationMetadataResolver.Resolve(
            ["Policy"],
            JsonSerializer.Deserialize<List<Claim>>(inherited)!
        );
        var actual = AuthorizationMetadataResolver.Resolve(
            ["Policy"],
            JsonSerializer.Deserialize<List<Claim>>(explicitPolicy)!
        );
        expected.ClaimSets.Single().Claims.Select(claim => claim.Name).Should().Equal("urn:leaf");
        _comparison = EffectivePermissionsComparer.Compare(
            EffectivePermissionsFixtures.Validate(expected.ClaimSets),
            EffectivePermissionsFixtures.Validate(actual.ClaimSets)
        );
    }

    [Test]
    public void It_compares_effective_permissions_without_raw_hierarchy_differences() =>
        _comparison.IsMatch.Should().BeTrue();
}
