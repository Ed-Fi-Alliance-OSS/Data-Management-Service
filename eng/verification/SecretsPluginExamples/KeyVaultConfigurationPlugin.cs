// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// The region below is mirrored verbatim into the EdFi.Api.Secrets readme, and a check compares the
// two, so the example an implementer copies is one that has been compiled. It therefore carries its
// own usings, its own namespace, and every type it names.
//
// This project is a compile check and is never loaded by a host. It holds three plugin classes, which
// a real plugin assembly may not (the host requires exactly one), and its assembly name equals none of
// their Names. A real plugin is its own project whose directory name, assembly name, and Name are one
// string; PLUGINS.md shows the project settings that do that.

// embed-region: plugin
using Azure.Identity;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;

namespace Acme.KeyVaultConfiguration;

public sealed class KeyVaultConfigurationPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.KeyVaultConfiguration";

    public override void ContributeConfiguration(
        IConfigurationBuilder configurationBuilder,
        IConfiguration bootstrapConfiguration
    )
    {
        // The vault address is ordinary operator configuration, read from what is already layered.
        // Throwing here fails startup naming this plugin, which is the right outcome for a plugin
        // allowlisted without the one setting it needs.
        string vaultUri =
            bootstrapConfiguration["Acme:KeyVault:VaultUri"]
            ?? throw new InvalidOperationException(
                "Set Acme:KeyVault:VaultUri to the vault's address, such as https://my-vault.vault.azure.net/."
            );

        // One source. Where it lands among the host's sources is the host's decision, not the order
        // it is added in. The credential is ambient: on Azure, DefaultAzureCredential finds the
        // workload's managed identity and no secret enters configuration.
        configurationBuilder.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());
    }
}
// embed-region-end: plugin
