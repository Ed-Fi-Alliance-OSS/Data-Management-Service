// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public partial class Given_E2E_Provisioning_Helper
{
    private string _scriptContents = null!;

    [SetUp]
    public void Setup()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot.FullName,
            "eng",
            "docker-compose",
            "provision-e2e-database.ps1"
        );

        _scriptContents = File.ReadAllText(scriptPath);
    }

    [Test]
    public void It_uses_the_resolved_postgres_user_for_readiness_and_reset_operations()
    {
        _scriptContents.Should().Contain("[string]$PostgresUsername");
        _scriptContents
            .Should()
            .Contain(
                "Wait-ForPostgresql -ContainerName $PostgresContainerName -PostgresUsername $postgresUsername"
            );
        _scriptContents.Should().Contain("-PostgresUsername $postgresUsername");
        HardcodedPostgresUserRegex().Matches(_scriptContents).Should().BeEmpty();
    }

    [Test]
    public async Task It_fails_fast_when_the_e2e_database_name_is_missing()
    {
        var result = await RunProvisioningHelper(
            """
            POSTGRES_PORT=5435
            POSTGRES_PASSWORD=abcdefgh1!
            POSTGRES_DB_NAME=edfi_datamanagementservice
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("Required environment value 'E2E_DATABASE_NAME'");
    }

    [Test]
    public async Task It_fails_fast_when_the_e2e_database_matches_the_bootstrap_database_name()
    {
        var result = await RunProvisioningHelper(
            """
            POSTGRES_PORT=5435
            POSTGRES_PASSWORD=abcdefgh1!
            POSTGRES_DB_NAME=edfi_datamanagementservice
            E2E_DATABASE_NAME=edfi_datamanagementservice
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("must be dedicated");
        result.NormalizedOutput.Should().Contain("POSTGRES_DB_NAME");
    }

    [Test]
    public async Task It_accepts_an_explicit_database_name_without_the_environment_key()
    {
        var result = await RunProvisioningHelper(
            """
            POSTGRES_PORT=5435
            POSTGRES_PASSWORD=abcdefgh1!
            POSTGRES_DB_NAME=route_context_one
            """,
            "route_context_one"
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("E2E database 'route_context_one'");
        result.NormalizedOutput.Should().Contain("must be dedicated");
        result.NormalizedOutput.Should().NotContain("Required environment value 'E2E_DATABASE_NAME'");
    }

    [Test]
    public async Task It_fails_fast_when_a_bootstrap_connection_string_targets_the_e2e_database()
    {
        var result = await RunProvisioningHelper(
            """
            POSTGRES_PORT=5435
            POSTGRES_PASSWORD=abcdefgh1!
            POSTGRES_DB_NAME=edfi_datamanagementservice
            E2E_DATABASE_NAME=edfi_datamanagementservice_e2e
            DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_e2e;NoResetOnClose=true;
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("must stay separate from DATABASE_CONNECTION_STRING");
    }

    [Test]
    public async Task It_fails_fast_when_the_e2e_database_targets_a_system_database()
    {
        var result = await RunProvisioningHelper(
            """
            POSTGRES_PORT=5435
            POSTGRES_PASSWORD=abcdefgh1!
            POSTGRES_DB_NAME=edfi_datamanagementservice
            E2E_DATABASE_NAME=postgres
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.NormalizedOutput.Should().Contain("reserved PostgreSQL system database");
        result.NormalizedOutput.Should().Contain("postgres");
    }

    private async Task<ScriptResult> RunProvisioningHelper(
        string environmentFileContents,
        string? databaseName = null
    )
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot.FullName,
            "eng",
            "docker-compose",
            "provision-e2e-database.ps1"
        );
        var environmentFilePath = Path.Combine(
            Path.GetTempPath(),
            $"e2e-provisioning-helper-{Guid.NewGuid():N}.env"
        );

        await File.WriteAllTextAsync(environmentFilePath, environmentFileContents);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = repositoryRoot.FullName,
            };

            startInfo.Environment["NO_COLOR"] = "1";
            startInfo.Environment["TERM"] = "dumb";

            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            // -Command prelude: stop on errors, set PlainText rendering, and use NormalView for
            // errors so ConciseView's pipe-prefix line wrapping does not break substring assertions.
            startInfo.ArgumentList.Add("-Command");
            var databaseNameArgument = databaseName is null
                ? string.Empty
                : $" -DatabaseName '{databaseName}'";
            startInfo.ArgumentList.Add(
                $"$ErrorActionPreference='Stop'; $PSStyle.OutputRendering='PlainText'; $ErrorView='NormalView'; "
                    + $"& '{scriptPath}' -EnvironmentFile '{environmentFilePath}' -Configuration Release{databaseNameArgument}"
            );

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();

            var standardOutputTask = process!.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string raw = await standardOutputTask + await standardErrorTask;
            return new ScriptResult(process.ExitCode, raw, NormalizePowerShellOutput(raw));
        }
        finally
        {
            if (File.Exists(environmentFilePath))
            {
                File.Delete(environmentFilePath);
            }
        }
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

    [GeneratedRegex(@"-U\s+postgres\b", RegexOptions.CultureInvariant)]
    private static partial Regex HardcodedPostgresUserRegex();

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record ScriptResult(int ExitCode, string Output, string NormalizedOutput);
}
