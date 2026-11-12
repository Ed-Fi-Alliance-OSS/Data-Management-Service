// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

[TestFixture(false)]
[TestFixture(true)]
public class Given_conversion_command_arguments(bool force)
{
    private ParseResult _result = null!;

    [SetUp]
    public void Setup()
    {
        string[] args = ["convert-ods-xml", "--input", "export file.xml", "--output", "claims file.json"];
        _result = CommandHost.CreateCommand(TextWriter.Null).Parse(force ? [.. args, "--force"] : args);
    }

    [Test]
    public void It_accepts_the_conversion_command() => _result.Errors.Should().BeEmpty();

    [Test]
    public void It_preserves_the_input_path() =>
        _result.GetValue<string>("--input").Should().Be("export file.xml");

    [Test]
    public void It_preserves_the_output_path() =>
        _result.GetValue<string>("--output").Should().Be("claims file.json");

    [Test]
    public void It_parses_the_force_option() => _result.GetValue<bool>("--force").Should().Be(force);
}

[TestFixture("--input", "--output")]
[TestFixture("--output", "--input")]
public class Given_a_missing_required_path(string suppliedOption, string missingOption)
{
    private int _exitCode;
    private string _error = string.Empty;

    [SetUp]
    public async Task Setup()
    {
        using var error = new StringWriter();
        _exitCode = await CommandHost.InvokeAsync(
            ["convert-ods-xml", suppliedOption, "some-file"],
            TextWriter.Null,
            error
        );
        _error = error.ToString();
    }

    [Test]
    public void It_returns_a_failure() => _exitCode.Should().Be(1);

    [Test]
    public void It_identifies_the_missing_path() => _error.Should().Contain(missingOption);
}

[TestFixture(false)]
[TestFixture(true)]
public class Given_a_help_request(bool subcommand)
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
            subcommand ? ["convert-ods-xml", "--help"] : ["--help"],
            output,
            error
        );
        _output = output.ToString();
        _error = error.ToString();
    }

    [Test]
    public void It_succeeds_without_input_or_output_paths() => _exitCode.Should().Be(0);

    [Test]
    public void It_describes_the_command_contract()
    {
        if (subcommand)
        {
            _output.Should().Contain("--input").And.Contain("--output").And.Contain("--force");
        }
        else
        {
            _output.Should().Contain("convert-ods-xml").And.Contain("--version");
        }
    }

    [Test]
    public void It_writes_no_error() => _error.Should().BeEmpty();
}

[TestFixture]
public class Given_a_version_request
{
    private int _exitCode;
    private string _output = string.Empty;

    [SetUp]
    public async Task Setup()
    {
        // System.CommandLine reads the entry assembly version; run the tool, not the test host.
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { typeof(CommandHost).Assembly.Location, "--version" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.Start();
        _output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        _exitCode = process.ExitCode;
    }

    [Test]
    public void It_succeeds() => _exitCode.Should().Be(0);

    [Test]
    public void It_reports_the_tool_assembly_version() =>
        _output
            .Should()
            .Be(
                typeof(CommandHost)
                    .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                    .InformationalVersion
            );
}
