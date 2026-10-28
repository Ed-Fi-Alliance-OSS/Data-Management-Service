// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal sealed class ResolveCommandFixture : IDisposable
{
    internal string DirectoryPath { get; } =
        Path.Combine(Path.GetTempPath(), $"resolve command {Guid.NewGuid():N}");
    internal string InputPath => Path.Combine(DirectoryPath, "input claims.json");
    internal string OutputPath => Path.Combine(DirectoryPath, "resolved metadata.json");
    internal string Output { get; private set; } = "";
    internal string Error { get; private set; } = "";
    internal int ExitCode { get; private set; }

    internal ResolveCommandFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(InputPath, CompleteClaimsDocumentValidatorFixtures.Read("valid.json"));
    }

    internal Task ResolveAsync(params string[] extra) =>
        InvokeAsync(["resolve", "--input", InputPath, "--output", OutputPath, .. extra]);

    internal async Task InvokeAsync(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        ExitCode = await CommandHost.InvokeAsync(
            args,
            output,
            error,
            () => throw new AssertionException("Offline commands must not create an HTTP client.")
        );
        Output = output.ToString();
        Error = error.ToString();
    }

    internal void AssertSuccess()
    {
        ExitCode.Should().Be(0, Error);
        Output.Should().BeEmpty();
        Error.Should().BeEmpty();
    }

    internal void AssertProtected(bool existing)
    {
        if (existing)
        {
            File.ReadAllBytes(OutputPath).Should().Equal(CommandWorkspace.Sentinel);
        }
        else
        {
            File.Exists(OutputPath).Should().BeFalse();
        }
        Directory.GetFiles(DirectoryPath, "*.tmp").Should().BeEmpty();
    }

    internal ClaimSetMetadata[] ReadMetadata() =>
        JsonSerializer.Deserialize<ClaimSetMetadata[]>(
            File.ReadAllText(OutputPath),
            ResolvedMetadataSerializerFixtures.JsonOptions
        )!;

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}

[TestFixture(false)]
[TestFixture(true)]
internal class Given_ResolveCommand_reviewed_inheritance(bool existing)
{
    private ResolveCommandFixture _fixture = null!;
    private byte[] _input = [];
    private byte[] _first = [];

    [SetUp]
    public async Task Setup()
    {
        _fixture = new();
        File.Copy(
            CommandWorkspace.Fixture("AuthorizationMetadata", "inheritance-input.json"),
            _fixture.InputPath,
            true
        );
        _input = await File.ReadAllBytesAsync(_fixture.InputPath);
        if (existing)
        {
            await File.WriteAllBytesAsync(_fixture.OutputPath, CommandWorkspace.Sentinel);
        }
        await _fixture.ResolveAsync(existing ? ["--force"] : []);
        _fixture.AssertSuccess();
        _first = await File.ReadAllBytesAsync(_fixture.OutputPath);
        await _fixture.ResolveAsync("--force");
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public void It_emits_exact_canonical_metadata_twice_without_console_output()
    {
        _fixture.AssertSuccess();
        byte[] expected = Encoding.UTF8.GetBytes(
            File.ReadAllText(CommandWorkspace.Fixture("ResolveCommand", "inheritance-expected.json"))
                .TrimEnd('\r', '\n')
        );
        _first.Should().Equal(expected);
        File.ReadAllBytes(_fixture.OutputPath).Should().Equal(expected);
    }

    [Test]
    public void It_preserves_every_reviewed_permission_and_empty_catalog_entry()
    {
        var expected = JsonSerializer.Deserialize<ClaimSetMetadata[]>(
            File.ReadAllText(CommandWorkspace.Fixture("AuthorizationMetadata", "inheritance-expected.json")),
            ResolvedMetadataSerializerFixtures.JsonOptions
        )!;
        var actual = _fixture.ReadMetadata();
        actual.Select(set => set.ClaimSetName).Should().Equal("Alpha", "Empty", "Zoo");
        foreach (var expectedSet in expected)
        {
            var actualSet = actual.Single(set => set.ClaimSetName == expectedSet.ClaimSetName);
            actualSet
                .Claims.Select(claim => claim.Name)
                .Should()
                .Equal(expectedSet.Claims.Select(claim => claim.Name));
            foreach (var claim in expectedSet.Claims)
            {
                var expectedActions = expectedSet
                    .Authorizations.Single(a => a.Id == claim.AuthorizationId)
                    .Actions.OrderBy(a => a.Name, StringComparer.Ordinal);
                int id = actualSet.Claims.Single(c => c.Name == claim.Name).AuthorizationId;
                actualSet
                    .Authorizations.Single(a => a.Id == id)
                    .Actions.Should()
                    .BeEquivalentTo(
                        expectedActions,
                        options => options.ComparingByMembers<ClaimSetMetadata.Action>().WithStrictOrdering()
                    );
            }
        }
        actual.Single(set => set.ClaimSetName == "Empty").Authorizations.Should().BeEmpty();
    }

    [Test]
    public void It_emits_a_metadata_array_that_is_not_a_complete_upload_document()
    {
        string json = File.ReadAllText(_fixture.OutputPath);
        JsonNode.Parse(json).Should().BeOfType<JsonArray>();
        AuthorizationMetadataValidator
            .Validate(json)
            .Should()
            .BeOfType<AuthorizationMetadataValidationResult.Success>();
        CompleteClaimsDocumentValidator
            .Validate(json)
            .Should()
            .BeOfType<CompleteClaimsDocumentValidationResult.Failure>();
    }

    [Test]
    public void It_preserves_input_and_leaves_only_the_requested_files()
    {
        File.ReadAllBytes(_fixture.InputPath).Should().Equal(_input);
        Directory
            .GetFiles(_fixture.DirectoryPath)
            .Should()
            .BeEquivalentTo(_fixture.InputPath, _fixture.OutputPath);
    }
}

[TestFixtureSource(nameof(Cases))]
internal class Given_ResolveCommand_invalid_complete_document(string file, bool existing)
{
    public static IEnumerable<TestFixtureData> Cases =>
        CompleteClaimsDocumentValidatorFixtures.Failures.SelectMany(failure =>
            new[] { false, true }.Select(existing => new TestFixtureData(failure.File, existing))
        );
    private ResolveCommandFixture _fixture = null!;
    private byte[] _input = [];

    [SetUp]
    public async Task Setup()
    {
        _fixture = new();
        await File.WriteAllTextAsync(_fixture.InputPath, CompleteClaimsDocumentValidatorFixtures.Read(file));
        _input = await File.ReadAllBytesAsync(_fixture.InputPath);
        if (existing)
        {
            await File.WriteAllBytesAsync(_fixture.OutputPath, CommandWorkspace.Sentinel);
        }
        await _fixture.ResolveAsync("--force");
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public void It_reports_the_reviewed_failure_kind_value_and_locations()
    {
        var expected = CompleteClaimsDocumentValidatorFixtures.Failures.Single(failure =>
            failure.File == file
        );
        _fixture.ExitCode.Should().Be(1);
        _fixture.Output.Should().BeEmpty();
        _fixture
            .Error.Should()
            .StartWith($"Resolution failed: {expected.Kind} at ")
            .And.Contain(expected.Value.Replace("\n", "\\u000A", StringComparison.Ordinal));
        foreach (string path in expected.Paths)
        {
            _fixture.Error.Should().Contain(path);
        }
    }

    [Test]
    public void It_preserves_input_and_destination_even_with_force()
    {
        File.ReadAllBytes(_fixture.InputPath).Should().Equal(_input);
        _fixture.AssertProtected(existing);
    }
}

[TestFixture]
internal class Given_ResolveCommand_invalid_json_or_schema
{
    [TestCase("{broken", "InvalidJson")]
    [TestCase("{}", "Schema")]
    [TestCase("[]", "Schema")]
    [TestCase("empty-actions", "Schema")]
    public async Task It_reports_validation_before_touching_existing_or_absent_output(
        string input,
        string kind
    )
    {
        foreach (bool existing in new[] { false, true })
        {
            using ResolveCommandFixture fixture = new();
            if (input == "empty-actions")
            {
                var node = JsonNode.Parse(CompleteClaimsDocumentValidatorFixtures.Read("valid.json"))!;
                node["claimsHierarchy"]![0]!["claimSets"]![0]!["actions"] = new JsonArray();
                await File.WriteAllTextAsync(fixture.InputPath, node.ToJsonString());
            }
            else
            {
                await File.WriteAllTextAsync(fixture.InputPath, input);
            }
            if (existing)
            {
                await File.WriteAllBytesAsync(fixture.OutputPath, CommandWorkspace.Sentinel);
            }
            await fixture.ResolveAsync("--force");
            fixture.ExitCode.Should().Be(1);
            fixture.Output.Should().BeEmpty();
            fixture.Error.Should().StartWith($"Resolution failed: {kind} at $");
            fixture.AssertProtected(existing);
        }
    }

    [Test]
    public async Task It_escapes_input_derived_diagnostic_values()
    {
        using ResolveCommandFixture fixture = new();
        var node = JsonNode.Parse(CompleteClaimsDocumentValidatorFixtures.Read("valid.json"))!;
        node["claimSets"]![1]!["claimSetName"] = "Unsafe\nSet\u2028Name";
        await File.WriteAllTextAsync(fixture.InputPath, node.ToJsonString());
        await fixture.ResolveAsync();
        fixture.ExitCode.Should().Be(1);
        fixture.Output.Should().BeEmpty();
        fixture
            .Error.Should()
            .Contain("Unsafe\\u000ASet\\u2028Name")
            .And.NotContain("Unsafe\n")
            .And.NotContain("\u2028");
        fixture.AssertProtected(false);
    }
}

[TestFixture]
internal class Given_ResolveCommand_file_failures
{
    [TestCase("missing-input")]
    [TestCase("directory-input")]
    [TestCase("existing-output")]
    [TestCase("missing-parent")]
    [TestCase("directory-output")]
    public async Task It_returns_one_with_stderr_and_no_partial_output(string scenario)
    {
        using ResolveCommandFixture fixture = new();
        await File.WriteAllBytesAsync(fixture.OutputPath, CommandWorkspace.Sentinel);
        string destination = fixture.OutputPath;
        string input = fixture.InputPath;
        switch (scenario)
        {
            case "missing-input":
                File.Delete(input);
                break;
            case "directory-input":
                input = fixture.DirectoryPath;
                break;
            case "missing-parent":
                destination = Path.Combine(fixture.DirectoryPath, "absent", "out.json");
                break;
            case "directory-output":
                destination = Path.Combine(fixture.DirectoryPath, "directory");
                Directory.CreateDirectory(destination);
                break;
        }
        await fixture.InvokeAsync([
            "resolve",
            "--input",
            input,
            "--output",
            destination,
            .. scenario == "existing-output" ? Array.Empty<string>() : ["--force"],
        ]);
        fixture.ExitCode.Should().Be(1);
        fixture.Output.Should().BeEmpty();
        fixture.Error.Should().StartWith("Resolution failed: ");
        if (scenario == "existing-output")
        {
            fixture.Error.Should().Contain("--force");
        }
        fixture.AssertProtected(true);
        Directory.Exists(Path.Combine(fixture.DirectoryPath, "absent")).Should().BeFalse();
    }
}

[TestFixture("conversion")]
[TestFixture("5.2")]
[TestFixture("6.1")]
internal class Given_ResolveCommand_produced_documents(string source)
{
    private ResolveCommandFixture _fixture = null!;
    private byte[] _first = [];
    private byte[] _input = [];

    [SetUp]
    public async Task Setup()
    {
        _fixture = new();
        if (source == "conversion")
        {
            await _fixture.InvokeAsync([
                "convert-ods-xml",
                "--input",
                CommandWorkspace.Fixture("OdsSecurityMetadata", "representative.xml"),
                "--output",
                _fixture.InputPath,
                "--force",
            ]);
        }
        else
        {
            string fragments = Path.Combine(_fixture.DirectoryPath, "fragments");
            Directory.CreateDirectory(fragments);
            await File.WriteAllTextAsync(
                Path.Combine(fragments, "offline-claims.json"),
                """
                {"name":"Offline","resourceClaims":[{
                  "name":"ed-fi/students",
                  "authorizationStrategyOverridesForCRUD":[{
                    "actionName":"Read","authorizationStrategies":[{"name":"NoFurtherAuthorizationRequired"}]
                  }]
                }]}
                """
            );
            await _fixture.InvokeAsync([
                "merge",
                "--fragments",
                fragments,
                "--data-standard-version",
                source,
                "--output",
                _fixture.InputPath,
                "--force",
            ]);
        }
        _fixture.AssertSuccess();
        _input = await File.ReadAllBytesAsync(_fixture.InputPath);
        await _fixture.ResolveAsync();
        _fixture.AssertSuccess();
        _first = await File.ReadAllBytesAsync(_fixture.OutputPath);
        await _fixture.ResolveAsync("--force");
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public void It_accepts_the_unmodified_document_and_produces_identical_bytes_twice()
    {
        _fixture.AssertSuccess();
        File.ReadAllBytes(_fixture.OutputPath).Should().Equal(_first);
        File.ReadAllBytes(_fixture.InputPath).Should().Equal(_input);
        Directory.GetFiles(_fixture.DirectoryPath, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Test]
    public void It_resolves_the_exact_expected_permissions()
    {
        var metadata = _fixture.ReadMetadata();
        var set = metadata.Single(set =>
            set.ClaimSetName == (source == "conversion" ? "AssessmentRead" : "Offline")
        );
        if (source != "conversion")
        {
            set.Claims.Should()
                .ContainSingle()
                .Which.Name.Should()
                .Be("http://ed-fi.org/identity/claims/ed-fi/student");
        }
        AssertRead(
            "student",
            source == "conversion" ? "RelationshipsWithEdOrgsAndPeople" : "NoFurtherAuthorizationRequired"
        );
        if (source == "conversion")
        {
            AssertRead("assessment", "NamespaceBased");
        }

        void AssertRead(string resource, string strategy)
        {
            var claim = set.Claims.Single(c =>
                c.Name == $"http://ed-fi.org/identity/claims/ed-fi/{resource}"
            );
            var actions = set.Authorizations.Single(a => a.Id == claim.AuthorizationId).Actions;
            actions.Should().ContainSingle().Which.Name.Should().Be("Read");
            actions.Single().AuthorizationStrategies.Select(s => s.Name).Should().Equal(strategy);
        }
    }
}

[TestFixture]
internal class Given_ResolveCommand_arguments
{
    [TestCase("--input")]
    [TestCase("--output")]
    public async Task It_requires_each_path_and_keeps_argument_errors_on_stderr(string missing)
    {
        using ResolveCommandFixture fixture = new();
        await fixture.InvokeAsync(["resolve", missing == "--input" ? "--output" : "--input", "value"]);
        fixture.ExitCode.Should().Be(1);
        fixture.Output.Should().BeEmpty();
        fixture.Error.Should().Contain($"Option '{missing}' is required.");
        fixture.AssertProtected(false);
    }

    [Test]
    public async Task It_documents_offline_input_output_and_exit_codes()
    {
        using ResolveCommandFixture fixture = new();
        await fixture.InvokeAsync(["resolve", "--help"]);
        fixture.ExitCode.Should().Be(0);
        fixture.Error.Should().BeEmpty();
        foreach (
            string text in new[]
            {
                "--input",
                "--output",
                "--force",
                "offline",
                "/v3/authorizationMetadata",
                "not a complete claims document",
                "0 = success",
                "1 = invalid input",
                "stderr",
            }
        )
        {
            fixture.Output.Should().Contain(text);
        }
        CommandHost
            .CreateCommand(TextWriter.Null)
            .Subcommands.Single(c => c.Name == "resolve")
            .Options.Select(o => o.Name)
            .Should()
            .Equal("--input", "--output", "--force");
    }
}
