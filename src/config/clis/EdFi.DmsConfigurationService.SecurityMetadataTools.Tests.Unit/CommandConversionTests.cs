// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal sealed class CommandWorkspace : IDisposable
{
    public string DirectoryPath { get; } =
        Path.Combine(Path.GetTempPath(), $"security metadata tests {Guid.NewGuid():N}");
    public string InputPath => Path.Combine(DirectoryPath, "input export.xml");
    public string OutputPath => Path.Combine(DirectoryPath, "output claims.json");
    public static byte[] Sentinel => [0, 255, 13, 10, 65, 128];

    public CommandWorkspace() => Directory.CreateDirectory(DirectoryPath);

    public static string Fixture(string directory, string name) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", directory, name);

    public Task<CommandResult> ConvertAsync(bool force = false) =>
        RunAsync(
            force
                ? ["convert-ods-xml", "--input", InputPath, "--output", OutputPath, "--force"]
                : ["convert-ods-xml", "--input", InputPath, "--output", OutputPath]
        );

    public static async Task<CommandResult> RunAsync(string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { typeof(CommandHost).Assembly.Location },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, await output, await error);
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}

internal sealed record CommandResult(int ExitCode, string Output, string Error);

[TestFixture(false)]
[TestFixture(true)]
public class Given_a_successful_conversion_command(bool overwrite)
{
    private CommandWorkspace _workspace = null!;
    private CommandResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        _workspace = new();
        File.Copy(
            CommandWorkspace.Fixture("OdsSecurityMetadata", "representative.xml"),
            _workspace.InputPath
        );
        if (overwrite)
        {
            await File.WriteAllBytesAsync(_workspace.OutputPath, CommandWorkspace.Sentinel);
        }

        _result = await _workspace.ConvertAsync(force: overwrite);
    }

    [TearDown]
    public void Teardown() => _workspace.Dispose();

    [Test]
    public void It_exits_successfully_without_console_output()
    {
        _result.ExitCode.Should().Be(0, _result.Error);
        _result.Output.Should().BeEmpty();
        _result.Error.Should().BeEmpty();
    }

    [Test]
    public void It_writes_the_complete_independently_prepared_expected_document()
    {
        JsonNode expected = JsonNode.Parse(
            File.ReadAllText(CommandWorkspace.Fixture("OdsSecurityMetadata", "expected-claims.json"))
        )!;
        JsonNode actual = JsonNode.Parse(File.ReadAllText(_workspace.OutputPath))!;
        JsonNode.DeepEquals(actual, expected).Should().BeTrue();
    }

    [Test]
    public void It_leaves_no_staging_files() =>
        Directory.GetFiles(_workspace.DirectoryPath, "*.tmp").Should().BeEmpty();
}

[TestFixture("malformed", false)]
[TestFixture("malformed", true)]
[TestFixture("cms-validation", false)]
[TestFixture("cms-validation", true)]
[TestFixture("validationRuleSet", false)]
[TestFixture("validationRuleSet", true)]
[TestFixture("validationRuleSetOverride", false)]
[TestFixture("validationRuleSetOverride", true)]
[TestFixture("name-collision", false)]
[TestFixture("name-collision", true)]
[TestFixture("unicode-validation", false)]
[TestFixture("unicode-validation", true)]
[TestFixture("control-name", false)]
[TestFixture("control-name", true)]
[TestFixture("whitespace-name", false)]
[TestFixture("whitespace-name", true)]
[TestFixture("missing-input", false)]
[TestFixture("missing-input", true)]
[TestFixture("unreadable-input", false)]
[TestFixture("unreadable-input", true)]
[TestFixture("invalid-utf8", false)]
[TestFixture("invalid-utf8", true)]
[TestFixture("dtd", false)]
[TestFixture("dtd", true)]
public class Given_a_failed_conversion_command(string failure, bool existingOutput)
{
    private CommandWorkspace _workspace = null!;
    private CommandResult _result = null!;
    private string _expectedDiagnostic = string.Empty;

    [SetUp]
    public async Task Setup()
    {
        _workspace = new();
        string xml = await File.ReadAllTextAsync(CommandWorkspace.Fixture("Validation", "valid-policy.xml"));
        switch (failure)
        {
            case "malformed":
                xml = await File.ReadAllTextAsync(CommandWorkspace.Fixture("Validation", "malformed.xml"));
                _expectedDiagnostic = "Invalid security metadata XML";
                break;
            case "cms-validation":
                xml = await File.ReadAllTextAsync(
                    CommandWorkspace.Fixture("Validation", "invalid-generated-actions.xml")
                );
                _expectedDiagnostic = "failed CMS validation";
                break;
            case "validationRuleSet":
                xml = xml.Replace(
                    "<Action name=\"Read\">",
                    "<Action name=\"Read\" validationRuleSet=\"unsupported\">",
                    StringComparison.Ordinal
                );
                _expectedDiagnostic = "validationRuleSet";
                break;
            case "validationRuleSetOverride":
                int claimSetsStart = xml.IndexOf("<ClaimSets>", StringComparison.Ordinal);
                xml =
                    xml[..claimSetsStart]
                    + xml[claimSetsStart..]
                        .Replace(
                            "<Action name=\"Read\">",
                            "<Action name=\"Read\" validationRuleSetOverride=\"unsupported\">",
                            StringComparison.Ordinal
                        );
                _expectedDiagnostic = "validationRuleSetOverride";
                break;
            case "name-collision":
                xml = xml.Replace("name=\"Reader\"", "name=\"École Reader\"", StringComparison.Ordinal)
                    .Replace(
                        "<ClaimSets />",
                        "<ClaimSets><ClaimSet name=\"écoleReader\"><Actions><Action name=\"Read\" /></Actions></ClaimSet></ClaimSets>",
                        StringComparison.Ordinal
                    );
                _expectedDiagnostic =
                    "Claim-set names \"École Reader\" and \"écoleReader\" collide after normalization to \"écoleReader\" (case-insensitive comparison).";
                break;
            case "unicode-validation":
                xml = xml.Replace(
                    "name=\"parent\"",
                    "name=\"東京 École&#xA;Reader\" Échec=\"unsupported\"",
                    StringComparison.Ordinal
                );
                _expectedDiagnostic =
                    "Unsupported attribute \"Échec\". At /SecurityMetadata/Claims/Claim[@name=\"東京 École\\u000AReader\"]";
                break;
            case "missing-input":
            case "unreadable-input":
                _expectedDiagnostic = "input export.xml";
                break;
            case "control-name":
                xml = xml.Replace(
                    "name=\"Reader\"",
                    "name=\"École&#x9;&#xD;&#xA;&#x85;&#x2028;&#x2029;&#x202E;&#x200B;Reader\"",
                    StringComparison.Ordinal
                );
                _expectedDiagnostic =
                    "Claim-set name \"École\\u0009\\u000D\\u000A\\u0085\\u2028\\u2029\\u202E\\u200BReader\"";
                break;
            case "whitespace-name":
                xml = xml.Replace("name=\"Reader\"", "name=\"Reader&#xA0;Name\"", StringComparison.Ordinal);
                _expectedDiagnostic = "whitespace";
                break;
            case "invalid-utf8":
                _expectedDiagnostic = "Invalid security metadata XML at line";
                break;
            case "dtd":
                xml = "<!DOCTYPE SecurityMetadata [<!ENTITY policy 'Reader'>]>" + xml;
                _expectedDiagnostic = "DTD is prohibited";
                break;
            default:
                throw new InvalidOperationException($"Unknown test case: {failure}");
        }

        if (failure == "unreadable-input")
        {
            Directory.CreateDirectory(_workspace.InputPath);
        }
        else if (failure == "invalid-utf8")
        {
            byte[] bytes = Encoding.UTF8.GetBytes(xml);
            // Corrupt a policy attribute, leaving the surrounding XML structure intact.
            int nameStart = xml.IndexOf("Reader", StringComparison.Ordinal);
            bytes[Encoding.UTF8.GetByteCount(xml[..nameStart])] = 0xFF;
            await File.WriteAllBytesAsync(_workspace.InputPath, bytes);
        }
        else if (failure != "missing-input")
        {
            await File.WriteAllTextAsync(_workspace.InputPath, xml);
        }

        if (existingOutput)
        {
            await File.WriteAllBytesAsync(_workspace.OutputPath, CommandWorkspace.Sentinel);
        }

        // Force must never bypass conversion or validation, or truncate an existing destination.
        _result = await _workspace.ConvertAsync(force: true);
    }

    [TearDown]
    public void Teardown() => _workspace.Dispose();

    [Test]
    public void It_reports_the_failure_on_stderr_only()
    {
        _result.ExitCode.Should().Be(1);
        _result.Output.Should().BeEmpty();
        _result.Error.Should().Contain(_expectedDiagnostic).And.NotContain("Unhandled exception");
        _result.Error.TrimEnd('\r', '\n').Should().NotContain("\n").And.NotContain("\r");
    }

    [Test]
    public void It_preserves_the_destination()
    {
        if (existingOutput)
        {
            File.ReadAllBytes(_workspace.OutputPath).Should().Equal(CommandWorkspace.Sentinel);
        }
        else
        {
            File.Exists(_workspace.OutputPath).Should().BeFalse();
        }
    }

    [Test]
    public void It_creates_no_staging_files() =>
        Directory.GetFiles(_workspace.DirectoryPath, "*.tmp").Should().BeEmpty();
}

[TestFixture("existing-output", "--force")]
[TestFixture("directory-output", "Conversion failed")]
[TestFixture("missing-parent", "Conversion failed")]
[TestFixture("empty-output", "Conversion failed")]
public class Given_an_unwritable_conversion_destination(string failure, string diagnostic)
{
    private CommandWorkspace _workspace = null!;
    private CommandResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        _workspace = new();
        File.Copy(CommandWorkspace.Fixture("Validation", "valid-policy.xml"), _workspace.InputPath);
        string destination = _workspace.OutputPath;
        switch (failure)
        {
            case "existing-output":
                await File.WriteAllBytesAsync(destination, CommandWorkspace.Sentinel);
                break;
            case "directory-output":
                Directory.CreateDirectory(destination);
                await File.WriteAllBytesAsync(
                    Path.Combine(destination, "sentinel"),
                    CommandWorkspace.Sentinel
                );
                break;
            case "missing-parent":
                destination = Path.Combine(_workspace.DirectoryPath, "missing", "claims.json");
                break;
            case "empty-output":
                destination = string.Empty;
                break;
        }

        _result = await CommandWorkspace.RunAsync([
            "convert-ods-xml",
            "--input",
            _workspace.InputPath,
            "--output",
            destination,
        ]);
    }

    [TearDown]
    public void Teardown() => _workspace.Dispose();

    [Test]
    public void It_fails_cleanly()
    {
        _result.ExitCode.Should().Be(1);
        _result.Output.Should().BeEmpty();
        _result.Error.Should().Contain(diagnostic).And.NotContain("Unhandled exception");
    }

    [Test]
    public void It_preserves_existing_content_and_leaves_no_staging_files()
    {
        string[] expectedFiles = failure switch
        {
            "existing-output" => [_workspace.InputPath, _workspace.OutputPath],
            "directory-output" => [_workspace.InputPath, Path.Combine(_workspace.OutputPath, "sentinel")],
            _ => [_workspace.InputPath],
        };
        Directory
            .GetFiles(_workspace.DirectoryPath, "*", SearchOption.AllDirectories)
            .Should()
            .BeEquivalentTo(expectedFiles);
        foreach (string file in expectedFiles.Where(file => file != _workspace.InputPath))
        {
            File.ReadAllBytes(file).Should().Equal(CommandWorkspace.Sentinel);
        }
    }
}

[TestFixture("unknown-command")]
[TestFixture("missing-input")]
[TestFixture("missing-output")]
[TestFixture("missing-value")]
[TestFixture("--command")]
[TestFixture("ParseXml")]
[TestFixture("--outputFormat")]
[TestFixture("--skipAuths")]
[TestFixture("Transform")]
public class Given_rejected_command_arguments(string argument)
{
    private CommandResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        string[] arguments = argument switch
        {
            "missing-input" => ["convert-ods-xml", "--output", "unused.json"],
            "missing-output" => ["convert-ods-xml", "--input", "unused.xml"],
            "missing-value" => ["convert-ods-xml", "--output", "unused.json", "--input"],
            "--command" => ["--command", "ParseXml"],
            "--outputFormat" or "--skipAuths" =>
            [
                "convert-ods-xml",
                "--input",
                "unused.xml",
                "--output",
                "unused.json",
                argument,
            ],
            _ => [argument],
        };
        _result = await CommandWorkspace.RunAsync(arguments);
    }

    [Test]
    public void It_reports_a_command_error_without_running_conversion()
    {
        _result.ExitCode.Should().Be(1);
        string rejectedArgument = argument switch
        {
            "missing-input" or "missing-value" => "--input",
            "missing-output" => "--output",
            _ => argument,
        };
        _result
            .Error.Should()
            .Contain(rejectedArgument)
            .And.NotContain("Conversion failed")
            .And.NotContain("Unhandled exception");
        _result.Output.Should().NotContain("claimSets");
    }
}
