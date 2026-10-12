// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

[TestFixture("Debug", "net10.0")]
[TestFixture("Release", "net10.0")]
[TestFixture("Release", "net11.0")]
[Category("ApiIntegration")]
[Category("PostgresqlIntegration")]
[Category("MssqlIntegration")]
public class Given_schema_restamp_tool_build_outputs(string configuration, string framework)
{
    private string _repositoryDirectory = null!;
    private string _testAssemblyDirectory = null!;
    private string _expectedAssemblyPath = null!;
    private string _resolvedAssemblyPath = null!;

    [SetUp]
    public void Setup()
    {
        _repositoryDirectory = Path.Combine(Path.GetTempPath(), $"schema-restamp-path-{Guid.NewGuid():N}");
        string dmsDirectory = Path.Combine(_repositoryDirectory, "src", "dms");
        Directory.CreateDirectory(dmsDirectory);
        File.WriteAllText(Path.Combine(dmsDirectory, "EdFi.DataManagementService.sln"), string.Empty);
        _testAssemblyDirectory = Path.Combine(
            dmsDirectory,
            "tests",
            "EdFi.DataManagementService.Tests.Integration",
            "bin",
            configuration,
            framework
        );
        Directory.CreateDirectory(_testAssemblyDirectory);

        (string Configuration, string Framework)[] outputs =
        [
            ("Debug", "net10.0"),
            ("Release", "net10.0"),
            ("Release", "net11.0"),
        ];
        foreach (var (outputConfiguration, outputFramework) in outputs)
        {
            string outputDirectory = Path.Combine(
                dmsDirectory,
                "clis",
                "EdFi.DataManagementService.SchemaTools",
                "bin",
                outputConfiguration,
                outputFramework
            );
            Directory.CreateDirectory(outputDirectory);
            string assemblyPath = Path.Combine(outputDirectory, "api-schema-tools.dll");
            File.WriteAllText(assemblyPath, string.Empty);
            if (outputConfiguration == configuration && outputFramework == framework)
            {
                _expectedAssemblyPath = assemblyPath;
            }
        }

        _resolvedAssemblyPath = SchemaRestampMigrationScenario.ResolveSchemaToolsAssemblyPath(
            _testAssemblyDirectory
        );
    }

    [Test]
    public void It_selects_the_matching_configuration_and_framework_among_competing_outputs() =>
        _resolvedAssemblyPath.Should().Be(_expectedAssemblyPath);

    [Test]
    public void It_rejects_a_missing_matching_artifact_instead_of_using_another_build()
    {
        File.Delete(_expectedAssemblyPath);
        Func<string> action = () =>
            SchemaRestampMigrationScenario.ResolveSchemaToolsAssemblyPath(_testAssemblyDirectory);

        action.Should().Throw<FileNotFoundException>().Which.FileName.Should().Be(_expectedAssemblyPath);
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_repositoryDirectory, recursive: true);
}

[TestFixture]
[Category("ApiIntegration")]
[Category("PostgresqlIntegration")]
[Category("MssqlIntegration")]
public class Given_an_explicit_schema_restamp_tool_path
{
    private string _repositoryDirectory = null!;
    private string _testAssemblyDirectory = null!;
    private string _expectedAssemblyPath = null!;
    private string _resolvedAssemblyPath = null!;

    [SetUp]
    public void Setup()
    {
        _repositoryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"schema-restamp-explicit-{Guid.NewGuid():N}"
        );
        string dmsDirectory = Path.Combine(_repositoryDirectory, "src", "dms");
        Directory.CreateDirectory(dmsDirectory);
        File.WriteAllText(Path.Combine(dmsDirectory, "EdFi.DataManagementService.sln"), string.Empty);

        string artifactDirectory = Path.Combine(
            _repositoryDirectory,
            "TestArtifacts",
            "integration-test-assemblies"
        );
        _testAssemblyDirectory = Path.Combine(artifactDirectory, "dms-api");
        Directory.CreateDirectory(_testAssemblyDirectory);
        string stagedToolDirectory = Path.Combine(
            artifactDirectory,
            "src",
            "dms",
            "clis",
            "EdFi.DataManagementService.SchemaTools",
            "bin",
            "Release",
            "net10.0"
        );
        Directory.CreateDirectory(stagedToolDirectory);
        _expectedAssemblyPath = Path.Combine(stagedToolDirectory, "api-schema-tools.dll");
        File.WriteAllText(_expectedAssemblyPath, string.Empty);

        // A directory-derived lookup would select this unrelated build instead of the staged tool.
        string unrelatedToolDirectory = Path.Combine(
            dmsDirectory,
            "clis",
            "EdFi.DataManagementService.SchemaTools",
            "bin",
            "integration-test-assemblies",
            "dms-api"
        );
        Directory.CreateDirectory(unrelatedToolDirectory);
        File.WriteAllText(Path.Combine(unrelatedToolDirectory, "api-schema-tools.dll"), string.Empty);

        _resolvedAssemblyPath = SchemaRestampMigrationScenario.ResolveSchemaToolsAssemblyPath(
            _testAssemblyDirectory,
            _expectedAssemblyPath
        );
    }

    [Test]
    public void It_uses_the_explicit_tool_path_with_a_flattened_test_artifact() =>
        _resolvedAssemblyPath.Should().Be(_expectedAssemblyPath);

    [Test]
    public void It_rejects_a_missing_explicit_tool_instead_of_selecting_an_unrelated_build()
    {
        File.Delete(_expectedAssemblyPath);
        Func<string> action = () =>
            SchemaRestampMigrationScenario.ResolveSchemaToolsAssemblyPath(
                _testAssemblyDirectory,
                _expectedAssemblyPath
            );

        action.Should().Throw<FileNotFoundException>().Which.FileName.Should().Be(_expectedAssemblyPath);
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_repositoryDirectory, recursive: true);
}

[TestFixture]
[Category("ApiIntegration")]
[Category("PostgresqlIntegration")]
[Category("MssqlIntegration")]
public class Given_a_cancelled_schema_restamp_tool_process
{
    private Process _process = null!;

    [SetUp]
    public async Task Setup()
    {
        // The child signals readiness, then blocks on its open stdin pipe. Cancellation
        // follows the signal rather than relying on the relative timing of timers.
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        startInfo.ArgumentList.Add(
            OperatingSystem.IsWindows() ? "echo ready & set /p wait=" : "printf 'ready\\n'; read wait"
        );
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start child.");
        try
        {
            using var cancellation = new CancellationTokenSource();
            Task execution = SchemaRestampMigrationScenario.WaitForToolExitAsync(
                _process,
                cancellation.Token
            );
            string ready =
                await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))
                ?? string.Empty;
            ready.TrimEnd().Should().Be("ready");
            _process.HasExited.Should().BeFalse();
            await cancellation.CancelAsync();
            Func<Task> action = () => execution.WaitAsync(TimeSpan.FromSeconds(30));
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    [Test]
    public void It_terminates_the_blocked_child_before_reporting_cancellation() =>
        _process.HasExited.Should().BeTrue();

    [TearDown]
    public void Cleanup()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10000);
        }
        _process.Dispose();
    }
}
