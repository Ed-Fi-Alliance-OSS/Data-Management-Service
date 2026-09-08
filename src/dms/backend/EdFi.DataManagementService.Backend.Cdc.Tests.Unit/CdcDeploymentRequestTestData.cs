// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.Configuration;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal static class CdcDeploymentRequestTestData
{
    public static CdcWorkerDeploymentPolicy Worker(
        long heapBytes = 268_435_456,
        string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        CdcKafkaDurabilityProfile durability = CdcKafkaDurabilityProfile.LocalSingleBroker,
        CdcKafkaAuthorizationProfile authorization = CdcKafkaAuthorizationProfile.AuthorizationDisabledLocal,
        string offsetTopic = "connect-offsets"
    ) =>
        new(
            new("worker"),
            new(offsetTopic),
            digest,
            heapBytes,
            "All",
            durability,
            authorization,
            new("worker-principal"),
            new("kafka-connector-principal"),
            new("kafka-administrator-principal"),
            [new(new("consumer"), new("consumer-group"))]
        );

    public static CdcDeploymentRequest Request(
        CdcProvider provider = CdcProvider.Postgresql,
        string endpoint = "http://connect:8083/",
        string metricsEndpoint = "http://connect:9404/metrics",
        Func<CoreCdc.CdcBinding, CoreCdc.CdcBinding>? changeBinding = null,
        CdcSourceFingerprint? setupFingerprint = null,
        CdcProviderArtifactNames? setupArtifacts = null,
        CdcWorkerDeploymentPolicy? worker = null,
        string password = "${env:DATABASE_PASSWORD}"
    )
    {
        CdcConnectorTemplateRequest template = BuildRequest(provider);
        CoreCdc.CdcBinding binding = changeBinding is null
            ? template.Binding
            : changeBinding(template.Binding);
        Dictionary<string, string> connection = new(template.ProviderConnectionProperties.Properties)
        {
            ["database.password"] = password,
            ["database.hostname"] = "private-source-host",
        };
        CdcProviderSetupRequest setup = new(
            provider,
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            setupFingerprint ?? SourceFingerprintFor(provider),
            new(new("setup-principal")),
            new(new("connector-principal")),
            setupArtifacts ?? BuildProviderArtifactNames(provider),
            new(false),
            BuildRequiredSourceTableInventory(provider),
            [
                new(
                    CdcDmsManagedTableKind.Core,
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    "private-physical-table"
                ),
            ]
        );
        return new(
            binding,
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Dms"] = "Host=private-source-host;Password=super-secret",
                        ["DocumentCache:Targets:0:DataStoreId"] = "1",
                    }
                )
                .Build(),
            setup,
            new Uri(endpoint, UriKind.RelativeOrAbsolute),
            new Uri(metricsEndpoint, UriKind.RelativeOrAbsolute),
            template.DeploymentPolicy,
            worker ?? Worker(),
            new(provider, connection),
            template.KafkaClientSecurityProperties,
            new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1))
        );
    }
}
