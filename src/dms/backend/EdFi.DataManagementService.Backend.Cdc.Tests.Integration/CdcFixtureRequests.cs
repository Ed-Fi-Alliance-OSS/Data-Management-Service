// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class CdcFixtureRequests
{
    internal static CdcDeploymentRequest WithWorkerEndpoints(
        CdcDeploymentRequest request,
        Uri connect,
        Uri metrics
    ) =>
        new(
            request.Binding,
            request.DmsSettings,
            request.ProviderSetup,
            connect,
            metrics,
            request.ConnectorPolicy,
            request.WorkerPolicy,
            request.ProviderConnectionProperties,
            request.KafkaClientSecurityProperties,
            request.Timing
        );
}
