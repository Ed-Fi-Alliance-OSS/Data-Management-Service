// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    private async Task<MessageContractProviderObserver> CreateProviderObserverAsync(CancellationToken token)
    {
        int port = await ReadMappedProviderPortAsync(token);
        return new(BuildBinding(Provider), () => CreateProviderAdminConnection(port));
    }

    private async Task<MessageContractProviderFences> CreateProviderFencesAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken token
    )
    {
        var deployment = CdcDeploymentRequest.CreateDeferred(
            request.Binding,
            new ConfigurationBuilder().Build(),
            () =>
                throw new InvalidOperationException(
                    "Message contract observations do not provision a provider."
                ),
            _httpClient.BaseAddress!,
            new Uri("http://fixture-connect:9404/metrics"),
            request.DeploymentPolicy,
            CdcDeploymentRequestTestData.Worker(
                heapBytes: (long)request.DeploymentPolicy.EffectiveProducerBufferBytes + 1
            ),
            request.ProviderConnectionProperties,
            request.KafkaClientSecurityProperties,
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10))
        );
        return new(
            await CreateProviderObserverAsync(token),
            deployment,
            new CdcConnectRestAdapter(_httpClient, TimeProvider.System)
        );
    }

    public async Task AssertConnectorIncludeListAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken token
    ) => await (await CreateProviderFencesAsync(request, token)).AssertConnectorIncludeListAsync(token);
}
