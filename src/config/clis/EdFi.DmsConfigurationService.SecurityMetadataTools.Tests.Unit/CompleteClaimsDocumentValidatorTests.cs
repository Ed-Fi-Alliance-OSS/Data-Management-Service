// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal static class CompleteClaimsDocumentValidatorFixtures
{
    internal sealed record FailureCase(string File, string Kind, string Value, string[] Paths);

    internal static string Read(string file) =>
        System.IO.File.ReadAllText(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "CompleteClaimsDocument", file)
        );

    internal static FailureCase[] Failures =>
        JsonSerializer.Deserialize<FailureCase[]>(
            Read("failures.json"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;

    public static IEnumerable<TestFixtureData> IntegrityCases =>
        Failures.Select(testCase => new TestFixtureData(testCase.File));

    internal static List<ClaimsValidationFailure> ValidateSchema(string json) =>
        new ClaimsValidator(NullLogger<ClaimsValidator>.Instance).Validate(JsonNode.Parse(json)!);
}

[TestFixtureSource(
    typeof(CompleteClaimsDocumentValidatorFixtures),
    nameof(CompleteClaimsDocumentValidatorFixtures.IntegrityCases)
)]
public class Given_CompleteClaimsDocumentValidator_integrity_fixture(string file)
{
    private CompleteClaimsDocumentValidationResult _result = null!;
    private List<ClaimsValidationFailure> _schemaFailures = [];
    private CompleteClaimsDocumentValidatorFixtures.FailureCase _expected = null!;

    [SetUp]
    public void Setup()
    {
        string json = CompleteClaimsDocumentValidatorFixtures.Read(file);
        _expected = CompleteClaimsDocumentValidatorFixtures.Failures.Single(testCase =>
            testCase.File == file
        );
        _schemaFailures = CompleteClaimsDocumentValidatorFixtures.ValidateSchema(json);
        _result = CompleteClaimsDocumentValidator.Validate(json);
    }

    [Test]
    public void It_passes_shared_schema_validation() => _schemaFailures.Should().BeEmpty();

    [Test]
    public void It_returns_only_the_expected_structured_integrity_failure()
    {
        var failure = _result
            .Should()
            .BeOfType<CompleteClaimsDocumentValidationResult.Failure>()
            .Subject.Failures.Should()
            .ContainSingle()
            .Subject;
        failure.Kind.Should().Be(Enum.Parse<CompleteClaimsFailureKind>(_expected.Kind));
        failure.Value.Should().Be(_expected.Value);
        failure.Paths.Should().Equal(_expected.Paths);
        failure.Message.Should().Contain(_expected.Value);
    }
}

[TestFixture(0, false)]
[TestFixture(1, true)]
[TestFixture(255, true)]
[TestFixture(256, true)]
[TestFixture(257, false)]
public class Given_CompleteClaimsDocumentValidator_catalog_name_length(int length, bool valid)
{
    private CompleteClaimsDocumentValidationResult _result = null!;
    private List<ClaimsValidationFailure> _schemaFailures = [];
    private string _name = string.Empty;

    [SetUp]
    public void Setup()
    {
        // Use non-ASCII letters to ensure valid names are not limited to ASCII.
        _name = new string('é', length);
        JsonNode document = JsonNode.Parse(CompleteClaimsDocumentValidatorFixtures.Read("valid.json"))!;
        document["claimSets"]![1]!["claimSetName"] = _name;
        string json = document.ToJsonString();
        _schemaFailures = CompleteClaimsDocumentValidatorFixtures.ValidateSchema(json);
        _result = CompleteClaimsDocumentValidator.Validate(json);
    }

    [Test]
    public void It_preserves_valid_names_and_rejects_outside_the_boundaries()
    {
        if (valid)
        {
            _result
                .Should()
                .BeOfType<CompleteClaimsDocumentValidationResult.Success>()
                .Subject.ClaimSetNames.Should()
                .Equal("ReviewSet", _name);
        }
        else
        {
            var failures = _result
                .Should()
                .BeOfType<CompleteClaimsDocumentValidationResult.Failure>()
                .Subject.Failures;
            failures
                .Should()
                .OnlyContain(failure =>
                    failure.Kind
                    == (
                        length == 0
                            ? CompleteClaimsFailureKind.Schema
                            : CompleteClaimsFailureKind.InvalidCatalogName
                    )
                );
            if (length > 0)
            {
                failures.Should().ContainSingle().Subject.Paths.Should().Equal("$.claimSets[1].claimSetName");
            }
        }
    }

    [Test]
    public void It_checks_nonempty_names_after_schema_validation() =>
        _schemaFailures.Count.Should().Be(length == 0 ? 1 : 0);
}

[TestFixture("leading")]
[TestFixture("trailing")]
[TestFixture("internal")]
[TestFixture("tab")]
[TestFixture("newline")]
[TestFixture("nonbreaking-space")]
[TestFixture("only-whitespace")]
public class Given_CompleteClaimsDocumentValidator_catalog_whitespace(string scenario)
{
    private CompleteClaimsDocumentValidationResult _result = null!;
    private List<ClaimsValidationFailure> _schemaFailures = [];
    private string _name = string.Empty;

    [SetUp]
    public void Setup()
    {
        _name = scenario switch
        {
            "leading" => " Set",
            "trailing" => "Set ",
            "internal" => "Claim Set",
            "tab" => "Claim\tSet",
            "newline" => "Claim\nSet",
            "nonbreaking-space" => "Claim\u00a0Set",
            "only-whitespace" => " ",
            _ => throw new InvalidOperationException(scenario),
        };
        JsonNode document = JsonNode.Parse(CompleteClaimsDocumentValidatorFixtures.Read("valid.json"))!;
        document["claimSets"]![1]!["claimSetName"] = _name;
        string json = document.ToJsonString();
        _schemaFailures = CompleteClaimsDocumentValidatorFixtures.ValidateSchema(json);
        _result = CompleteClaimsDocumentValidator.Validate(json);
    }

    [Test]
    public void It_passes_shared_schema_validation() => _schemaFailures.Should().BeEmpty();

    [Test]
    public void It_rejects_whitespace_without_trimming_or_normalizing()
    {
        var failure = _result
            .Should()
            .BeOfType<CompleteClaimsDocumentValidationResult.Failure>()
            .Subject.Failures.Should()
            .ContainSingle()
            .Subject;
        failure.Kind.Should().Be(CompleteClaimsFailureKind.InvalidCatalogName);
        failure.Value.Should().Be(_name);
        failure.Paths.Should().Equal("$.claimSets[1].claimSetName");
    }
}

[TestFixture("malformed")]
[TestFixture("empty")]
[TestFixture("null")]
[TestFixture("scalar")]
[TestFixture("fragment")]
[TestFixture("hierarchy-array")]
[TestFixture("metadata-array")]
[TestFixture("missing-hierarchy")]
[TestFixture("missing-catalog")]
[TestFixture("invalid-nested-value")]
public class Given_CompleteClaimsDocumentValidator_invalid_shape(string scenario)
{
    private CompleteClaimsDocumentValidationResult _result = null!;

    [SetUp]
    public void Setup()
    {
        string json = scenario switch
        {
            "malformed" => "{broken",
            "empty" => "",
            "null" => "null",
            "scalar" => "42",
            "fragment" => """{"claimSetName":"ReviewSet","resourceClaims":[{"name":"urn:claim"}]}""",
            "hierarchy-array" => """[{"name":"urn:claim"}]""",
            "metadata-array" => """[{"claimSetName":"ReviewSet","claims":[],"authorizations":[]}]""",
            "missing-hierarchy" =>
                """{"claimSets":[{"claimSetName":"ReviewSet","isSystemReserved":false}]}""",
            "missing-catalog" => """{"claimsHierarchy":[{"name":"urn:claim"}]}""",
            // Would generate an integrity failure and throw while traversing if schema did not gate it.
            "invalid-nested-value" =>
                """{"claimSets":[{"claimSetName":"Invalid Name","isSystemReserved":false}],"claimsHierarchy":[{"name":"urn:claim","claims":[null]}]}""",
            _ => throw new InvalidOperationException(scenario),
        };
        _result = CompleteClaimsDocumentValidator.Validate(json);
    }

    [Test]
    public void It_rejects_input_before_integrity_traversal()
    {
        var failures = _result
            .Should()
            .BeOfType<CompleteClaimsDocumentValidationResult.Failure>()
            .Subject.Failures;
        CompleteClaimsFailureKind expected = scenario is "malformed" or "empty"
            ? CompleteClaimsFailureKind.InvalidJson
            : CompleteClaimsFailureKind.Schema;
        failures.Should().NotBeEmpty().And.OnlyContain(failure => failure.Kind == expected);
        failures.Should().OnlyContain(failure => failure.Paths.Count > 0 && failure.Message.Length > 0);
    }
}

[TestFixture]
public class Given_CompleteClaimsDocumentValidator_valid_document
{
    private CompleteClaimsDocumentValidationResult.Success _result = null!;

    [SetUp]
    public void Setup()
    {
        string json = CompleteClaimsDocumentValidatorFixtures.Read("valid.json");
        _result = CompleteClaimsDocumentValidator
            .Validate(json)
            .Should()
            .BeOfType<CompleteClaimsDocumentValidationResult.Success>()
            .Subject;
    }

    [Test]
    public void It_preserves_the_complete_catalog_and_hierarchy_reference_names()
    {
        _result.ClaimSetNames.Should().Equal("ReviewSet", "EmptySet");
        _result.Hierarchy.Single().ClaimSets.Single().Name.Should().Be("reviewset");
    }

    [Test]
    public void It_provides_parent_links_for_inherited_resolution() =>
        _result.Hierarchy.Single().Claims.Single().Parent.Should().BeSameAs(_result.Hierarchy.Single());

    [Test]
    public void It_resolves_case_only_references_and_retains_empty_catalog_entries()
    {
        var metadata = AuthorizationMetadataResolver.Resolve(_result.ClaimSetNames, _result.Hierarchy);
        string actual = JsonSerializer.Serialize(
            metadata.ClaimSets,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        JsonNode
            .DeepEquals(
                JsonNode.Parse(actual),
                JsonNode.Parse(
                    """
                    [{"claimSetName":"ReviewSet","claims":[{"name":"urn:leaf","authorizationId":1}],"authorizations":[{"id":1,"actions":[{"name":"Read","authorizationStrategies":[{"name":"NamespaceBased"}]}]}]},
                     {"claimSetName":"EmptySet","claims":[],"authorizations":[]}]
                    """
                )
            )
            .Should()
            .BeTrue();
    }
}

[TestFixture]
public class Given_CompleteClaimsDocumentValidator_distinct_uri_text
{
    private CompleteClaimsDocumentValidationResult.Success _result = null!;

    [SetUp]
    public void Setup() =>
        _result = CompleteClaimsDocumentValidator
            .Validate(CompleteClaimsDocumentValidatorFixtures.Read("distinct-uri-text.json"))
            .Should()
            .BeOfType<CompleteClaimsDocumentValidationResult.Success>()
            .Subject;

    [Test]
    public void It_keeps_all_ordinally_distinct_names_unchanged() =>
        _result
            .Hierarchy.Single()
            .Claims.Select(claim => claim.Name)
            .Should()
            .Equal(
                "http://example.org/Claim",
                "http://example.org/claim",
                "http://EXAMPLE.org/claim",
                "http://example.org:80/claim",
                "http://example.org/%63laim",
                "http://example.org/claim/"
            );
}
