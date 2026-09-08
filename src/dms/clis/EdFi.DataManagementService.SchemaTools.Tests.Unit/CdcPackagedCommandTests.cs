// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

/// <summary>
/// Runs the shipped command host in a separate process with a controlled controller boundary, plus
/// the production executable's parser, configuration and cancellation paths. Setting the directory
/// selects dotnet publish output; absence uses build output. No help or documentation assertions.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_Cdc_packaged_command
{
    private string _directory = null!;
    private string _driver = null!;
    private string _temporary = null!;
    private const string Sentinel = "packaged-secret-sentinel";

    [OneTimeSetUp]
    public void Setup()
    {
        _directory =
            Environment.GetEnvironmentVariable("DMS_CDC_PACKAGED_DIRECTORY")
            ?? TestContext.CurrentContext.TestDirectory;
        _temporary = Path.Combine(Path.GetTempPath(), "cdc-packaged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporary);
        _driver = Path.Combine(_directory, "CdcContractDriver-" + Guid.NewGuid().ToString("N") + ".dll");
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat(Directory.GetFiles(_directory, "*.dll"))
            .GroupBy(Path.GetFileName)
            .Select(g => g.Last());
        var references = paths.Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(_driver),
            [CSharpSyntaxTree.ParseText(Driver)],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );
        var emitted = compilation.Emit(_driver);
        emitted.Success.Should().BeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        File.Delete(_driver);
        Directory.Delete(_temporary, true);
    }

    [TestCase("success", 0)]
    [TestCase("failure", 1)]
    [TestCase("cancel", 130)]
    public async Task It_preserves_one_stdout_value_from_the_packaged_host(string mode, int expected)
    {
        var result = await RunAsync(
            _driver,
            [mode, "cdc", "stop", "--settings", Sentinel, "--state-path", "/state", "--json"]
        );
        AssertResult(result, expected);
    }

    [Test]
    public async Task It_sanitizes_production_executable_parse_errors()
    {
        var result = await RunAsync(
            Path.Combine(_directory, "api-schema-tools.dll"),
            ["cdc", "replace-source", "--password", Sentinel, "--json"]
        );
        AssertResult(result, 2);
    }

    [Test]
    public async Task It_sanitizes_production_configuration_errors_even_with_verbose_enabled()
    {
        string path = Path.Combine(_temporary, "invalid.json");
        await File.WriteAllTextAsync(path, "{\"Cdc\":{\"Provider\":\"" + Sentinel + "\"}}");
        var result = await RunAsync(
            Path.Combine(_directory, "api-schema-tools.dll"),
            ["--verbose", "cdc", "validate", "--settings", path, "--state-path", _temporary, "--json"]
        );
        AssertResult(result, 2);
    }

    [Test]
    public async Task It_does_not_route_an_ordinary_schema_argument_named_cdc_to_the_controller()
    {
        var result = await RunAsync(
            Path.Combine(_directory, "api-schema-tools.dll"),
            ["hash", "--schema", "cdc"]
        );
        result.Item1.Should().Be(1);
        result.Item2.Should().NotContain("\"operation\"");
    }

    [Test]
    [Platform(Exclude = "Win", Reason = "Exercises the production SIGTERM path.")]
    public async Task It_cancels_the_production_executable_while_waiting_for_the_controller_lock()
    {
        string state = Path.Combine(_temporary, "state");
        string path = Path.Combine(_temporary, "settings.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["AppSettings:Datastore"] = "postgresql",
                    ["DocumentCache:Targets:0:DataStoreId"] = "42",
                    ["Cdc:LagThresholdMilliseconds"] = "5000",
                    ["Cdc:Provider"] = "postgresql",
                    ["Cdc:DeploymentKey"] = "local",
                    ["Cdc:DataStoreId"] = "42",
                    ["Cdc:InstanceKey"] = "datastore-42",
                    ["Cdc:Generation"] = 1,
                    ["Cdc:Compose:Project"] = "selected",
                    ["Cdc:Compose:File"] = Sentinel,
                    ["Cdc:Compose:EnvironmentFile"] = Sentinel,
                    ["Cdc:Compose:BrokerSizeOverrideFile"] = Sentinel,
                    ["Cdc:SetupConnectionString"] =
                        "Host=localhost;Database=test;Username=test;Password=" + Sentinel,
                    ["Cdc:DurabilityProfile"] = "LocalSingleBroker",
                    ["Cdc:AuthorizationProfile"] = "AuthorizationDisabledLocal",
                }
            )
        );
        await using var held = await new LocalCdcWorkflowJournalStore(state).AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            default
        );
        using var process = Start(
            Path.Combine(_directory, "api-schema-tools.dll"),
            ["cdc", "validate", "--settings", path, "--state-path", state, "--json"]
        );
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.Delay(1500);
        process.HasExited.Should().BeFalse("the real command must wait on the retained state-root lock");
        using var signal = Process.Start(
            new ProcessStartInfo("kill")
            {
                ArgumentList =
                {
                    "-TERM",
                    process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            }
        )!;
        await signal.WaitForExitAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        AssertResult((process.ExitCode, await output, await error), 130);
    }

    private static void AssertResult((int Code, string Output, string Error) result, int expected)
    {
        result.Code.Should().Be(expected, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        json.RootElement.GetProperty("exitCode").GetInt32().Should().Be(expected);
        json.RootElement.GetProperty("succeeded").GetBoolean().Should().Be(expected == 0);
        (result.Output + result.Error).Should().NotContain(Sentinel);
    }

    private async Task<(int, string, string)> RunAsync(string assembly, string[] args)
    {
        using var process = Start(assembly, args);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        return (process.ExitCode, await output, await error);
    }

    private Process Start(string assembly, string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _temporary,
        };
        foreach (
            var argument in new[]
            {
                "exec",
                "--runtimeconfig",
                Path.Combine(_directory, "api-schema-tools.runtimeconfig.json"),
                "--depsfile",
                Path.Combine(_directory, "api-schema-tools.deps.json"),
                assembly,
            }.Concat(args)
        )
        {
            info.ArgumentList.Add(argument);
        }
        return Process.Start(info)!;
    }

    private const string Driver = """
        using System;
        using System.IO;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using EdFi.DataManagementService.Backend.Cdc;
        using EdFi.DataManagementService.SchemaTools.Cdc;
        class Driver
        {
            static Task<int> Main(string[] args) => CdcCommandHost.InvokeAsync(args.Skip(1).ToArray(), new Runner(args[0]), Console.Out, Console.Error);
            sealed class Runner(string mode) : ICdcCommandRunner
            {
                public Task<CdcCommandResult> RunAsync(CdcCommandInvocation invocation, TextWriter progress, CancellationToken token)
                {
                    if (mode == "failure") throw new InvalidOperationException("packaged-secret-sentinel");
                    if (mode == "cancel") throw new OperationCanceledException("packaged-secret-sentinel");
                    progress.WriteLine("controller completed");
                    return Task.FromResult(new CdcCommandResult("stop", true, 0, Array.Empty<CdcDeploymentDiagnostic>()));
                }
            }
        }
        """;
}
