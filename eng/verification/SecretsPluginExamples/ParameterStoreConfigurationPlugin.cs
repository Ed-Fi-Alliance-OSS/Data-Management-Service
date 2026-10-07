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
using Amazon.Extensions.NETCore.Setup;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;

namespace Acme.ParameterStoreConfiguration;

public sealed class ParameterStoreConfigurationPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.ParameterStoreConfiguration";

    public override void ContributeConfiguration(
        IConfigurationBuilder configurationBuilder,
        IConfiguration bootstrapConfiguration
    )
    {
        // The parameter path is ordinary operator configuration, read from what is already layered.
        string path =
            bootstrapConfiguration["Acme:ParameterStore:ConfigurationPath"]
            ?? throw new InvalidOperationException(
                "Set Acme:ParameterStore:ConfigurationPath to the parameter path to load, such as /edfi/cms."
            );

        // The AWS options are read from bootstrapConfiguration's AWS section and passed explicitly.
        // Without them the source builds the builder it is added to in order to find them, and the
        // builder a plugin is handed is not the one its sources are loaded with. The credential is
        // ambient: the SDK's default chain finds the workload's IAM role and no secret enters
        // configuration.
        AWSOptions awsOptions = bootstrapConfiguration.GetAWSOptions();

        // One source. Where it lands among the host's sources is the host's decision, not the order
        // it is added in.
        configurationBuilder.AddSystemsManager(path, awsOptions);
    }
}
// embed-region-end: plugin
