// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Cdc.Control;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

/// <summary>
/// Reconciles the local CDC opt-in's configuration against the control plane's required inputs. The
/// entry points and the control plane are each self-consistent on their own, and neither side sees
/// the composition: the compose service's environment block, the wrapper's <c>-e</c> values and the
/// CLI's own configuration overrides are three separate sources, and only together do they have to
/// satisfy <see cref="CdcControlOptionsValidator"/> and the provider-setup inputs factory.
/// </summary>
/// <remarks>
/// Every value is read from the shipped source rather than restated here — the compose file as YAML,
/// the argument lists by invoking the PowerShell builders that produce them — so this cannot pass
/// against a second copy of the configuration that has drifted from the one the local path uses. No
/// Docker is involved: the builders compose an argument list, and only the list is read.
/// </remarks>
[TestFixture]
[Category("LocalCdcOptIn")]
public sealed partial class Given_LocalCdcOptInConfiguration
{
    private const string CdcEnvironmentVariablePrefix = "DataManagement__DocumentCache__Cdc__";

    /// <summary>The CLI verb group token, which is how the tool's own arguments are found.</summary>
    private const string CdcCommandToken = "cdc";

    [TestCase("postgresql", "database.dbname")]
    [TestCase("mssql", "database.names")]
    public void It_configures_every_control_plane_option_the_local_cdc_opt_in_requires(
        string databaseEngine,
        string catalogPropertyName
    )
    {
        IReadOnlyList<string> composeArguments = EnableArguments(databaseEngine);

        (CdcControlOptions options, ParseResult parseResult) = ControlOptions(composeArguments);

        parseResult
            .Errors.Should()
            .BeEmpty("every flag the enable phase passes must be an option the cdc verb exposes");
        ValidateOptionsResult validation = new CdcControlOptionsValidator().Validate(
            Options.DefaultName,
            options
        );
        validation
            .Succeeded.Should()
            .BeTrue(
                "the local opt-in must supply every control-plane option the deployment requires: {0}",
                validation.FailureMessage ?? string.Empty
            );
        options
            .ProviderConnectionProperties.Keys.Should()
            .Contain(
                ["database.hostname", "database.user", "database.password", catalogPropertyName],
                "the connector template requires these of the provider before it renders a connector"
            );
        options
            .ProviderConnectionProperties["database.password"]
            .Should()
            .StartWith(
                "${env:",
                "the connector password reaches the worker as a config-provider reference, never as a secret rendered into the registered configuration"
            );
    }

    /// <summary>
    /// Retirement goes through the same options validation, and it supplies no connection properties
    /// on purpose: it registers no connector and reads none, and the captured database name is a
    /// per-run value the teardown has no authority over.
    /// </summary>
    [TestCase("postgresql")]
    [TestCase("mssql")]
    public void It_configures_every_control_plane_option_the_local_cdc_retirement_requires(
        string databaseEngine
    )
    {
        IReadOnlyList<string> composeArguments = RetireArguments(databaseEngine);

        (CdcControlOptions options, ParseResult parseResult) = ControlOptions(composeArguments);

        parseResult
            .Errors.Should()
            .BeEmpty("every flag the teardown passes must be an option the cdc verb exposes");
        ValidateOptionsResult validation = new CdcControlOptionsValidator().Validate(
            Options.DefaultName,
            options
        );
        validation
            .Succeeded.Should()
            .BeTrue(
                "a destructive teardown must not fail options validation before it can retire: {0}",
                validation.FailureMessage ?? string.Empty
            );
        options.ConnectorPrincipal.Should().NotBeEmpty("every cdc verb runs a provider-setup pass");
        options
            .ProviderConnectionProperties.Should()
            .BeEmpty("a retirement registers no connector and reads no connection property");
    }

    /// <summary>
    /// Binds the options exactly as the container does: the compose service's environment block, then
    /// the <c>-e</c> values the phase adds, then the configuration overrides the CLI derives from its
    /// own parsed command line.
    /// </summary>
    private static (CdcControlOptions Options, ParseResult ParseResult) ControlOptions(
        IReadOnlyList<string> composeArguments
    )
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal);

        foreach ((string key, string value) in ComposeServiceCdcEnvironment())
        {
            settings[ToConfigurationKey(key)] = value;
        }

        foreach ((string key, string value) in EnvironmentArguments(composeArguments))
        {
            settings[ToConfigurationKey(key)] = value;
        }

        ParseResult parseResult = DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Parse(CommandLineArguments(composeArguments));

        foreach (
            (string key, string? value) in DocumentCacheAdminCommandSurface.CreateConfigurationOverrides(
                parseResult
            )
        )
        {
            settings[key] = value;
        }

        CdcControlOptions options = new();
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build()
            .GetSection(CdcControlOptions.SectionName)
            .Bind(options);

        return (options, parseResult);
    }

    /// <summary>
    /// The environment-variable form the container receives, translated to a configuration key the way
    /// the environment-variable provider does. The dots inside a provider connection property name are
    /// part of the key and survive the translation.
    /// </summary>
    private static string ToConfigurationKey(string environmentVariableName) =>
        environmentVariableName.Replace("__", ":", StringComparison.Ordinal);

    /// <summary>
    /// The CDC deployment-policy values the compose service declares, with each compose
    /// <c>${VAR:-default}</c> reference resolved to the default an unmodified env file leaves in place.
    /// </summary>
    private static IReadOnlyList<(string Key, string Value)> ComposeServiceCdcEnvironment()
    {
        string composeFile = Path.Combine(DockerComposeDirectory(), "cdc-setup.yml");
        List<(string Key, string Value)> environment = [];

        foreach (string line in File.ReadAllLines(composeFile))
        {
            if (ComposeCdcEnvironmentLine().Match(line) is not { Success: true } match)
            {
                continue;
            }

            environment.Add(
                (match.Groups["key"].Value, ResolveComposeInterpolation(match.Groups["value"].Value))
            );
        }

        environment
            .Should()
            .NotBeEmpty("the compose service is where the local path's CDC deployment policy comes from");

        return environment;
    }

    /// <summary>
    /// Resolves a compose value reference to the default it falls back to. A reference with no default
    /// resolves to nothing, which is what the container would receive.
    /// </summary>
    private static string ResolveComposeInterpolation(string composeValue) =>
        ComposeVariableReference().Replace(composeValue, match => match.Groups["default"].Value);

    /// <summary>The <c>KEY=VALUE</c> pairs the phase passes as <c>-e</c> arguments.</summary>
    private static IReadOnlyList<(string Key, string Value)> EnvironmentArguments(
        IReadOnlyList<string> composeArguments
    )
    {
        List<(string Key, string Value)> environment = [];

        for (int index = 0; index < composeArguments.Count - 1; index++)
        {
            if (!string.Equals(composeArguments[index], "-e", StringComparison.Ordinal))
            {
                continue;
            }

            string pair = composeArguments[index + 1];
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            separator.Should().BePositive($"-e argument '{pair}' must be a KEY=VALUE pair");
            environment.Add((pair[..separator], pair[(separator + 1)..]));
        }

        environment
            .Should()
            .Contain(pair => pair.Key.StartsWith(CdcEnvironmentVariablePrefix, StringComparison.Ordinal));

        return environment;
    }

    /// <summary>
    /// The tool's own arguments: everything from the <c>cdc</c> verb group onwards. The compose file
    /// and service are named <c>cdc-setup</c>, so the bare group token appears once.
    /// </summary>
    private static string[] CommandLineArguments(IReadOnlyList<string> composeArguments)
    {
        int commandIndex = composeArguments
            .Select((argument, index) => (argument, index))
            .Where(candidate => string.Equals(candidate.argument, CdcCommandToken, StringComparison.Ordinal))
            .Select(candidate => candidate.index)
            .Should()
            .ContainSingle("the argument list runs exactly one cdc verb")
            .Subject;

        return [.. composeArguments.Skip(commandIndex)];
    }

    private static IReadOnlyList<string> EnableArguments(string databaseEngine) =>
        ArgumentList(
            $$"""
            Import-Module (Join-Path '{{DockerComposeDirectory().Replace(
                '\\',
                '/'
            )}}' 'bootstrap-wrapper.psm1') -Force
            Get-WrapperCdcEnableArgument `
                -ComposeProjectName 'dms-local' `
                -EnvironmentFile '.env' `
                -TenantKey '' `
                -DataStoreId 1 `
                -DatabaseEngine '{{databaseEngine}}' `
                -DatabaseCreatedByThisRun $true `
                -DmsBearerToken 'operator-token' `
                -SourceDatabaseName 'edfi_datamanagementservice'
            """
        );

    private static IReadOnlyList<string> RetireArguments(string databaseEngine) =>
        ArgumentList(
            $$"""
            Import-Module (Join-Path '{{DockerComposeDirectory().Replace(
                '\\',
                '/'
            )}}' 'cdc-teardown.psm1') -Force
            $bindingRecord = [pscustomobject]@{
                DataStoreId   = 1
                TenantKey     = ''
                DeploymentKey = 'local'
                InstanceKey   = 'ds1'
                Generation    = 1
            }
            Get-CdcRetireArgument `
                -ComposeProjectName 'dms-local' `
                -EnvironmentFile '.env' `
                -BindingRecord $bindingRecord `
                -DatabaseEngine '{{databaseEngine}}' `
                -DmsBearerToken 'operator-token'
            """
        );

    /// <summary>
    /// Runs one of the shipped argument builders and returns the list it composed. The builder is
    /// invoked rather than parsed so the values reconciled here are the ones the phase actually passes.
    /// </summary>
    private static IReadOnlyList<string> ArgumentList(string builderInvocation)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), $"cdc-arguments-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            scriptPath,
            $"""
            $ErrorActionPreference = 'Stop'
            {builderInvocation} | ConvertTo-Json -Compress -AsArray

            """
        );

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = DockerComposeDirectory(),
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);

            using Process? process = Process.Start(startInfo);
            process.Should().NotBeNull();

            string standardOutput = process!.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            process.ExitCode.Should().Be(0, "the argument builder must compose a list: {0}", standardError);

            return JsonSerializer.Deserialize<string[]>(standardOutput)
                ?? throw new InvalidOperationException("The argument builder returned no argument list.");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static string DockerComposeDirectory() =>
        Path.Combine(RepositoryRoot().FullName, "eng", "docker-compose");

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LICENSE")))
        {
            directory = directory.Parent;
        }

        return directory
            ?? throw new InvalidOperationException(
                "Could not locate the repository root from the test assembly output."
            );
    }

    [GeneratedRegex(
        @"^\s+(?<key>DataManagement__DocumentCache__Cdc__[A-Za-z0-9_.]+):\s*(?<value>\S.*?)\s*$",
        RegexOptions.ExplicitCapture
    )]
    private static partial Regex ComposeCdcEnvironmentLine();

    [GeneratedRegex(
        @"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?<default>[^}]*))?\}",
        RegexOptions.ExplicitCapture
    )]
    private static partial Regex ComposeVariableReference();
}
