// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal sealed class VerifyCommandFixture : IDisposable
{
    internal CmsAuthorizationMetadataClientFixture Http { get; } = new();
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"verify-{Guid.NewGuid():N}");
    internal string InputPath => Path.Combine(DirectoryPath, "claims.json");
    internal string Output { get; private set; } = "";
    internal string Error { get; private set; } = "";
    internal int ExitCode { get; private set; }

    internal VerifyCommandFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(InputPath, CompleteClaimsDocumentValidatorFixtures.Read("valid.json"));
        SetResponse([
            EffectivePermissionsFixtures.Set(
                "ReviewSet",
                "urn:leaf",
                EffectivePermissionsFixtures.Action("Read", "NamespaceBased")
            ),
            new("EmptySet", [], []),
        ]);
    }

    internal void SetResponse(IEnumerable<ClaimSetMetadata> sets) =>
        Http.Handler.Body = JsonSerializer.Serialize(sets, ResolvedMetadataSerializerFixtures.JsonOptions);

    internal async Task InvokeAsync(params string[] additionalArgs)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args =
        [
            "verify",
            "--input",
            InputPath,
            "--cms-url",
            "https://cms.example/prefix",
            "--access-token-env",
            Http.VariableName,
            .. additionalArgs,
        ];
        ExitCode = await CommandHost.InvokeAsync(
            args,
            output,
            error,
            () => new(Http.Handler, disposeHandler: false)
        );
        Output = output.ToString();
        Error = error.ToString();
    }

    internal void AssertUnchanged(byte[] before)
    {
        File.ReadAllBytes(InputPath).Should().Equal(before);
        Directory.GetFiles(DirectoryPath).Should().Equal(InputPath);
        Output.Should().NotContain(CmsAuthorizationMetadataClientFixture.Token);
        Error.Should().NotContain(CmsAuthorizationMetadataClientFixture.Token);
    }

    public void Dispose()
    {
        Http.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

[TestFixture]
internal class Given_VerifyCommand_matching_metadata
{
    private VerifyCommandFixture _fixture = null!;
    private byte[] _before = [];

    [SetUp]
    public async Task Setup()
    {
        _fixture = new();
        _before = await File.ReadAllBytesAsync(_fixture.InputPath);
        await _fixture.InvokeAsync("--tenant", "District");
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public void It_returns_zero_with_only_the_match_report()
    {
        _fixture.ExitCode.Should().Be(0);
        _fixture
            .Output.Should()
            .Be(
                $"CMS authorization metadata matches the expected effective permissions.{Environment.NewLine}"
            );
        _fixture.Error.Should().BeEmpty();
    }

    [Test]
    public void It_fetches_once_using_the_command_arguments_and_existing_token()
    {
        _fixture.Http.Handler.Calls.Should().Be(1);
        _fixture.Http.Handler.Url.Should().Be("https://cms.example/prefix/v3/authorizationMetadata");
        _fixture.Http.Handler.Method.Should().Be(HttpMethod.Get);
        _fixture.Http.Handler.HasContent.Should().BeFalse();
        _fixture.Http.Handler.Headers["Tenant"].Should().Equal("District");
        _fixture
            .Http.Handler.Headers["Authorization"]
            .Should()
            .Equal($"Bearer {CmsAuthorizationMetadataClientFixture.Token}");
    }

    [Test]
    public void It_preserves_input_and_keeps_credentials_out_of_both_streams() =>
        _fixture.AssertUnchanged(_before);
}

[TestFixtureSource(
    typeof(CompleteClaimsDocumentValidatorFixtures),
    nameof(CompleteClaimsDocumentValidatorFixtures.IntegrityCases)
)]
internal class Given_VerifyCommand_invalid_complete_document(string file)
{
    private VerifyCommandFixture _fixture = null!;
    private byte[] _before = [];

    [SetUp]
    public async Task Setup()
    {
        _fixture = new();
        await File.WriteAllTextAsync(_fixture.InputPath, CompleteClaimsDocumentValidatorFixtures.Read(file));
        _before = await File.ReadAllBytesAsync(_fixture.InputPath);
        await _fixture.InvokeAsync();
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public void It_returns_contextual_validation_failure_before_http()
    {
        _fixture.ExitCode.Should().Be(1);
        _fixture.Output.Should().BeEmpty();
        _fixture.Http.Handler.Calls.Should().Be(0);
        var expected = CompleteClaimsDocumentValidatorFixtures.Failures.Single(failure =>
            failure.File == file
        );
        _fixture
            .Error.Should()
            .StartWith($"Verification failed: {expected.Kind} at ")
            .And.Contain(expected.Value);
        foreach (string path in expected.Paths)
        {
            _fixture.Error.Should().Contain(path);
        }
    }

    [Test]
    public void It_preserves_the_input_without_creating_files() => _fixture.AssertUnchanged(_before);
}

[TestFixture]
internal class Given_VerifyCommand_invalid_input_or_configuration
{
    [TestCase("{broken", "InvalidJson")]
    [TestCase("{}", "Schema")]
    [TestCase("[]", "Schema")]
    public async Task It_rejects_invalid_json_and_schema_before_http(string json, string kind)
    {
        using VerifyCommandFixture fixture = new();
        await File.WriteAllTextAsync(fixture.InputPath, json);
        byte[] before = await File.ReadAllBytesAsync(fixture.InputPath);
        await fixture.InvokeAsync();
        fixture.ExitCode.Should().Be(1);
        fixture.Output.Should().BeEmpty();
        fixture.Error.Should().StartWith($"Verification failed: {kind} at $");
        fixture.Http.Handler.Calls.Should().Be(0);
        fixture.AssertUnchanged(before);
    }

    [Test]
    public async Task It_reports_a_missing_input_without_http_or_new_files()
    {
        using VerifyCommandFixture fixture = new();
        File.Delete(fixture.InputPath);
        await fixture.InvokeAsync();
        fixture.ExitCode.Should().Be(1);
        fixture.Output.Should().BeEmpty();
        fixture.Error.Should().StartWith("Verification failed: ").And.Contain("claims.json");
        fixture.Http.Handler.Calls.Should().Be(0);
        Directory.GetFiles(fixture.DirectoryPath).Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_missing_credentials_without_http()
    {
        using VerifyCommandFixture fixture = new();
        Environment.SetEnvironmentVariable(fixture.Http.VariableName, null);
        byte[] before = await File.ReadAllBytesAsync(fixture.InputPath);
        await fixture.InvokeAsync();
        fixture.ExitCode.Should().Be(1);
        fixture.Output.Should().BeEmpty();
        fixture
            .Error.Should()
            .Be(
                $"Verification failed: The access-token environment variable is missing, empty, or unavailable.{Environment.NewLine}"
            );
        fixture.Http.Handler.Calls.Should().Be(0);
        fixture.AssertUnchanged(before);
    }
}

[TestFixture]
internal class Given_VerifyCommand_mismatched_metadata
{
    private static IEnumerable<TestCaseData> Differences()
    {
        yield return new(
            "set-missing",
            "Missing claim set: claim set \"ReviewSet\"; expected [\"ReviewSet\"]; actual <missing>."
        );
        yield return new(
            "set-extra",
            "Unexpected claim set: claim set \"Extra\"; expected <missing>; actual [\"Extra\"]."
        );
        yield return new(
            "claim-missing",
            "Missing claim: claim set \"ReviewSet\", claim \"urn:leaf\"; expected [\"urn:leaf\"]; actual <missing>."
        );
        yield return new(
            "claim-extra",
            "Unexpected claim: claim set \"ReviewSet\", claim \"urn:extra\"; expected <missing>; actual [\"urn:extra\"]."
        );
        yield return new(
            "action-missing",
            "Missing action: claim set \"ReviewSet\", claim \"urn:leaf\", action \"Read\"; expected [\"Read\"]; actual <missing>."
        );
        yield return new(
            "action-extra",
            "Unexpected action: claim set \"ReviewSet\", claim \"urn:leaf\", action \"Delete\"; expected <missing>; actual [\"Delete\"]."
        );
        yield return new(
            "strategy",
            "Different strategies: claim set \"ReviewSet\", claim \"urn:leaf\", action \"Read\"; expected [\"NamespaceBased\"]; actual [\"Custom\"]."
        );
        yield return new(
            "strategy-order",
            "Different strategies: claim set \"ReviewSet\", claim \"urn:leaf\", action \"Read\"; expected [\"Second\", \"First\", \"Second\"]; actual [\"First\", \"Second\", \"Second\"]."
        );
    }

    [TestCaseSource(nameof(Differences))]
    public async Task It_returns_two_with_an_exact_stable_stdout_report(string scenario, string line)
    {
        using VerifyCommandFixture fixture = new();
        var actual = JsonNode.Parse(fixture.Http.Handler.Body)!.AsArray();
        var set = actual[0]!;
        var actions = set["authorizations"]![0]!["actions"]!.AsArray();
        switch (scenario)
        {
            case "set-missing":
                actual.RemoveAt(0);
                break;
            case "set-extra":
                actual.Add(
                    JsonSerializer.SerializeToNode(
                        new ClaimSetMetadata("Extra", [], []),
                        ResolvedMetadataSerializerFixtures.JsonOptions
                    )
                );
                break;
            case "claim-missing":
                set["claims"]!.AsArray().Clear();
                break;
            case "claim-extra":
                set["claims"]!
                    .AsArray()
                    .Add(new JsonObject { ["name"] = "urn:extra", ["authorizationId"] = 7 });
                break;
            case "action-missing":
                actions.Clear();
                break;
            case "action-extra":
                actions.Add(
                    JsonSerializer.SerializeToNode(
                        EffectivePermissionsFixtures.Action("Delete"),
                        ResolvedMetadataSerializerFixtures.JsonOptions
                    )
                );
                break;
            case "strategy":
                actions[0]!["authorizationStrategies"]![0]!["name"] = "Custom";
                break;
            case "strategy-order":
                var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture.InputPath))!;
                var strategies = AuthorizationMetadataValidatorFixtures.Valid()[0]!["authorizations"]![0]![
                    "actions"
                ]![0]!["authorizationStrategies"]!;
                input["claimsHierarchy"]![0]!["defaultAuthorization"]!["actions"]![0]![
                    "authorizationStrategies"
                ] = strategies.DeepClone();
                await File.WriteAllTextAsync(fixture.InputPath, input.ToJsonString());
                actions[0]!["authorizationStrategies"] = new JsonArray(
                    strategies[1]!.DeepClone(),
                    strategies[0]!.DeepClone(),
                    strategies[2]!.DeepClone()
                );
                break;
            default:
                throw new InvalidOperationException(scenario);
        }
        fixture.Http.Handler.Body = actual.ToJsonString();
        byte[] before = await File.ReadAllBytesAsync(fixture.InputPath);
        await fixture.InvokeAsync();
        fixture.ExitCode.Should().Be(2);
        fixture.Error.Should().BeEmpty();
        fixture
            .Output.Should()
            .Be(
                $"CMS authorization metadata differs from the expected effective permissions.\n{line}{Environment.NewLine}"
            );
        string firstOutput = fixture.Output;
        await fixture.InvokeAsync();
        fixture.Output.Should().Be(firstOutput);
        fixture.Http.Handler.Calls.Should().Be(2);
        fixture.AssertUnchanged(before);
    }
}

[TestFixture]
internal class Given_VerifyCommand_failed_response
{
    [TestCase("invalid-reference", "CMS returned invalid authorization metadata.")]
    [TestCase("duplicate-uri", "CMS returned invalid authorization metadata.")]
    [TestCase("http", "CMS authorization metadata request returned HTTP 403.")]
    [TestCase("partial", "CMS authorization metadata request returned HTTP 206.")]
    [TestCase("transport", "CMS authorization metadata request could not be completed.")]
    [TestCase(
        "credential-reflection",
        "CMS authorization metadata contains credential material and cannot be reported."
    )]
    public async Task It_reports_only_a_safe_execution_failure(string scenario, string message)
    {
        using VerifyCommandFixture fixture = new();
        switch (scenario)
        {
            case "invalid-reference":
                fixture.Http.Handler.Body = AuthorizationMetadataValidatorFixtures
                    .CrossSetReference()
                    .ToJsonString();
                break;
            case "duplicate-uri":
                fixture.Http.Handler.Body = AuthorizationMetadataValidatorFixtures
                    .DuplicateClaim()
                    .ToJsonString();
                break;
            case "http":
                fixture.Http.Handler.StatusCode = HttpStatusCode.Forbidden;
                fixture.Http.Handler.Body = CmsAuthorizationMetadataClientFixture.Token;
                break;
            case "partial":
                fixture.Http.Handler.StatusCode = HttpStatusCode.PartialContent;
                break;
            case "transport":
                fixture.Http.Handler.Respond = () =>
                    throw new HttpRequestException(CmsAuthorizationMetadataClientFixture.Token);
                break;
            case "credential-reflection":
                fixture.Http.Handler.Body = fixture.Http.Handler.Body.Replace(
                    "ReviewSet",
                    CmsAuthorizationMetadataClientFixture.Token
                );
                break;
            default:
                throw new InvalidOperationException(scenario);
        }
        byte[] before = await File.ReadAllBytesAsync(fixture.InputPath);
        await fixture.InvokeAsync();
        fixture.ExitCode.Should().Be(1);
        fixture.Output.Should().BeEmpty();
        fixture.Error.Should().Be($"Verification failed: {message}{Environment.NewLine}");
        fixture.Http.Handler.Calls.Should().Be(1);
        fixture.AssertUnchanged(before);
    }
}

[TestFixture]
internal class Given_VerifyCommand_arguments
{
    [TestCase("--input")]
    [TestCase("--cms-url")]
    [TestCase("--access-token-env")]
    public async Task It_requires_each_required_argument_before_http(string missing)
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        string[] options = ["--input", "--cms-url", "--access-token-env"];
        string[] args =
        [
            "verify",
            .. options.Where(option => option != missing).SelectMany(option => new[] { option, "value" }),
        ];
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = await CommandHost.InvokeAsync(args, output, error, () => fixture.HttpClient);
        code.Should().Be(1);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain($"Option '{missing}' is required.");
        fixture.Handler.Calls.Should().Be(0);
    }

    [Test]
    public async Task It_exposes_only_the_specified_options_and_describes_outputs_and_exit_codes()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = await CommandHost.InvokeAsync(["verify", "--help"], output, error);
        code.Should().Be(0);
        error.ToString().Should().BeEmpty();
        string help = output.ToString();
        foreach (
            string text in new[]
            {
                "--input",
                "--cms-url",
                "--access-token-env",
                "--tenant",
                "0 = match",
                "2 = mismatch",
                "1 = invalid input",
                "stdout",
                "stderr",
                "cache freshness",
                "live Ed-Fi API enforcement",
            }
        )
        {
            help.Should().Contain(text);
        }
        var command = CommandHost
            .CreateCommand(TextWriter.Null)
            .Subcommands.Single(command => command.Name == "verify");
        command
            .Options.Select(option => option.Name)
            .Should()
            .Equal("--input", "--cms-url", "--access-token-env", "--tenant");
    }

    [TestCase("--output")]
    [TestCase("--force")]
    [TestCase("--access-token")]
    public async Task It_rejects_unadvertised_options_without_http(string option)
    {
        using VerifyCommandFixture fixture = new();
        byte[] before = await File.ReadAllBytesAsync(fixture.InputPath);
        await fixture.InvokeAsync(option);
        fixture.ExitCode.Should().Be(1);
        fixture.Output.Should().BeEmpty();
        fixture.Error.Should().Contain(option);
        fixture.Http.Handler.Calls.Should().Be(0);
        fixture.AssertUnchanged(before);
    }
}
