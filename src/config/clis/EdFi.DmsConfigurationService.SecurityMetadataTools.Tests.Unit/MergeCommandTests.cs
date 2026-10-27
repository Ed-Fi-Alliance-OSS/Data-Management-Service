// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Claims;
using EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Claims.Composition;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

public abstract class MergeCommandTests
{
    protected static readonly string Fixtures = Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "Fixtures",
        "ClaimsComposition"
    );
    protected string DirectoryPath = string.Empty;
    protected string Fragments = string.Empty;
    protected string Destination = string.Empty;
    protected const string Sentinel = "existing destination\r\n\u2603";
    protected string Error = string.Empty;
    protected string Output = string.Empty;
    protected int ExitCode;

    [SetUp]
    public void CreateDirectory()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), $"merge-tests-{Guid.NewGuid():N}");
        Fragments = Path.Combine(DirectoryPath, "fragments");
        Directory.CreateDirectory(Fragments);
        Destination = Path.Combine(DirectoryPath, "output.json");
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(DirectoryPath, true);

    protected async Task RunFixture(
        string basePath,
        bool force = true,
        Func<ClaimsDocument, string, ClaimsCompositionResult>? compose = null
    )
    {
        using var error = new StringWriter();
        using var output = new StringWriter();
        var command = MergeCommand.Create(
            error,
            _ => new(ReadDocument(basePath), []),
            compose ?? new ClaimsFragmentComposer(NullLogger.Instance).ComposeClaimsFromFragments
        );
        string[] args = ["--fragments", Fragments, "--output", Destination];
        ExitCode = await command
            .Parse(force ? [.. args, "--force"] : args)
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        Error = error.ToString();
        Output = output.ToString();
    }

    protected async Task RunHost(string[] extra, bool force = true)
    {
        using var error = new StringWriter();
        using var output = new StringWriter();
        string[] args = ["merge", "--fragments", Fragments, "--output", Destination, .. extra];
        ExitCode = await CommandHost.InvokeAsync(
            force ? [.. args, "--force"] : args,
            output,
            error,
            () => throw new AssertionException("Merge must not create an HTTP client.")
        );
        Error = error.ToString();
        Output = output.ToString();
    }

    protected static ClaimsDocument ReadDocument(string file)
    {
        var document = JsonNode.Parse(File.ReadAllText(file))!;
        return new(document["claimSets"]!, document["claimsHierarchy"]!);
    }

    protected void AssertNoOutput(bool existing)
    {
        ExitCode.Should().Be(1);
        Output.Should().BeEmpty();
        if (existing)
        {
            File.ReadAllBytes(Destination).Should().Equal(System.Text.Encoding.UTF8.GetBytes(Sentinel));
        }
        else
        {
            File.Exists(Destination).Should().BeFalse();
        }
        string[] expectedFiles = existing ? [Destination] : [];
        Directory.GetFiles(DirectoryPath).Should().Equal(expectedFiles);
    }

    protected void AssertDocument(string expectedPath)
    {
        ExitCode.Should().Be(0, Error);
        Error.Should().BeEmpty();
        Output.Should().BeEmpty();
        JsonNode
            .DeepEquals(
                JsonNode.Parse(File.ReadAllText(Destination)),
                JsonNode.Parse(File.ReadAllText(expectedPath))
            )
            .Should()
            .BeTrue();
    }

    protected void StageScenario(string scenario)
    {
        string source = Path.Combine(Fixtures, scenario, "fragments");
        if (!Directory.Exists(source))
        {
            return;
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(Fragments, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}

[TestFixtureSource(nameof(Cases))]
public class Given_MergeCommand_unapplied_input(string table, string scenario, bool existing)
    : MergeCommandTests
{
    public static IEnumerable<TestFixtureData> Cases =>
        Directory
            .GetFiles(Path.Combine(Fixtures, "unapplied-input"), "*-cases.json")
            .Order(StringComparer.Ordinal)
            .SelectMany(file =>
                JsonNode
                    .Parse(File.ReadAllText(file))!
                    .AsArray()
                    .SelectMany(entry =>
                        new[] { false, true }.Select(existing => new TestFixtureData(
                            Path.GetFileName(file),
                            entry!["name"]!.GetValue<string>(),
                            existing
                        ))
                    )
            );

    private JsonNode _entry = null!;
    private string _basePath = string.Empty;
    private string _expected = string.Empty;
    private string _fragmentPath = string.Empty;
    private JsonNode[] _failures = [];

    [SetUp]
    public async Task Setup()
    {
        string root = Path.Combine(Fixtures, "unapplied-input");
        _entry = JsonNode
            .Parse(await File.ReadAllTextAsync(Path.Combine(root, table)))!
            .AsArray()
            .Single(entry => entry!["name"]!.GetValue<string>() == scenario)!;
        _basePath = Path.Combine(root, _entry["base"]?.GetValue<string>() ?? "../base.json");
        _expected = Path.Combine(root, _entry["expected"]!.GetValue<string>());
        _fragmentPath = Path.Combine(Fragments, "input-claims.json");
        await File.WriteAllTextAsync(_fragmentPath, _entry["fragment"]!.ToJsonString());
        _failures = _entry["diagnostics"]!
            .AsArray()
            .OfType<JsonNode>()
            .Where(d => d["code"]!.GetValue<string>() != "ClaimSetRegistered")
            .ToArray();
        if (existing)
        {
            await File.WriteAllTextAsync(Destination, Sentinel);
        }
        await RunFixture(_basePath);
    }

    [Test]
    public void It_accepts_only_complete_composition_and_preserves_failed_destinations()
    {
        if (_failures.Length == 0)
        {
            AssertDocument(_expected);
        }
        else
        {
            AssertNoOutput(existing);
        }
    }

    [Test]
    public void It_renders_every_original_fragment_claim_and_indexed_location_on_stderr()
    {
        string expectedError = string.Concat(
            _failures.Select(d =>
                $"Merge failed: {d["code"]}: source '{_fragmentPath}', claim '{d["claimName"]}', claim set '{d["claimSetName"]}', path '{d["jsonPath"]}'.{Environment.NewLine}"
            )
        );
        Error.Should().Be(expectedError);
    }
}

[TestFixture("first-wins")]
[TestFixture("reverse-wins")]
[TestFixture("base-wins")]
public class Given_MergeCommand_precedence(string scenario) : MergeCommandTests
{
    private byte[] _first = [];

    [SetUp]
    public async Task Setup()
    {
        StageScenario(scenario);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
        _first = await File.ReadAllBytesAsync(Destination);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
    }

    [Test]
    public void It_matches_the_reviewed_CMS_document_in_each_application_order() =>
        AssertDocument(Path.Combine(Fixtures, scenario, "expected.json"));

    [Test]
    public void It_produces_identical_bytes() => File.ReadAllBytes(Destination).Should().Equal(_first);
}

[TestFixture("malformed", "MalformedFragment")]
[TestFixture("retired", "RetiredFragment")]
[TestFixture("unmatched", "UnmatchedGrantTarget")]
[TestFixture("invalid-registration", "InvalidClaimSetRegistration")]
[TestFixture("empty-registration", "InvalidClaimSetRegistration")]
[TestFixture("no-resource-claims", "NoResourceClaims")]
[TestFixture("discarded-children", "InvalidChildName")]
[TestFixture("missing", "MissingDirectory")]
public class Given_MergeCommand_incomplete_composition(string scenario, string code) : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        StageScenario(scenario);
        if (scenario == "missing")
        {
            Directory.Delete(Fragments);
        }
        await File.WriteAllTextAsync(Destination, Sentinel);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
    }

    [Test]
    public void It_rejects_with_context_and_preserves_output()
    {
        AssertNoOutput(true);
        Error.Should().Contain(code).And.Contain(Fragments);
    }

    [Test]
    public async Task It_creates_no_partial_new_file()
    {
        File.Delete(Destination);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
        AssertNoOutput(false);
    }
}

[TestFixture("empty-object", "")]
[TestFixture("named-omission", "Skipped\nPolicy")]
[TestFixture("complete-document", "")]
public class Given_MergeCommand_omitted_resource_claims(string scenario, string claimSetName)
    : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        string input = scenario switch
        {
            "complete-document" => await File.ReadAllTextAsync(Path.Combine(Fixtures, "base.json")),
            "named-omission" => new JsonObject { ["name"] = claimSetName }.ToJsonString(),
            _ => "{}",
        };
        await File.WriteAllTextAsync(Path.Combine(Fragments, "input-claims.json"), input);
        await File.WriteAllTextAsync(Destination, Sentinel);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
    }

    [Test]
    public void It_reports_sanitized_source_context_and_preserves_existing_bytes_with_force()
    {
        AssertNoOutput(true);
        string safeName = scenario == "named-omission" ? "Skipped\\u000APolicy" : "";
        Error
            .Should()
            .Be(
                $"Merge failed: NoResourceClaims: source '{Path.Combine(Fragments, "input-claims.json")}', claim '', claim set '{safeName}', path '$.resourceClaims'.{Environment.NewLine}"
            );
    }

    [Test]
    public async Task It_creates_no_destination_or_staging_residue()
    {
        File.Delete(Destination);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
        AssertNoOutput(false);
    }
}

[TestFixture("resourceClaims")]
[TestFixture("ReSoUrCeClAiMs")]
public class Given_MergeCommand_explicitly_empty_resource_claims(string propertyName) : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        await File.WriteAllTextAsync(
            Path.Combine(Fragments, "empty-claims.json"),
            new JsonObject { [propertyName] = new JsonArray() }.ToJsonString()
        );
        await RunFixture(Path.Combine(Fixtures, "base.json"));
    }

    [Test]
    public void It_preserves_successful_base_only_composition() =>
        AssertDocument(Path.Combine(Fixtures, "base.json"));
}

[TestFixture("")]
[TestFixture("5.2")]
[TestFixture("6.1")]
public class Given_MergeCommand_bundled_base(string version) : MergeCommandTests
{
    [SetUp]
    public async Task Setup() =>
        await RunHost(version.Length == 0 ? [] : ["--data-standard-version", version]);

    [Test]
    public void It_emits_the_complete_selected_base_without_network_access()
    {
        ExitCode.Should().Be(0, Error);
        Error.Should().BeEmpty();
        string json = File.ReadAllText(Destination);
        CompleteClaimsDocumentValidator
            .Validate(json)
            .Should()
            .BeOfType<CompleteClaimsDocumentValidationResult.Success>();
        var document = JsonNode.Parse(json)!;
        var bundled = BundledClaimsProvider.Load(version).Nodes!;
        JsonNode.DeepEquals(document["claimSets"], bundled.ClaimSetsNode).Should().BeTrue();
        JsonNode.DeepEquals(document["claimsHierarchy"], bundled.ClaimsHierarchyNode).Should().BeTrue();
    }

    [Test]
    public async Task It_is_byte_stable()
    {
        byte[] first = await File.ReadAllBytesAsync(Destination);
        await RunHost(version.Length == 0 ? [] : ["--data-standard-version", version]);
        ExitCode.Should().Be(0, Error);
        (await File.ReadAllBytesAsync(Destination)).Should().Equal(first);
    }
}

[TestFixture(false)]
[TestFixture(true)]
public class Given_MergeCommand_unsupported_version(bool existing) : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        if (existing)
        {
            await File.WriteAllTextAsync(Destination, Sentinel);
        }
        await RunHost(["--data-standard-version", "99\nunsupported"]);
    }

    [Test]
    public void It_rejects_and_normalizes_the_error_without_touching_output()
    {
        AssertNoOutput(existing);
        Error.Should().Contain("99\\u000Aunsupported").And.NotContain("99\nunsupported");
    }
}

[TestFixture]
public class Given_MergeCommand_output_protection : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        await File.WriteAllTextAsync(Destination, Sentinel);
        await RunHost([], force: false);
    }

    [Test]
    public void It_requires_force_to_replace_an_existing_file()
    {
        AssertNoOutput(true);
        Error.Should().Contain("--force");
    }
}

[TestFixture("duplicate-leaf", "DuplicateClaimName")]
[TestFixture("duplicate-parent", "DuplicateClaimName")]
[TestFixture("undeclared-reference", "UndeclaredClaimSet")]
[TestFixture("schema", "Schema")]
public class Given_MergeCommand_invalid_composed_document(string scenario, string code) : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        string fragment = scenario switch
        {
            "duplicate-leaf" => """
                {"name":"NewPolicy","resourceClaims":[{"name":"new/parent","isParent":true,"children":[{"name":"edfi/student"}]}]}
                """,
            "duplicate-parent" => """
                {"name":"NewPolicy","resourceClaims":[{"name":"new/parent","isParent":true,"children":[{"name":"edfi/domain"}]}]}
                """,
            "undeclared-reference" => """
                {"name":"NewPolicy","resourceClaims":[{"name":"new/parent","isParent":true,"claimSets":[{"name":"Undeclared","actions":[{"name":"Read"}]}]}]}
                """,
            _ => """
                {"name":"NewPolicy","resourceClaims":[{"name":"new/parent","isParent":true,"children":[{}]}]}
                """,
        };
        await File.WriteAllTextAsync(Path.Combine(Fragments, "input-claims.json"), fragment);
        await File.WriteAllTextAsync(Destination, Sentinel);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
    }

    [Test]
    public void It_rejects_with_the_integrity_or_schema_location()
    {
        AssertNoOutput(true);
        Error.Should().Contain(code).And.Contain("$");
    }

    [Test]
    public async Task It_creates_no_partial_new_file()
    {
        File.Delete(Destination);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
        AssertNoOutput(false);
    }
}

// Fragment registration deduplicates catalog names. Exercise the validation boundary
// with the reviewed complete-document fixtures for defects a bundled base could contain.
[TestFixtureSource(
    typeof(CompleteClaimsDocumentValidatorFixtures),
    nameof(CompleteClaimsDocumentValidatorFixtures.IntegrityCases)
)]
public class Given_MergeCommand_invalid_base(string file) : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        string basePath = Path.Combine(DirectoryPath, "base.json");
        await File.WriteAllTextAsync(basePath, CompleteClaimsDocumentValidatorFixtures.Read(file));
        await File.WriteAllTextAsync(Destination, Sentinel);
        await RunFixture(basePath);
        File.Delete(basePath);
    }

    [Test]
    public void It_enforces_complete_document_integrity_before_publishing()
    {
        AssertNoOutput(true);
        var expected = CompleteClaimsDocumentValidatorFixtures.Failures.Single(c => c.File == file);
        Error.Should().Contain(expected.Kind);
        foreach (string path in expected.Paths)
        {
            Error.Should().Contain(path);
        }
    }
}

[TestFixture(ClaimsCompositionDiagnosticCode.DirectoryDiscoveryFailed)]
[TestFixture(ClaimsCompositionDiagnosticCode.FragmentReadFailed)]
public class Given_MergeCommand_failed_filesystem_boundary(ClaimsCompositionDiagnosticCode code)
    : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        await File.WriteAllTextAsync(Destination, Sentinel);
        await RunFixture(
            Path.Combine(Fixtures, "base.json"),
            compose: (document, path) =>
                new(
                    new(document, []),
                    [new(code, path + "\n\u2028", "claim\r", "set\u001b", "$.resourceClaims[0]")]
                )
        );
    }

    [Test]
    public void It_rejects_even_a_document_returned_with_discovery_or_read_diagnostics()
    {
        AssertNoOutput(true);
        Error
            .Should()
            .Be(
                $"Merge failed: {code}: source '{Fragments}\\u000A\\u2028', claim 'claim\\u000D', claim set 'set\\u001B', path '$.resourceClaims[0]'.{Environment.NewLine}"
            );
    }
}

[TestFixture("--help", 0, "--data-standard-version")]
[TestFixture("--fragments", 1, "Option '--output' is required.")]
[TestFixture("--output", 1, "Option '--fragments' is required.")]
[TestFixture("--unknown", 1, "Unrecognized command or argument '--unknown'.")]
public class Given_MergeCommand_arguments(string option, int expectedExit, string expectedText)
{
    private int _exitCode;
    private string _output = string.Empty;
    private string _error = string.Empty;

    [SetUp]
    public async Task Setup()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        _exitCode = await CommandHost.InvokeAsync(
            option switch
            {
                "--help" => ["merge", option],
                "--unknown" => ["merge", "--fragments", "some-path", "--output", "some-file", option],
                _ => ["merge", option, "some-path"],
            },
            output,
            error
        );
        _output = output.ToString();
        _error = error.ToString();
    }

    [Test]
    public void It_exposes_help_or_rejects_invalid_arguments_on_the_expected_streams()
    {
        _exitCode.Should().Be(expectedExit);
        if (expectedExit == 0)
        {
            _output
                .Should()
                .Contain(expectedText)
                .And.Contain("--fragments")
                .And.Contain("--output")
                .And.Contain("--force")
                .And.Contain("0 = success")
                .And.Contain("1 = invalid input");
            _error.Should().BeEmpty();
        }
        else
        {
            _output.Should().BeEmpty();
            _error
                .Should()
                .Contain(expectedText)
                .And.Contain("Usage:")
                .And.Contain("--fragments")
                .And.Contain("--output")
                .And.Contain("--data-standard-version")
                .And.Contain("--force");
        }
    }
}

[TestFixtureSource(nameof(Cases))]
public class Given_MergeCommand_informational_actions(string scenario) : MergeCommandTests
{
    public static IEnumerable<TestFixtureData> Cases =>
        JsonNode
            .Parse(File.ReadAllText(Path.Combine(Fixtures, "unapplied-input", "informational-cases.json")))!
            .AsArray()
            .Select(entry => new TestFixtureData(entry!["name"]!.GetValue<string>()));

    [SetUp]
    public async Task Setup()
    {
        var entry = JsonNode
            .Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(Fixtures, "unapplied-input", "informational-cases.json")
                )
            )!
            .AsArray()
            .Single(entry => entry!["name"]!.GetValue<string>() == scenario)!;
        await File.WriteAllTextAsync(
            Path.Combine(Fragments, "input-claims.json"),
            entry["fragment"]!.ToJsonString()
        );
        await RunFixture(Path.Combine(Fixtures, "base.json"));
        ExitCode.Should().Be(0, Error);
    }

    [Test]
    public void It_resolves_informational_action_flags_using_only_override_grants()
    {
        var document = (CompleteClaimsDocumentValidationResult.Success)
            CompleteClaimsDocumentValidator.Validate(File.ReadAllText(Destination));
        var set = AuthorizationMetadataResolver
            .Resolve(document.ClaimSetNames, document.Hierarchy)
            .ClaimSets.Single(set => set.ClaimSetName == "Isolated");
        if (scenario.EndsWith("applied", StringComparison.Ordinal))
        {
            set.Claims.Should().ContainSingle().Which.Name.Should().Be("edfi/student");
            var actions = set.Authorizations.Single(a => a.Id == set.Claims.Single().AuthorizationId).Actions;
            actions.Should().ContainSingle().Which.Name.Should().Be("Read");
            actions.Single().AuthorizationStrategies.Select(s => s.Name).Should().Equal("Applied");
        }
        else
        {
            set.Claims.Should().BeEmpty();
            set.Authorizations.Should().BeEmpty();
        }
    }
}

[TestFixture("5.2")]
[TestFixture("6.1")]
public class Given_MergeCommand_bundled_composition(string version) : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        await File.WriteAllTextAsync(
            Path.Combine(Fragments, "offline-claims.json"),
            """
            {"name":"Offline","resourceClaims":[{
              "name":"ed-fi/students",
              "authorizationStrategyOverridesForCRUD":[{
                "actionName":"Read","authorizationStrategies":[{"name":"NoFurtherAuthorizationRequired"}]
              }]
            }]}
            """
        );
        await RunHost(["--data-standard-version", version]);
    }

    [Test]
    public void It_produces_a_complete_valid_document_with_the_exact_requested_permissions()
    {
        ExitCode.Should().Be(0, Error);
        Error.Should().BeEmpty();
        var document = CompleteClaimsDocumentValidator
            .Validate(File.ReadAllText(Destination))
            .Should()
            .BeOfType<CompleteClaimsDocumentValidationResult.Success>()
            .Subject;
        var set = AuthorizationMetadataResolver
            .Resolve(document.ClaimSetNames, document.Hierarchy)
            .ClaimSets.Single(set => set.ClaimSetName == "Offline");
        set.Claims.Should()
            .ContainSingle()
            .Which.Name.Should()
            .Be("http://ed-fi.org/identity/claims/ed-fi/student");
        var actions = set.Authorizations.Single(a => a.Id == set.Claims.Single().AuthorizationId).Actions;
        actions.Should().ContainSingle().Which.Name.Should().Be("Read");
        actions
            .Single()
            .AuthorizationStrategies.Select(s => s.Name)
            .Should()
            .Equal("NoFurtherAuthorizationRequired");
    }
}

[TestFixture("resource-claim", "", "$.resourceClaims[1]")]
[TestFixture("children", "edfi/parent\\u2028name", "$.resourceClaims[1].children")]
[TestFixture("child", "edfi/parent\\u2028name", "$.resourceClaims[1].children[1]")]
[TestFixture("missing-name", "", "$.resourceClaims[1].name")]
[TestFixture("null-name", "", "$.resourceClaims[1].name")]
[TestFixture(
    "grant-strategy",
    "edfi/student",
    "$.resourceClaims[1].authorizationStrategyOverridesForCRUD[1].authorizationStrategies[1]"
)]
[TestFixture(
    "default-strategy",
    "edfi/parent\\u2028name",
    "$.resourceClaims[1]._defaultAuthorizationStrategiesForCrud[1].authorizationStrategies[1]"
)]
[TestFixture("claim-set", "edfi/parent\\u2028name", "$.resourceClaims[1].claimSets[0]")]
[TestFixture("claim-set-action", "edfi/parent\\u2028name", "$.resourceClaims[1].claimSets[0].actions[0]")]
[TestFixture(
    "claim-set-strategy",
    "edfi/parent\\u2028name",
    "$.resourceClaims[1].claimSets[0].actions[0].authorizationStrategyOverrides[1]"
)]
public class Given_MergeCommand_null_structure(string scenario, string safeClaimName, string jsonPath)
    : MergeCommandTests
{
    [SetUp]
    public async Task Setup()
    {
        File.Copy(
            Path.Combine(Fixtures, "null-structures", "fragments", $"{scenario}-claims.json"),
            Path.Combine(Fragments, "input-claims.json")
        );
        await File.WriteAllTextAsync(Destination, Sentinel);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
    }

    [Test]
    public void It_reports_sanitized_context_without_a_stack_trace_and_preserves_existing_bytes_with_force()
    {
        AssertNoOutput(true);
        string source = Path.Combine(Fragments, "input-claims.json");
        Error
            .Should()
            .Be(
                $"Merge failed: MalformedFragment: source '{source}', claim '{safeClaimName}', claim set 'Policy\\u000AName', path '{jsonPath}'.{Environment.NewLine}"
                    + $"Merge failed: NullFragmentStructure at '{source}': Fragment contains a missing or null required value.{Environment.NewLine}"
            );
    }

    [Test]
    public async Task It_creates_no_destination_or_staging_residue()
    {
        File.Delete(Destination);
        await RunFixture(Path.Combine(Fixtures, "base.json"));
        AssertNoOutput(false);
    }
}
