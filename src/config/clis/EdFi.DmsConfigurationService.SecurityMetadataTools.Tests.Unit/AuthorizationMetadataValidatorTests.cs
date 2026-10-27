// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal static class AuthorizationMetadataValidatorFixtures
{
    internal const string ValidJson = """
        [{
          "claimSetName": "School",
          "claims": [{"name": "urn:student", "authorizationId": 7}],
          "authorizations": [{
            "id": 7,
            "actions": [{"name": "Read", "authorizationStrategies": [{"name": "Second"}, {"name": "First"}, {"name": "Second"}]}]
          }]
        }]
        """;

    internal static JsonArray Valid() => JsonNode.Parse(ValidJson)!.AsArray();

    internal static AuthorizationMetadataValidationResult.Failure Failure(string json) =>
        AuthorizationMetadataValidator
            .Validate(json)
            .Should()
            .BeOfType<AuthorizationMetadataValidationResult.Failure>()
            .Subject;

    internal static AuthorizationMetadataValidationResult.Success Success(string json) =>
        AuthorizationMetadataValidator
            .Validate(json)
            .Should()
            .BeOfType<AuthorizationMetadataValidationResult.Success>()
            .Subject;
}

[TestFixture]
public class Given_AuthorizationMetadataValidator_invalid_fields
{
    private JsonArray _input = null!;

    [SetUp]
    public void Setup() => _input = AuthorizationMetadataValidatorFixtures.Valid();

    private static IEnumerable<TestCaseData> InvalidFields()
    {
        (string Pointer, string Path, string Expected, string WrongValue)[] fields =
        [
            ("0/claimSetName", "$[0].claimSetName", "a string", "42"),
            ("0/claims", "$[0].claims", "an array", "{}"),
            ("0/authorizations", "$[0].authorizations", "an array", "{}"),
            ("0/claims/0/name", "$[0].claims[0].name", "a string", "false"),
            ("0/claims/0/authorizationId", "$[0].claims[0].authorizationId", "a 32-bit integer", "\"7\""),
            ("0/authorizations/0/id", "$[0].authorizations[0].id", "a 32-bit integer", "1.5"),
            ("0/authorizations/0/actions", "$[0].authorizations[0].actions", "an array", "\"Read\""),
            ("0/authorizations/0/actions/0/name", "$[0].authorizations[0].actions[0].name", "a string", "[]"),
            (
                "0/authorizations/0/actions/0/authorizationStrategies",
                "$[0].authorizations[0].actions[0].authorizationStrategies",
                "an array",
                "{}"
            ),
            (
                "0/authorizations/0/actions/0/authorizationStrategies/0/name",
                "$[0].authorizations[0].actions[0].authorizationStrategies[0].name",
                "a string",
                "{}"
            ),
        ];
        foreach (var field in fields)
        {
            foreach (string variant in new[] { "missing", "null", "wrong-type" })
            {
                yield return new TestCaseData(
                    field.Pointer,
                    field.Path,
                    field.Expected,
                    variant == "null" ? "null" : field.WrongValue,
                    variant
                ).SetName($"It_rejects_{variant}_{field.Pointer.Replace('/', '_')}");
            }
        }
    }

    [TestCaseSource(nameof(InvalidFields))]
    public void It_rejects_each_missing_null_or_wrong_type_field(
        string pointer,
        string path,
        string expected,
        string replacement,
        string variant
    )
    {
        string[] segments = pointer.Split('/');
        JsonNode parent = _input;
        foreach (string segment in segments[..^1])
        {
            parent = parent is JsonArray array
                ? array[int.Parse(segment, CultureInfo.InvariantCulture)]!
                : parent[segment]!;
        }
        string name = segments[^1];
        if (variant == "missing")
        {
            parent.AsObject().Remove(name);
        }
        else
        {
            parent[name] = JsonNode.Parse(replacement);
        }

        var failure = AuthorizationMetadataValidatorFixtures
            .Failure(_input.ToJsonString())
            .Failures.Should()
            .ContainSingle()
            .Subject;
        failure.Kind.Should().Be(AuthorizationMetadataFailureKind.InvalidShape);
        failure.Paths.Should().Equal(path);
        failure
            .Message.Should()
            .Be(variant == "missing" ? $"Required property '{name}' is missing." : $"Expected {expected}.");
    }

    [TestCase("id", "2147483648")]
    [TestCase("id", "-2147483649")]
    [TestCase("id", "false")]
    [TestCase("id", "1e100")]
    [TestCase("authorizationId", "2147483648")]
    [TestCase("authorizationId", "1.5")]
    [TestCase("authorizationId", "true")]
    public void It_rejects_non_int32_ids(string property, string value)
    {
        string collection = property == "id" ? "authorizations" : "claims";
        _input[0]![collection]![0]![property] = JsonNode.Parse(value);
        var failure = AuthorizationMetadataValidatorFixtures
            .Failure(_input.ToJsonString())
            .Failures.Should()
            .ContainSingle()
            .Subject;
        failure.Kind.Should().Be(AuthorizationMetadataFailureKind.InvalidShape);
        failure.Paths.Should().Equal($"$[0].{collection}[0].{property}");
    }

    [Test]
    public void It_does_not_treat_wrong_case_properties_as_required_properties()
    {
        _input[0]!.AsObject().Remove("claims");
        _input[0]!["Claims"] = new JsonArray();
        AuthorizationMetadataValidatorFixtures
            .Failure(_input.ToJsonString())
            .Failures.Single()
            .Paths.Should()
            .Equal("$[0].claims");
    }
}

[TestFixture]
public class Given_AuthorizationMetadataValidator_invalid_structure
{
    [TestCase("null", "$")]
    [TestCase("{}", "$")]
    [TestCase("\"text\"", "$")]
    [TestCase("123", "$")]
    [TestCase("true", "$")]
    [TestCase("[null]", "$[0]")]
    [TestCase("[[]]", "$[0]")]
    [TestCase("[false]", "$[0]")]
    [TestCase("[{\"claimSetName\":\"School\",\"claims\":[null],\"authorizations\":[]}]", "$[0].claims[0]")]
    [TestCase("[{\"claimSetName\":\"School\",\"claims\":[7],\"authorizations\":[]}]", "$[0].claims[0]")]
    [TestCase(
        "[{\"claimSetName\":\"School\",\"claims\":[],\"authorizations\":[null]}]",
        "$[0].authorizations[0]"
    )]
    [TestCase(
        "[{\"claimSetName\":\"School\",\"claims\":[],\"authorizations\":[[]]}]",
        "$[0].authorizations[0]"
    )]
    [TestCase(
        "[{\"claimSetName\":\"School\",\"claims\":[],\"authorizations\":[{\"id\":7,\"actions\":[null]}]}]",
        "$[0].authorizations[0].actions[0]"
    )]
    [TestCase(
        "[{\"claimSetName\":\"School\",\"claims\":[],\"authorizations\":[{\"id\":7,\"actions\":[\"Read\"]}]}]",
        "$[0].authorizations[0].actions[0]"
    )]
    [TestCase(
        "[{\"claimSetName\":\"School\",\"claims\":[],\"authorizations\":[{\"id\":7,\"actions\":[{\"name\":\"Read\",\"authorizationStrategies\":[null]}]}]}]",
        "$[0].authorizations[0].actions[0].authorizationStrategies[0]"
    )]
    [TestCase(
        "[{\"claimSetName\":\"School\",\"claims\":[],\"authorizations\":[{\"id\":7,\"actions\":[{\"name\":\"Read\",\"authorizationStrategies\":[\"First\"]}]}]}]",
        "$[0].authorizations[0].actions[0].authorizationStrategies[0]"
    )]
    public void It_rejects_incorrect_roots_and_null_or_wrong_type_elements(string json, string path)
    {
        var failure = AuthorizationMetadataValidatorFixtures
            .Failure(json)
            .Failures.Should()
            .ContainSingle()
            .Subject;
        failure.Kind.Should().Be(AuthorizationMetadataFailureKind.InvalidShape);
        failure.Paths.Should().Equal(path);
    }

    [TestCase("")]
    [TestCase("[")]
    [TestCase("[] trailing")]
    [TestCase("[{},]")]
    public void It_returns_a_structured_failure_for_invalid_json(string json)
    {
        var failure = AuthorizationMetadataValidatorFixtures
            .Failure(json)
            .Failures.Should()
            .ContainSingle()
            .Subject;
        failure.Kind.Should().Be(AuthorizationMetadataFailureKind.InvalidJson);
        failure.Paths.Should().Equal("$");
    }

    [Test]
    public void It_checks_the_entire_array_and_reports_shape_errors_in_fixed_field_order()
    {
        string json = """[{"authorizations":null,"claims":null},null,{}]""";
        var failures = AuthorizationMetadataValidatorFixtures.Failure(json).Failures;
        failures
            .SelectMany(failure => failure.Paths)
            .Should()
            .Equal(
                "$[0].claimSetName",
                "$[0].claims",
                "$[0].authorizations",
                "$[1]",
                "$[2].claimSetName",
                "$[2].claims",
                "$[2].authorizations"
            );
        AuthorizationMetadataValidatorFixtures
            .Failure(json)
            .Failures.Should()
            .BeEquivalentTo(failures, options => options.WithStrictOrdering());
    }

    [TestCase("claimSetName", "School", "$[0]")]
    [TestCase("name", "urn:student", "$[0].claims[0]")]
    [TestCase("id", "7", "$[0].authorizations[0]")]
    [TestCase("name", "Read", "$[0].authorizations[0].actions[0]")]
    [TestCase("name", "First", "$[0].authorizations[0].actions[0].authorizationStrategies[1]")]
    public void It_rejects_duplicate_json_properties_at_each_object_level(
        string property,
        string value,
        string path
    )
    {
        string literal = property == "id" ? value : $"\"{value}\"";
        string propertyJson = $"\"{property}\": {literal}";
        string json = AuthorizationMetadataValidatorFixtures.ValidJson.Replace(
            propertyJson,
            $"{propertyJson}, {propertyJson}",
            StringComparison.Ordinal
        );
        var failure = AuthorizationMetadataValidatorFixtures
            .Failure(json)
            .Failures.Should()
            .ContainSingle()
            .Subject;
        failure.Kind.Should().Be(AuthorizationMetadataFailureKind.DuplicateProperty);
        failure.Paths.Should().Equal(path);
        failure.Value.Should().Be(property);
    }
}

[TestFixture]
public class Given_AuthorizationMetadataValidator_ambiguous_permissions
{
    private JsonArray _input = null!;

    [SetUp]
    public void Setup() => _input = AuthorizationMetadataValidatorFixtures.Valid();

    [TestCase("School")]
    [TestCase("sChOoL")]
    public void It_rejects_duplicate_catalog_names(string name)
    {
        _input.Add(
            new JsonObject
            {
                ["claimSetName"] = name,
                ["claims"] = new JsonArray(),
                ["authorizations"] = new JsonArray(),
            }
        );
        AssertDuplicate(
            AuthorizationMetadataFailureKind.DuplicateClaimSetName,
            name,
            "claim set name",
            "$[0].claimSetName",
            "$[1].claimSetName"
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_rejects_duplicate_authorization_ids_even_when_unreferenced(bool conflicting)
    {
        _input[0]!["claims"] = new JsonArray();
        var authorizations = _input[0]!["authorizations"]!.AsArray();
        var duplicate = authorizations[0]!.DeepClone();
        if (conflicting)
        {
            duplicate["actions"] = new JsonArray();
        }
        authorizations.Add(duplicate);
        AssertDuplicate(
            AuthorizationMetadataFailureKind.DuplicateAuthorizationId,
            "7",
            "authorization ID",
            "$[0].authorizations[0].id",
            "$[0].authorizations[1].id"
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_rejects_duplicate_action_names_even_when_permissions_agree(bool conflicting)
    {
        var actions = _input[0]!["authorizations"]![0]!["actions"]!.AsArray();
        var duplicate = actions[0]!.DeepClone();
        if (conflicting)
        {
            duplicate["authorizationStrategies"] = new JsonArray();
        }
        actions.Add(duplicate);
        AssertDuplicate(
            AuthorizationMetadataFailureKind.DuplicateActionName,
            "Read",
            "action name",
            "$[0].authorizations[0].actions[0].name",
            "$[0].authorizations[0].actions[1].name"
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_rejects_duplicate_resource_uris_with_identical_or_conflicting_dereferenced_permissions(
        bool conflicting
    )
    {
        var secondAuthorization = _input[0]!["authorizations"]![0]!.DeepClone();
        secondAuthorization["id"] = 8;
        if (conflicting)
        {
            secondAuthorization["actions"] = new JsonArray();
        }
        _input[0]!["authorizations"]!.AsArray().Add(secondAuthorization);
        _input[0]!["claims"]!
            .AsArray()
            .Add(new JsonObject { ["name"] = "urn:student", ["authorizationId"] = 8 });
        AssertDuplicate(
            AuthorizationMetadataFailureKind.DuplicateClaimName,
            "urn:student",
            "resource claim URI",
            "$[0].claims[0].name",
            "$[0].claims[1].name"
        );
    }

    [Test]
    public void It_rejects_a_reference_that_only_exists_in_a_different_set()
    {
        var secondSet = _input[0]!.DeepClone();
        secondSet["claimSetName"] = "Other";
        secondSet["authorizations"] = new JsonArray();
        _input.Add(secondSet);
        var failure = AuthorizationMetadataValidatorFixtures
            .Failure(_input.ToJsonString())
            .Failures.Should()
            .ContainSingle()
            .Subject;
        failure.Kind.Should().Be(AuthorizationMetadataFailureKind.UnknownAuthorizationId);
        failure.Value.Should().Be("7");
        failure.Paths.Should().Equal("$[1].claims[0].authorizationId");
        failure
            .Message.Should()
            .Be("Resource claim 'urn:student' in claim set 'Other' references unknown authorization ID '7'.");
    }

    [Test]
    public void It_reports_all_semantic_errors_in_deterministic_order_without_returning_partial_permissions()
    {
        var second = _input[0]!.DeepClone();
        second["claims"]!.AsArray().Add(second["claims"]![0]!.DeepClone());
        second["claims"]![1]!["authorizationId"] = 99;
        second["authorizations"]!.AsArray().Add(second["authorizations"]![0]!.DeepClone());
        second["authorizations"]![1]!["actions"]!
            .AsArray()
            .Add(second["authorizations"]![1]!["actions"]![0]!.DeepClone());
        _input.Add(second);
        var failures = AuthorizationMetadataValidatorFixtures.Failure(_input.ToJsonString()).Failures;
        failures
            .Select(failure => failure.Kind)
            .Should()
            .Equal(
                AuthorizationMetadataFailureKind.DuplicateClaimSetName,
                AuthorizationMetadataFailureKind.DuplicateAuthorizationId,
                AuthorizationMetadataFailureKind.DuplicateActionName,
                AuthorizationMetadataFailureKind.DuplicateClaimName,
                AuthorizationMetadataFailureKind.UnknownAuthorizationId
            );
        AuthorizationMetadataValidatorFixtures
            .Failure(_input.ToJsonString())
            .Failures.Should()
            .BeEquivalentTo(failures, options => options.WithStrictOrdering());
    }

    private void AssertDuplicate(
        AuthorizationMetadataFailureKind kind,
        string value,
        string description,
        string firstPath,
        string duplicatePath
    )
    {
        var failure = AuthorizationMetadataValidatorFixtures
            .Failure(_input.ToJsonString())
            .Failures.Should()
            .ContainSingle()
            .Subject;
        failure.Kind.Should().Be(kind);
        failure.Value.Should().Be(value);
        failure.Paths.Should().Equal(firstPath, duplicatePath);
        failure.Message.Should().Be($"Duplicate {description} '{value}'.");
    }
}

[TestFixture]
public class Given_AuthorizationMetadataValidator_valid_permissions
{
    private JsonArray _input = null!;

    [SetUp]
    public void Setup() => _input = AuthorizationMetadataValidatorFixtures.Valid();

    [Test]
    public void It_preserves_strategy_sequence_and_multiplicity()
    {
        var claim = Validate().ClaimSets.Single().Claims.Single();
        claim.Name.Should().Be("urn:student");
        claim.Actions.Single().Name.Should().Be("Read");
        claim
            .Actions.Single()
            .AuthorizationStrategies.Select(strategy => strategy.Name)
            .Should()
            .Equal("Second", "First", "Second");
    }

    [Test]
    public void It_scopes_reused_ids_and_uris_to_each_set()
    {
        var second = _input[0]!.DeepClone();
        second["claimSetName"] = "Other";
        second["authorizations"]![0]!["actions"]![0]!["name"] = "Create";
        _input.Add(second);
        var sets = Validate().ClaimSets;
        sets.Select(set => set.ClaimSetName).Should().Equal("School", "Other");
        sets.Select(set => set.Claims.Single().Name).Should().Equal("urn:student", "urn:student");
        sets.Select(set => set.Claims.Single().Actions.Single().Name).Should().Equal("Read", "Create");
    }

    [Test]
    public void It_preserves_case_distinct_and_unnormalized_uris()
    {
        string[] names =
        [
            "urn:student",
            "urn:Student",
            "http://EXAMPLE.org/student",
            "http://example.org/student",
            "http://example.org/%73tudent",
            "http://example.org/student/",
            " urn:student ",
        ];
        _input[0]!["claims"] = new JsonArray(
            names
                .Select(name => (JsonNode)new JsonObject { ["name"] = name, ["authorizationId"] = 7 })
                .ToArray()
        );
        Validate().ClaimSets.Single().Claims.Select(claim => claim.Name).Should().Equal(names);
    }

    [Test]
    public void It_preserves_original_names_and_ordinal_action_distinctions()
    {
        _input[0]!["claimSetName"] = " School ";
        var actions = _input[0]!["authorizations"]![0]!["actions"]!.AsArray();
        actions.Add(
            new JsonObject
            {
                ["name"] = "read",
                ["authorizationStrategies"] = new JsonArray(new JsonObject { ["name"] = " first " }),
            }
        );
        var set = Validate().ClaimSets.Single();
        set.ClaimSetName.Should().Be(" School ");
        set.Claims.Single().Actions.Select(action => action.Name).Should().Equal("Read", "read");
        set.Claims.Single().Actions[1].AuthorizationStrategies.Single().Name.Should().Be(" first ");
    }

    [Test]
    public void It_preserves_empty_claim_sets() =>
        AuthorizationMetadataValidatorFixtures
            .Success("""[{"claimSetName":"Empty","claims":[],"authorizations":[]}]""")
            .ClaimSets.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(new ValidatedClaimSetMetadata("Empty", []));

    [Test]
    public void It_accepts_an_empty_metadata_array() =>
        AuthorizationMetadataValidatorFixtures.Success("[]").ClaimSets.Should().BeEmpty();

    [Test]
    public void It_preserves_a_claim_with_no_actions()
    {
        _input[0]!["authorizations"]![0]!["actions"] = new JsonArray();
        Validate().ClaimSets.Single().Claims.Should().ContainSingle().Which.Actions.Should().BeEmpty();
    }

    [Test]
    public void It_preserves_an_action_with_no_strategies()
    {
        _input[0]!["authorizations"]![0]!["actions"]![0]!["authorizationStrategies"] = new JsonArray();
        Validate()
            .ClaimSets.Single()
            .Claims.Single()
            .Actions.Should()
            .ContainSingle()
            .Which.AuthorizationStrategies.Should()
            .BeEmpty();
    }

    [TestCase(int.MinValue)]
    [TestCase(0)]
    [TestCase(int.MaxValue)]
    public void It_uses_ids_only_for_scoped_lookup(int id)
    {
        _input[0]!["claims"]![0]!["authorizationId"] = id;
        _input[0]!["authorizations"]![0]!["id"] = id;
        Validate().ClaimSets.Single().Claims.Single().Actions.Single().Name.Should().Be("Read");
    }

    [Test]
    public void It_accepts_unreferenced_valid_authorizations_and_ignores_unrelated_catalog_properties()
    {
        _input[0]!["authorizations"]!
            .AsArray()
            .Add(new JsonObject { ["id"] = 9, ["actions"] = new JsonArray() });
        _input[0]!["isSystemReserved"] = true;
        Validate().ClaimSets.Single().Claims.Single().Actions.Single().Name.Should().Be("Read");
    }

    [Test]
    public void It_accepts_reviewed_resolved_metadata_with_exact_dereferenced_permissions()
    {
        string json = ResolvedMetadataSerializerFixtures.Read("expected.json");
        var sets = AuthorizationMetadataValidatorFixtures.Success(json).ClaimSets;
        sets.Select(set => set.ClaimSetName).Should().Equal("Empty", "Zulu", "zoo");
        sets[0].Claims.Should().BeEmpty();
        sets[1]
            .Claims.Single()
            .Actions.Single()
            .AuthorizationStrategies.Select(strategy => strategy.Name)
            .Should()
            .Equal("Zulu");
        var claims = sets[2].Claims;
        claims
            .Select(claim => claim.Name)
            .Should()
            .Equal(
                "urn:Z-reverse",
                "urn:a-forward",
                "urn:actions",
                "urn:duplicate",
                "urn:no-strategies",
                "urn:z-empty"
            );
        claims[0]
            .Actions.Single()
            .AuthorizationStrategies.Select(strategy => strategy.Name)
            .Should()
            .Equal("Second", "First");
        claims[1]
            .Actions.Single()
            .AuthorizationStrategies.Select(strategy => strategy.Name)
            .Should()
            .Equal("First", "Second");
        claims[2].Actions.Select(action => action.Name).Should().Equal("Create", "Zed", "apple", "update");
        claims[2]
            .Actions[0]
            .AuthorizationStrategies.Select(strategy => strategy.Name)
            .Should()
            .Equal("z", "A", "z");
        claims[3]
            .Actions.Single()
            .AuthorizationStrategies.Select(strategy => strategy.Name)
            .Should()
            .Equal("Second", "First");
        claims[4].Actions.Single().AuthorizationStrategies.Should().BeEmpty();
        claims[5].Actions.Should().BeEmpty();
    }

    private AuthorizationMetadataValidationResult.Success Validate() =>
        AuthorizationMetadataValidatorFixtures.Success(_input.ToJsonString());
}
