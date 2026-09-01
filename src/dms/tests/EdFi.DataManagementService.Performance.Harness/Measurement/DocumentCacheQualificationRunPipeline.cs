// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Tests.Integration;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Shared orchestration boundary for the long-running DMS-1317 DocumentCache representative
/// qualification. Provider-specific NUnit fixtures own database leasing and hand this pipeline
/// the API harness plus an out-of-band replay/assertion connection factory.
/// </summary>
public static class DocumentCacheQualificationRunPipeline
{
    public static Task<string> RunAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string leasedConnectionString,
        DocumentCacheRepresentativeRunConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(openReplayConnectionAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(leasedConnectionString);
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.Provider != provider)
        {
            throw new PerfObservationException(
                $"DocumentCache representative pipeline received provider {PerfProviders.ArtifactName(provider)} "
                    + $"with configuration for {PerfProviders.ArtifactName(configuration.Provider)}."
            );
        }

        throw new PerfObservationException(
            "DocumentCache representative run entry points are wired, but the lifecycle/outage/status/write-overhead/contended-writer pipeline is implemented by plan step 5."
        );
    }
}
