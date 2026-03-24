// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

public static class CliTestHelper
{
    private static readonly TimeSpan _processTimeout = TimeSpan.FromMinutes(2);

    public static (int ExitCode, string Output, string Error) RunProcess(
        string fileName,
        IEnumerable<string> arguments,
        IDictionary<string, string>? environmentVariables = null
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = Process.Start(startInfo)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)_processTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);

            var partialOut = outputTask.IsCompleted ? outputTask.Result : "(not captured)";
            var partialErr = errorTask.IsCompleted ? errorTask.Result : "(not captured)";

            Assert.Fail(
                $"Process '{fileName}' timed out after {_processTimeout.TotalSeconds}s."
                    + $"\nArgs: {string.Join(" ", arguments)}"
                    + $"\nstdout: {partialOut}\nstderr: {partialErr}"
            );
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        return (process.ExitCode, output, error);
    }

    public static string GetExecutablePath()
    {
        var assemblyLocation = typeof(CliTestHelper).Assembly.Location;
        var testBinDir = Path.GetDirectoryName(assemblyLocation)!;

        // Extract configuration and framework from current test assembly path
        // Path structure: .../bin/{Configuration}/{Framework}/
        var frameworkDir = new DirectoryInfo(testBinDir);
        var configurationDir = frameworkDir.Parent!;
        var framework = frameworkDir.Name;
        var configuration = configurationDir.Name;

        // Navigate from test bin to SchemaTools bin (same configuration)
        var schemaToolsBinDir = Path.Combine(
            testBinDir,
            "..",
            "..",
            "..",
            "..",
            "EdFi.DataManagementService.SchemaTools",
            "bin",
            configuration,
            framework
        );

        var exePath = Path.Combine(schemaToolsBinDir, "dms-schema.exe");
        if (!File.Exists(exePath))
        {
            // Try .dll with dotnet on non-Windows
            exePath = Path.Combine(schemaToolsBinDir, "dms-schema.dll");
        }

        return Path.GetFullPath(exePath);
    }

    public static (int ExitCode, string Output, string Error) RunCli(params string[] args)
    {
        var exePath = GetExecutablePath();

        if (exePath.EndsWith(".dll"))
        {
            return RunProcess("dotnet", [exePath, .. args]);
        }

        return RunProcess(exePath, args);
    }

    public static string[] GetAuthoritativeSchemaPaths()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(CliTestHelper).Assembly.Location)!;

        // Navigate from test bin to backend Fixtures
        // Path: .../clis/SchemaTools.Tests.Integration/bin/Debug/net10.0/
        // Go up 5 levels to reach .../dms/, then down to backend/Fixtures
        var fixturesDir = Path.Combine(
            assemblyDir,
            "..",
            "..",
            "..",
            "..",
            "..",
            "backend",
            "Fixtures"
        );

        return
        [
            Path.GetFullPath(Path.Combine(fixturesDir,
                "authoritative",
                "ds-5.2",
                "inputs",
                "ds-5.2-api-schema-authoritative.json"
            )),
            Path.GetFullPath(Path.Combine(fixturesDir,
                "authoritative",
                "sample",
                "inputs",
                "sample-api-schema-authoritative.json"
            )),
        ];
    }

    public static string GetMinimalSchemaPath()
    {
        var assemblyLocation = typeof(CliTestHelper).Assembly.Location;
        var testBinDir = Path.GetDirectoryName(assemblyLocation)!;
        return Path.Combine(testBinDir, "Fixtures", "minimal-api-schema.json");
    }

    public static string GetAlternateMinimalSchemaPath()
    {
        var assemblyLocation = typeof(CliTestHelper).Assembly.Location;
        var testBinDir = Path.GetDirectoryName(assemblyLocation)!;
        return Path.Combine(testBinDir, "Fixtures", "minimal-api-schema-alt.json");
    }
}
