// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public partial class Given_Template_Management_Instance_Selection
{
    [Test]
    public async Task It_accepts_an_explicit_instance_id_even_when_the_connection_string_is_protected()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 9; connectionString = '/+upQZSq2rTHZGWDCIbAZ6aWEnTOCYSG7DiiBbqgJpY=' }
            )
            Resolve-DmsInstanceIdForTemplate -DmsInstances $instances -RequestedDmsInstanceId 9 -DatabaseNameBound $true
            """
        );

        result.ExitCode.Should().Be(0);
        result.NormalizedOutput.Should().Contain("9");
    }

    [Test]
    public async Task It_fails_when_the_explicit_instance_id_is_not_registered()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 7; connectionString = '/+upQZSq2rTHZGWDCIbAZ6aWEnTOCYSG7DiiBbqgJpY=' }
            )
            Resolve-DmsInstanceIdForTemplate -DmsInstances $instances -RequestedDmsInstanceId 9 -DatabaseNameBound $true
            """
        );

        result.ExitCode.Should().NotBe(0);
        result
            .NormalizedOutput.Should()
            .Contain("DMS instance id '9' is not registered in the Configuration Service");
    }

    [Test]
    public async Task It_requires_a_database_name_when_an_instance_id_is_provided_to_Build_Template()
    {
        var result = await RunInModuleScope(
            """
            Build-Template `
                -TemplateType Minimal `
                -DmsUrl 'http://localhost:1' `
                -CmsUrl 'http://localhost:1' `
                -MinimalSampleDataDirectory './' `
                -Extension 'ed-fi' `
                -ConfigFilePath './MinimalTemplateSettings.psd1' `
                -StandardVersion '5.2.0' `
                -PackageVersion '0.0.1' `
                -DmsInstanceId 9
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("-DmsInstanceId requires -DmsInstanceDatabaseName");
    }

    [Test]
    public async Task It_keeps_the_legacy_first_instance_behavior_when_no_new_parameters_are_bound()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 7; connectionString = '/+upQZSq2rTHZGWDCIbAZ6aWEnTOCYSG7DiiBbqgJpY=' },
                [pscustomobject]@{ id = 9; connectionString = '/another-protected-value=' }
            )
            Resolve-DmsInstanceIdForTemplate -DmsInstances $instances -DatabaseNameBound $false
            """
        );

        result.ExitCode.Should().Be(0);
        result.NormalizedOutput.Should().Contain("7");
    }

    [Test]
    public async Task It_fails_when_a_database_name_is_bound_without_an_instance_id_and_instances_exist()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 7; connectionString = '/+upQZSq2rTHZGWDCIbAZ6aWEnTOCYSG7DiiBbqgJpY=' }
            )
            Resolve-DmsInstanceIdForTemplate -DmsInstances $instances -DatabaseNameBound $true
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("Pass -DmsInstanceId");
    }

    [Test]
    public async Task It_signals_registration_is_needed_when_no_instances_exist()
    {
        var result = await RunInModuleScope(
            """
            $resolved = Resolve-DmsInstanceIdForTemplate -DmsInstances @() -DatabaseNameBound $true
            if ($null -eq $resolved) { 'REGISTER_NEW' } else { $resolved }
            """
        );

        result.ExitCode.Should().Be(0);
        result.NormalizedOutput.Should().Contain("REGISTER_NEW");
    }

    private async Task<ScriptResult> RunInModuleScope(string scriptBody)
    {
        var repositoryRoot = FindRepositoryRoot();
        var moduleDirectory = Path.Combine(repositoryRoot.FullName, "eng", "DatabaseTemplates");

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = moduleDirectory,
        };

        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        // -Command prelude: stop on errors, set PlainText rendering, and use NormalView for
        // errors so ConciseView's pipe-prefix line wrapping does not break substring assertions.
        // Template-Management.psm1 imports sibling modules with paths relative to the current
        // location, so the working directory must be the module directory.
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$ErrorActionPreference='Stop'; $PSStyle.OutputRendering='PlainText'; $ErrorView='NormalView'; "
                + "Import-Module ./Template-Management.psm1 -Force; "
                + "& (Get-Module Template-Management) { "
                + scriptBody
                + " }"
        );

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();

        var standardOutputTask = process!.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        string raw = await standardOutputTask + await standardErrorTask;
        return new ScriptResult(process.ExitCode, raw, NormalizePowerShellOutput(raw));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        while (
            currentDirectory is not null && !File.Exists(Path.Combine(currentDirectory.FullName, "LICENSE"))
        )
        {
            currentDirectory = currentDirectory.Parent;
        }

        return currentDirectory
            ?? throw new InvalidOperationException(
                "Could not locate repository root from the test assembly output."
            );
    }

    private static string NormalizePowerShellOutput(string output)
    {
        var withoutAnsi = AnsiEscapeRegex().Replace(output, "");
        var normalizedLines = withoutAnsi.Replace("\r\n", "\n").Replace('\r', '\n');

        return WhitespaceRegex().Replace(normalizedLines, " ").Trim();
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record ScriptResult(int ExitCode, string Output, string NormalizedOutput);
}
