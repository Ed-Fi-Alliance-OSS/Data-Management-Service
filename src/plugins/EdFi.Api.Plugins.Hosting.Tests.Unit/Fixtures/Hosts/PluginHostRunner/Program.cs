// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json;
using EdFi.Api.Plugins;
using EdFi.Api.Plugins.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PluginHostRunner;

/// <summary>
/// A host whose default context carries contract 1.1.0, loading a plugin built against 1.0.0 through
/// the real loader.
/// </summary>
/// <remarks>
/// <para>
/// The staging that gives this process a 1.1.0 contract happens outside it, in a temporary copy of its
/// own publish output. That makes the identity it ends up with a thing to be verified rather than
/// assumed, which is why the first thing this program does is read the contract version out of its own
/// default context and refuse to go on unless it is exactly 1.1.0.0. A run that reported success
/// against a 1.0.0 host would prove nothing at all, and this is what stops that being possible.
/// </para>
/// <para>
/// Everything else it reports is read rather than asserted. The caller does the asserting, so a
/// failure is diagnosable from the output instead of hiding behind an exit code.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>The contract identity this program requires its own default context to carry.</summary>
    private static readonly Version _requiredHostContract = new(1, 1, 0, 0);

    /// <summary>The line prefix the caller looks for, so ordinary output cannot be mistaken for it.</summary>
    private const string ResultPrefix = "RESULT ";

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: PluginHostRunner <plugin-root> <plugin-name>");
            return 2;
        }

        Version? hostContract = typeof(EdFiApiPlugin).Assembly.GetName().Version;

        if (hostContract != _requiredHostContract)
        {
            // Named, and before anything is loaded. Staging is what puts 1.1.0 here, and staging that
            // silently did not take would otherwise turn this whole proof into a 1.0-on-1.0 run.
            Console.Error.WriteLine(
                $"HOST CONTRACT MISMATCH: this process requires EdFi.Api.Plugins "
                    + $"{_requiredHostContract} in its default context and carries "
                    + $"{hostContract?.ToString() ?? "no version"}."
            );

            return 3;
        }

        try
        {
            return Report(args[0], args[1], hostContract);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"RUN FAILURE {exception.GetType().FullName}: {exception.Message}");
            return 1;
        }
    }

    private static int Report(string root, string pluginName, Version hostContract)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Plugins:Directory"] = root,
                    ["Plugins:Allowed"] = pluginName,
                }
            )
            .Build();

        // The real public entry point, with the contract assembly name a host passes.
        LoadedPlugin plugin = PluginLoader.Load(configuration, ["EdFi.Api.Plugins"]).Plugins[0];

        Type pluginType = plugin.Instance.GetType();

        // The 1.1 addition, found by name because this program was compiled against 1.0.0 and cannot
        // refer to it. Invoked as well as found, so "present and does nothing" is a fact of the run
        // rather than a claim about a member nobody called.
        MethodInfo? addedVirtual = pluginType.GetMethod("DescribeCapabilities");

        addedVirtual?.Invoke(plugin.Instance, null);

        ServiceCollection services = [];
        plugin.Instance.ContributeServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        object? marker = pluginType.Assembly.GetType("Acme.OldContract.OldContractMarker") is { } markerType
            ? provider.GetService(markerType)
            : null;

        RunnerResult result = new(
            hostContract.ToString(),
            plugin.Name,
            pluginType.BaseType!.Assembly.GetName().Version!.ToString(),
            addedVirtual is not null,
            marker?.GetType().GetProperty("PluginName")?.GetValue(marker) as string,
            [
                .. plugin
                    .MaterializeSubstitutions()
                    .Select(substitution => new RunnerSubstitution(
                        substitution.AssemblyName,
                        substitution.RequestedVersion?.ToString(),
                        substitution.HostVersion.ToString(),
                        substitution.DeclaredVersion?.ToString()
                    )),
            ]
        );

        Console.WriteLine(ResultPrefix + JsonSerializer.Serialize(result));

        return 0;
    }
}

/// <summary>What one run observed, for the caller to assert on.</summary>
internal sealed record RunnerResult(
    string HostContractVersion,
    string PluginName,
    string PluginBaseContractVersion,
    bool AddedVirtualVisible,
    string? HookMarkerPluginName,
    RunnerSubstitution[] Substitutions
);

/// <summary>One host-first substitution the run recorded.</summary>
internal sealed record RunnerSubstitution(
    string AssemblyName,
    string? RequestedVersion,
    string HostVersion,
    string? DeclaredVersion
);
