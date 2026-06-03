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
    public async Task It_selects_the_instance_whose_connection_string_targets_the_database()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 7; connectionString = 'host=h;port=5432;username=u;password=p;database=other_db;' },
                [pscustomobject]@{ id = 9; connectionString = 'host=h;port=5432;username=u;password=p;Database=edfi_relational;' }
            )
            Select-DmsInstanceId -DmsInstances $instances -DatabaseName 'edfi_relational'
            """
        );

        result.ExitCode.Should().Be(0);
        result.NormalizedOutput.Should().Contain("9");
    }

    [Test]
    public async Task It_fails_when_no_instance_targets_the_database()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 7; connectionString = 'host=h;port=5432;username=u;password=p;database=other_db;' }
            )
            Select-DmsInstanceId -DmsInstances $instances -DatabaseName 'edfi_relational'
            """
        );

        result.ExitCode.Should().NotBe(0);
        result
            .NormalizedOutput.Should()
            .Contain("No existing DMS instance targets database 'edfi_relational'");
    }

    [Test]
    public async Task It_matches_the_database_name_with_ordinal_case_sensitivity()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 7; connectionString = 'host=h;port=5432;username=u;password=p;database=EDFI_relational;' }
            )
            Select-DmsInstanceId -DmsInstances $instances -DatabaseName 'edfi_relational'
            """
        );

        result.ExitCode.Should().NotBe(0);
        result
            .NormalizedOutput.Should()
            .Contain("No existing DMS instance targets database 'edfi_relational'");
    }

    [Test]
    public async Task It_fails_fast_when_a_connection_string_is_missing()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 9; connectionString = 'host=h;port=5432;username=u;password=p;database=edfi_relational;' },
                [pscustomobject]@{ id = 7; connectionString = $null }
            )
            Select-DmsInstanceId -DmsInstances $instances -DatabaseName 'edfi_relational'
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("no readable connectionString");
    }

    [Test]
    public async Task It_fails_fast_when_a_connection_string_is_unparseable()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 7; connectionString = 'this is not a connection string' }
            )
            Select-DmsInstanceId -DmsInstances $instances -DatabaseName 'edfi_relational'
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("unparseable connectionString");
    }

    [Test]
    public async Task It_fails_fast_when_a_connection_string_has_no_database_key()
    {
        var result = await RunInModuleScope(
            """
            $instances = @(
                [pscustomobject]@{ id = 7; connectionString = 'host=h;port=5432;username=u;password=p;' }
            )
            Select-DmsInstanceId -DmsInstances $instances -DatabaseName 'edfi_relational'
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("does not specify a database");
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
