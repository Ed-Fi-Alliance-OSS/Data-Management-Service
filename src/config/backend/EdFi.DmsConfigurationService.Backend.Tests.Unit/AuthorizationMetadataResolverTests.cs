// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

public class AuthorizationMetadataResolverTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private static JsonNode ReadFixture(string name) =>
        JsonNode.Parse(
            File.ReadAllText(
                Path.Combine(
                    TestContext.CurrentContext.TestDirectory,
                    "Fixtures",
                    "AuthorizationMetadata",
                    name
                )
            )
        )!;

    private static IClaimSetRepository CreateRepository(JsonNode input)
    {
        var repository = A.Fake<IClaimSetRepository>();
        A.CallTo(() => repository.QueryClaimSet(A<ClaimSetQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success(
                    input["claimSets"]!
                        .AsArray()
                        .Select(
                            (entry, index) =>
                                new ClaimSetResponse
                                {
                                    Id = index + 1,
                                    Name = entry!["claimSetName"]!.GetValue<string>(),
                                    IsSystemReserved = entry["isSystemReserved"]!.GetValue<bool>(),
                                }
                        )
                )
            );
        return repository;
    }

    [TestFixture("collisions")]
    [TestFixture("inheritance")]
    [TestFixture("empty-actions")]
    [TestFixture("empty-hierarchy")]
    [TestFixture("empty-catalog")]
    public class Given_AuthorizationMetadata_literal_expectations(string scenario)
    {
        private JsonNode _expected = null!;
        private AuthorizationMetadataResponse _actual = null!;
        private AuthorizationMetadataResponse _repeated = null!;
        private AuthorizationMetadataResponse _shared = null!;
        private IClaimSetRepository _repository = null!;

        [SetUp]
        public async Task Setup()
        {
            var input = ReadFixture($"{scenario}-input.json");
            _expected = ReadFixture($"{scenario}-expected.json");
            var hierarchy = input["claimsHierarchy"]!.Deserialize<List<Claim>>()!;
            _repository = CreateRepository(input);
            var factory = new AuthorizationMetadataResponseFactory(_repository);
            _actual = await factory.Create(null, hierarchy);
            _repeated = await factory.Create(null, hierarchy);
            _shared = AuthorizationMetadataResolver.Resolve(
                input["claimSets"]!.AsArray().Select(entry => entry!["claimSetName"]!.GetValue<string>()),
                hierarchy
            );
        }

        [Test]
        public void It_preserves_exact_CMS_permissions_order_and_authorization_ids() =>
            JsonNode
                .DeepEquals(JsonSerializer.SerializeToNode(_actual.ClaimSets, _jsonOptions), _expected)
                .Should()
                .BeTrue();

        [Test]
        public void It_restarts_authorization_ids_for_each_response() =>
            JsonNode
                .DeepEquals(JsonSerializer.SerializeToNode(_repeated.ClaimSets, _jsonOptions), _expected)
                .Should()
                .BeTrue();

        [Test]
        public void It_resolves_the_same_literal_permissions_without_a_repository() =>
            JsonNode
                .DeepEquals(JsonSerializer.SerializeToNode(_shared.ClaimSets, _jsonOptions), _expected)
                .Should()
                .BeTrue();

        [Test]
        public void It_queries_the_complete_catalog() =>
            A.CallTo(() =>
                    _repository.QueryClaimSet(A<ClaimSetQuery>.That.Matches(q => q.Limit == int.MaxValue))
                )
                .MustHaveHappenedTwiceExactly();
    }

    [TestFixture("aLpHa", "filtered-alpha-expected.json")]
    [TestFixture("Empty", "filtered-empty-expected.json")]
    [TestFixture("missing", "empty-catalog-expected.json")]
    [TestFixture("", "empty-catalog-expected.json")]
    public class Given_AuthorizationMetadata_catalog_filter(string filter, string expectedFile)
    {
        private JsonNode _actual = null!;
        private JsonNode _expected = null!;

        [SetUp]
        public async Task Setup()
        {
            var input = ReadFixture("inheritance-input.json");
            var factory = new AuthorizationMetadataResponseFactory(CreateRepository(input));
            var result = await factory.Create(filter, input["claimsHierarchy"]!.Deserialize<List<Claim>>()!);
            _actual = JsonSerializer.SerializeToNode(result.ClaimSets, _jsonOptions)!;
            _expected = ReadFixture(expectedFile);
        }

        [Test]
        public void It_preserves_case_insensitive_filtering_and_filtered_id_allocation() =>
            JsonNode.DeepEquals(_actual, _expected).Should().BeTrue();
    }

    [TestFixture(false, "catalog unavailable")]
    [TestFixture(true, "ClaimSetQueryResult")]
    public class Given_AuthorizationMetadata_catalog_failure(bool unexpected, string message)
    {
        private Exception _exception = null!;

        [SetUp]
        public async Task Setup()
        {
            var repository = A.Fake<IClaimSetRepository>();
            ClaimSetQueryResult result = unexpected
                ? new ClaimSetQueryResult()
                : new ClaimSetQueryResult.FailureUnknown(message);
            A.CallTo(() => repository.QueryClaimSet(A<ClaimSetQuery>.Ignored)).Returns(result);
            var factory = new AuthorizationMetadataResponseFactory(repository);
            try
            {
                await factory.Create(null, []);
                Assert.Fail("The catalog failure must propagate.");
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_preserves_the_repository_error() => _exception.Message.Should().Be(message);

        [Test]
        public void It_preserves_the_exception_type() => _exception.Should().BeOfType<Exception>();
    }

    [TestFixture]
    public class Given_AuthorizationMetadata_deserialized_hierarchy
    {
        private List<Claim> _hierarchy = null!;
        private Claim _minimal = null!;

        [SetUp]
        public void Setup()
        {
            _hierarchy = ReadFixture("inheritance-input.json")["claimsHierarchy"]!.Deserialize<
                List<Claim>
            >()!;
            _minimal = JsonSerializer.Deserialize<Claim>("""{"name":"minimal"}""")!;
        }

        [Test]
        public void It_populates_parent_links_at_every_depth()
        {
            var root = _hierarchy[0];
            root.Parent.Should().BeNull();
            root.Claims.Should().AllSatisfy(child => child.Parent.Should().BeSameAs(root));
            var branch = root.Claims[1];
            branch.Claims.Should().AllSatisfy(child => child.Parent.Should().BeSameAs(branch));
        }

        [Test]
        public void It_retains_default_collections_for_omitted_fields()
        {
            _minimal.Name.Should().Be("minimal");
            _minimal.Claims.Should().BeEmpty();
            _minimal.ClaimSets.Should().BeEmpty();
            _minimal.DefaultAuthorization.Should().BeNull();
            _minimal.Parent.Should().BeNull();
        }

        [Test]
        public void It_preserves_JSON_names_and_ignores_parent_links()
        {
            var leaf = _hierarchy[0].Claims[0];
            var serialized = JsonSerializer.SerializeToNode(leaf)!;
            var expected = ReadFixture("inheritance-input.json")["claimsHierarchy"]![0]!["claims"]![
                0
            ]!.DeepClone();
            expected["claims"] = new JsonArray();
            JsonNode.DeepEquals(serialized, expected).Should().BeTrue();
        }
    }

    private sealed class ConstantHashAuthorizationComparer : IEqualityComparer<ClaimSetMetadata.Authorization>
    {
        public bool Equals(ClaimSetMetadata.Authorization? x, ClaimSetMetadata.Authorization? y) =>
            EqualityComparer<ClaimSetMetadata.Authorization>.Default.Equals(x, y);

        public int HashCalls { get; private set; }

        public int GetHashCode(ClaimSetMetadata.Authorization obj)
        {
            HashCalls++;
            return 42;
        }
    }

    [TestFixture("collisions")]
    [TestFixture("inheritance")]
    public class Given_AuthorizationMetadata_resolver_hash_collisions(string scenario)
    {
        private ConstantHashAuthorizationComparer _comparer = null!;
        private JsonNode _actual = null!;
        private JsonNode _expected = null!;

        [SetUp]
        public void Setup()
        {
            var input = ReadFixture($"{scenario}-input.json");
            _expected = ReadFixture($"{scenario}-expected.json");
            _comparer = new ConstantHashAuthorizationComparer();
            var result = AuthorizationMetadataResolver.Resolve(
                input["claimSets"]!.AsArray().Select(entry => entry!["claimSetName"]!.GetValue<string>()),
                input["claimsHierarchy"]!.Deserialize<List<Claim>>()!,
                _comparer
            );
            _actual = JsonSerializer.SerializeToNode(result.ClaimSets, _jsonOptions)!;
        }

        [Test]
        public void It_exercises_the_constant_hash_lookup() => _comparer.HashCalls.Should().BeGreaterThan(0);

        [Test]
        public void It_preserves_every_permission_reference_sequence_and_id_under_collisions() =>
            JsonNode.DeepEquals(_actual, _expected).Should().BeTrue();
    }
}
