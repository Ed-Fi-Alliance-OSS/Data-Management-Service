// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.FixtureContracts;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace Acme.ConfigContributor;

/// <summary>
/// Contributes configuration sources, doing whatever <c>Fixture:&lt;Name&gt;:Configuration</c> in the
/// bootstrap configuration asks for.
/// </summary>
/// <remarks>
/// Named from its own assembly, because the same source is compiled as two plugins.
/// </remarks>
public sealed class ConfigContributorPlugin : EdFiApiPlugin
{
    /// <summary>The encryption key this plugin supplies, so a test can recognize the plugin's value.</summary>
    public const string EncryptionKeySuffix = "-encryption-key";

    /// <summary>A prefix no environment variable carries.</summary>
    public const string UnmatchedEnvironmentPrefix = "ACME_CONFIG_CONTRIBUTOR_UNMATCHED_";

    public override string Name => typeof(ConfigContributorPlugin).Assembly.GetName().Name!;

    public override void ContributeConfiguration(
        IConfigurationBuilder configurationBuilder,
        IConfiguration bootstrapConfiguration
    )
    {
        // Taken on entry, before anything else, so a case can order this hook against the host's
        // announcement and against the other plugin's hook.
        FixtureObservations.Record($"{Name}:enteredAt", FixtureObservations.Next().ToString());
        FixtureObservations.Record(
            $"{Name}:sameInstance",
            ReferenceEquals(configurationBuilder, bootstrapConfiguration).ToString()
        );

        IList<IConfigurationSource> sources = configurationBuilder.Sources;

        switch (bootstrapConfiguration[$"Fixture:{Name}:Configuration"])
        {
            case "append":
                configurationBuilder.Add(Supplied());
                configurationBuilder.Add(Source(new() { ["Fixture:WithinPlugin"] = $"{Name}:second" }));
                break;

            case "appendEnvironment":
                // An environment source of the plugin's own, added after an ordinary one. It is a
                // plugin source like any other, and must not become the anchor its own additions are
                // placed under. The prefix matches nothing, so it reads no values.
                configurationBuilder.Add(Supplied());
                configurationBuilder.Add(
                    new EnvironmentVariablesConfigurationSource { Prefix = UnmatchedEnvironmentPrefix }
                );
                break;

            case "insert":
                // An addition made at the bottom of the list rather than appended at the top. Still an
                // addition, and placed like one.
                sources.Insert(0, Supplied());
                break;

            case "remove":
                sources.RemoveAt(0);
                break;

            case "removePluginSource":
                // Takes out a source an earlier plugin in the allowlist added, which is still a
                // pre-existing source as far as this hook is concerned.
                sources.Remove(
                    sources.First(source =>
                        source is MemoryConfigurationSource { InitialData: { } data }
                        && data.Any(pair =>
                            pair.Key.StartsWith("Fixture:Supplied:", StringComparison.Ordinal)
                        )
                    )
                );
                break;

            case "reorder":
                (sources[0], sources[1]) = (sources[1], sources[0]);
                break;

            case "throw":
                throw new InvalidOperationException($"{Name} could not reach its configuration store");
        }
    }

    private MemoryConfigurationSource Supplied() =>
        Source(
            new()
            {
                [$"Fixture:Supplied:{Name}"] = Name,
                ["Fixture:Winner"] = Name,
                ["Fixture:WithinPlugin"] = $"{Name}:first",
                ["Fixture:Precedence:Json"] = Name,
                ["Fixture:Precedence:Environment"] = Name,
                ["Fixture:Precedence:CommandLine"] = Name,
                ["ConfigurationServiceSettings:EncryptionKey"] = Name + EncryptionKeySuffix,
                ["Plugins:Allowed"] = "Acme.NotAllowlisted",
            }
        );

    private static MemoryConfigurationSource Source(Dictionary<string, string?> data) =>
        new() { InitialData = data };
}
