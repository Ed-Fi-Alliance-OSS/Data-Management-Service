// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal static class ResolvedMetadataSerializerFixtures
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string Read(string file) =>
        File.ReadAllText(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "ResolvedMetadata", file)
        );

    internal static AuthorizationMetadataResponse Input() =>
        new(JsonSerializer.Deserialize<List<ClaimSetMetadata>>(Read("input.json"), JsonOptions)!);

    internal static List<ClaimSetMetadata> Parse(string json) =>
        JsonSerializer.Deserialize<List<ClaimSetMetadata>>(json, JsonOptions)!;
}

[TestFixture]
public class Given_ResolvedMetadataSerializer_reviewed_metadata
{
    private AuthorizationMetadataResponse _input = null!;
    private string _before = string.Empty;
    private string _actual = string.Empty;
    private List<ClaimSetMetadata> _parsed = [];

    [SetUp]
    public void Setup()
    {
        _input = ResolvedMetadataSerializerFixtures.Input();
        _before = JsonSerializer.Serialize(_input, ResolvedMetadataSerializerFixtures.JsonOptions);
        _actual = ResolvedMetadataSerializer.Serialize(_input);
        _parsed = ResolvedMetadataSerializerFixtures.Parse(_actual);
    }

    [Test]
    public void It_emits_the_exact_reviewed_array_and_reference_ids() =>
        Encoding
            .UTF8.GetBytes(_actual)
            .Should()
            .Equal(
                Encoding.UTF8.GetBytes(
                    ResolvedMetadataSerializerFixtures.Read("expected.json").TrimEnd('\r', '\n')
                )
            );

    [Test]
    public void It_preserves_every_dereferenced_permission_including_empty_and_reversed_sequences()
    {
        // Compare serialized sorted actions, not the shared order-insensitive Equals implementation.
        foreach (var set in _input.ClaimSets)
        {
            var actualSet = _parsed.Single(candidate => candidate.ClaimSetName == set.ClaimSetName);
            actualSet
                .Claims.Select(claim => claim.Name)
                .Should()
                .BeEquivalentTo(set.Claims.Select(claim => claim.Name));
            foreach (var claim in set.Claims)
            {
                var originalAuthorization = set.Authorizations.Single(authorization =>
                    authorization.Id == claim.AuthorizationId
                );
                var actualClaim = actualSet.Claims.Single(candidate => candidate.Name == claim.Name);
                var actualAuthorization = actualSet.Authorizations.Single(authorization =>
                    authorization.Id == actualClaim.AuthorizationId
                );
                JsonSerializer
                    .Serialize(actualAuthorization.Actions)
                    .Should()
                    .Be(
                        JsonSerializer.Serialize(
                            originalAuthorization.Actions.OrderBy(
                                action => action.Name,
                                StringComparer.Ordinal
                            )
                        )
                    );
            }
        }
    }

    [Test]
    public void It_keeps_order_only_differences_distinct_and_reuses_identical_sequences()
    {
        var claims = _parsed.Single(set => set.ClaimSetName == "zoo").Claims;
        claims.Single(claim => claim.Name == "urn:Z-reverse").AuthorizationId.Should().Be(6);
        claims.Single(claim => claim.Name == "urn:a-forward").AuthorizationId.Should().Be(5);
        claims.Single(claim => claim.Name == "urn:duplicate").AuthorizationId.Should().Be(6);
    }

    [Test]
    public void It_preserves_the_empty_catalog_entry_and_empty_leaf_permissions()
    {
        var emptySet = _parsed.Single(set => set.ClaimSetName == "Empty");
        emptySet.Claims.Should().BeEmpty();
        emptySet.Authorizations.Should().BeEmpty();
        var zoo = _parsed.Single(set => set.ClaimSetName == "zoo");
        zoo.Claims.Single(claim => claim.Name == "urn:z-empty").AuthorizationId.Should().Be(2);
        zoo.Authorizations.Single(authorization => authorization.Id == 2).Actions.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_mutate_the_shared_response() =>
        JsonSerializer.Serialize(_input, ResolvedMetadataSerializerFixtures.JsonOptions).Should().Be(_before);

    [Test]
    public void It_is_idempotent() => ResolvedMetadataSerializer.Serialize(new(_parsed)).Should().Be(_actual);
}

[TestFixture("en-US")]
[TestFixture("tr-TR")]
[TestFixture("sv-SE")]
public class Given_ResolvedMetadataSerializer_unordered_collections(string culture)
{
    private CultureInfo _originalCulture = null!;
    private string _expected = string.Empty;

    [SetUp]
    public void Setup()
    {
        _originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        _expected = ResolvedMetadataSerializerFixtures.Read("expected.json").TrimEnd('\r', '\n');
    }

    [TearDown]
    public void TearDown() => CultureInfo.CurrentCulture = _originalCulture;

    [TestCase(1)]
    [TestCase(23)]
    [TestCase(42)]
    public void It_ignores_collection_permutations_and_incoming_ids(int seed)
    {
        var random = new Random(seed);
        var input = ResolvedMetadataSerializerFixtures.Input();
        var permuted = input.ClaimSets.Select(set =>
        {
            var ids = set
                .Authorizations.Select(
                    (authorization, index) => (authorization.Id, NewId: 1000 + seed + index * 7)
                )
                .ToDictionary(pair => pair.Id, pair => pair.NewId);
            var authorizations = set.Authorizations.Select(authorization =>
                authorization with
                {
                    Id = ids[authorization.Id],
                    Actions = Shuffle(authorization.Actions),
                }
            );
            return new ClaimSetMetadata(
                set.ClaimSetName,
                Shuffle(
                        set.Claims.Select(claim =>
                            claim with
                            {
                                AuthorizationId = ids[claim.AuthorizationId],
                            }
                        )
                    )
                    .ToList(),
                Shuffle(authorizations).ToList()
            );
        });
        var actual = ResolvedMetadataSerializer.Serialize(new(Shuffle(permuted)));
        Encoding.UTF8.GetBytes(actual).Should().Equal(Encoding.UTF8.GetBytes(_expected));

        T[] Shuffle<T>(IEnumerable<T> items)
        {
            T[] values = items.ToArray();
            random.Shuffle(values);
            return values;
        }
    }
}

[TestFixture]
public class Given_ResolvedMetadataSerializer_sequence_and_ordinal_keys
{
    private List<ClaimSetMetadata> _actual = [];

    [SetUp]
    public void Setup()
    {
        string[][] sequences =
        [
            ["a"],
            ["Z", "a"],
            ["Z", "Z", "a"],
            ["Z"],
            [],
            ["Z,a"],
            ["Z", "a", "a"],
        ];
        var authorizations = sequences
            .Select(
                (sequence, index) =>
                    new ClaimSetMetadata.Authorization(
                        100 + index,
                        [
                            new(
                                "Read",
                                sequence
                                    .Select(name => new ClaimSetMetadata.AuthorizationStrategy(name))
                                    .ToArray()
                            ),
                        ]
                    )
            )
            .ToList();
        var claims = authorizations
            .Select(authorization => new ClaimSetMetadata.Claim($"urn:{authorization.Id}", authorization.Id))
            .ToList();
        _actual = ResolvedMetadataSerializerFixtures.Parse(
            ResolvedMetadataSerializer.Serialize(new([new("Sequences", claims, authorizations)]))
        );
    }

    [Test]
    public void It_uses_ordinal_names_sequence_positions_lengths_and_multiplicity()
    {
        var set = _actual.Single();
        set.Authorizations.Select(authorization => authorization.Id).Should().Equal(1, 2, 3, 4, 5, 6, 7);
        // Empty, Z, Z/Z/a, Z/a, Z/a/a, Z,a (one strategy), a.
        set.Claims.Select(claim => claim.AuthorizationId).Should().Equal(7, 4, 3, 2, 1, 6, 5);
    }

    [Test]
    public void It_retains_exact_strategy_values_in_each_position()
    {
        var strategies = _actual
            .Single()
            .Authorizations.Select(authorization =>
                JsonSerializer.Serialize(
                    authorization.Actions.Single().AuthorizationStrategies.Select(strategy => strategy.Name)
                )
            );
        strategies
            .Should()
            .Equal(
                "[]",
                "[\"Z\"]",
                "[\"Z\",\"Z\",\"a\"]",
                "[\"Z\",\"a\"]",
                "[\"Z\",\"a\",\"a\"]",
                "[\"Z,a\"]",
                "[\"a\"]"
            );
    }
}

[TestFixture]
public class Given_ResolvedMetadataSerializer_shared_resolver_result
{
    private AuthorizationMetadataResponse _input = null!;
    private string _before = string.Empty;
    private List<ClaimSetMetadata> _actual = [];

    [SetUp]
    public void Setup()
    {
        List<Claim> hierarchy = JsonSerializer.Deserialize<List<Claim>>(
            """
            [{"name":"urn:leaf","claimSets":[{"name":"Zulu","actions":[
              {"name":"Read","authorizationStrategyOverrides":[{"name":"Second"},{"name":"First"}]},
              {"name":"Create","authorizationStrategyOverrides":[]}
            ]}]}]
            """
        )!;
        _input = AuthorizationMetadataResolver.Resolve(["Zulu", "Empty"], hierarchy);
        _before = JsonSerializer.Serialize(_input, ResolvedMetadataSerializerFixtures.JsonOptions);
        _actual = ResolvedMetadataSerializerFixtures.Parse(ResolvedMetadataSerializer.Serialize(_input));
    }

    [Test]
    public void It_canonicalizes_a_real_resolver_result_only_at_the_cli_boundary()
    {
        _actual.Select(set => set.ClaimSetName).Should().Equal("Empty", "Zulu");
        var authorization = _actual[1].Authorizations.Should().ContainSingle().Subject;
        authorization.Id.Should().Be(1);
        authorization.Actions.Select(action => action.Name).Should().Equal("Create", "Read");
        authorization
            .Actions[1]
            .AuthorizationStrategies.Select(strategy => strategy.Name)
            .Should()
            .Equal("Second", "First");
    }

    [Test]
    public void It_leaves_shared_catalog_action_order_and_ids_unchanged()
    {
        JsonSerializer.Serialize(_input, ResolvedMetadataSerializerFixtures.JsonOptions).Should().Be(_before);
        _input.ClaimSets.Select(set => set.ClaimSetName).Should().Equal("Zulu", "Empty");
        _input
            .ClaimSets[0]
            .Authorizations.Single()
            .Actions.Select(action => action.Name)
            .Should()
            .Equal("Read", "Create");
        _input.ClaimSets[0].Claims.Single().AuthorizationId.Should().Be(1);
    }
}

[TestFixture]
public class Given_ResolvedMetadataSerializer_empty_response
{
    private string _actual = string.Empty;

    [SetUp]
    public void Setup() => _actual = ResolvedMetadataSerializer.Serialize(new([]));

    [Test]
    public void It_emits_an_empty_metadata_array() => _actual.Should().Be("[]");
}
