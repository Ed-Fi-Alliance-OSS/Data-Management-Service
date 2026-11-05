// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Records the DMS data store provisioning readiness verdict once at startup, after <c>InitializeDatabase</c>
/// (spec 4.4, D-2, 7.3). A disabled feature records <c>Disabled</c>. An enabled feature first has its static
/// options validated (an <c>OptionsValidationException</c> propagates and stops startup with the validator's
/// text), then runs the registered engine's environmental probes. The remaining probe groups of 7.3 (secret
/// resolution, trust policy, catalog, <c>psql</c>, staging, lock probe) arrive with later steps, so until they
/// exist the verdict is <c>NotReady</c> even when the engine reports its environment ready: no build at this
/// step can report <c>Ready</c>. The host keeps running either way; the provisioner refuses every call until
/// a restart after remediation.
/// </summary>
public static class DmsDataStoreProvisioningStartup
{
    public const string ProbesIncompleteProbe = "ReadinessProbesIncomplete";

    private static readonly EventId _disabled = new(
        1438_01,
        nameof(DmsDataStoreProvisioningStartup) + ".Disabled"
    );

    private static readonly EventId _notReady = new(
        1438_02,
        nameof(DmsDataStoreProvisioningStartup) + ".NotReady"
    );

    public static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        DmsDataStoreProvisionerReadiness readiness =
            services.GetRequiredService<DmsDataStoreProvisionerReadiness>();

        // Resolving the value runs the static validator; a failure is left unhandled so the process exits
        // non-zero with the validator's message, as DatabaseOptions does further up in Program.cs.
        DmsDataStoreProvisioningOptions options = services
            .GetRequiredService<IOptions<DmsDataStoreProvisioningOptions>>()
            .Value;

        if (!options.Enabled)
        {
            readiness.Record(new DmsProvisionerReadiness.Disabled());
            logger.LogInformation(_disabled, "DMS data store provisioning is disabled");
            return;
        }

        IDmsTargetEngine engine = services.GetRequiredService<IDmsTargetEngine>();
        DmsEngineEnvironment environment = await engine.ValidateEnvironmentAsync(cancellationToken);

        IReadOnlyList<DmsReadinessReason> reasons = environment switch
        {
            DmsEngineEnvironment.NotReady notReady => notReady.Reasons,
            _ =>
            [
                new DmsReadinessReason(
                    DmsProvisioningFailureCode.StartupValidationFailed,
                    ProbesIncompleteProbe
                ),
            ],
        };

        readiness.Record(new DmsProvisionerReadiness.NotReady(reasons));
        foreach (DmsReadinessReason reason in reasons)
        {
            logger.LogCritical(
                _notReady,
                "DMSPROV-{Code}: DMS data store provisioning is not ready for target provider {Provider}: probe {Probe} failed. The provisioner refuses every call until the host restarts after remediation",
                reason.Code,
                LoggingUtility.SanitizeForLog(engine.Provider),
                reason.Probe
            );
        }
    }
}
