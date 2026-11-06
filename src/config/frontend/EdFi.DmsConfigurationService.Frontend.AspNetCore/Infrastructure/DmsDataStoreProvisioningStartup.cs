// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Records the DMS data store provisioning readiness verdict once at startup, after <c>InitializeDatabase</c>
/// (spec 4.4, D-2, 7.3). A disabled feature records <c>Disabled</c>. An enabled feature first has its static
/// options validated (an <c>OptionsValidationException</c> propagates and stops startup with the validator's
/// text), then runs the trust policy and catalog probes (7.3 steps 2 and 3) through the registered catalog and
/// the registered engine's environmental probes, and records every failed probe. The remaining probe groups
/// of 7.3 (secret resolution, <c>psql</c>, staging, lock probe) arrive with later steps, so when every existing
/// probe passes the verdict is still <c>NotReady</c> with the <see cref="ProbesIncompleteProbe"/> reason: no
/// build at this step can report <c>Ready</c>. The host keeps running either way; the provisioner refuses
/// every call until a restart after remediation.
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

        ITemplateArtifactCatalog catalog = services.GetRequiredService<ITemplateArtifactCatalog>();
        IDmsTargetEngine engine = services.GetRequiredService<IDmsTargetEngine>();

        // Every probe group runs so the operator sees the complete picture in one restart.
        List<DmsReadinessReason> reasons = [.. await catalog.ValidateAsync(cancellationToken)];
        if (
            await engine.ValidateEnvironmentAsync(cancellationToken) is DmsEngineEnvironment.NotReady notReady
        )
        {
            reasons.AddRange(notReady.Reasons);
        }

        if (reasons.Count == 0)
        {
            reasons.Add(
                new DmsReadinessReason(
                    DmsProvisioningFailureCode.StartupValidationFailed,
                    ProbesIncompleteProbe
                )
            );
        }

        readiness.Record(new DmsProvisionerReadiness.NotReady(reasons));
        foreach (DmsReadinessReason reason in reasons)
        {
            logger.LogCritical(
                _notReady,
                "DMSPROV-{Code}: DMS data store provisioning is not ready for target provider {Provider}: probe {Probe} failed at {Operation} (exception types {ExceptionTypeChain}). The provisioner refuses every call until the host restarts after remediation",
                reason.Code,
                LoggingUtility.SanitizeForLog(engine.Provider),
                reason.Probe,
                reason.Diagnostic?.Operation ?? "none",
                string.IsNullOrEmpty(reason.Diagnostic?.ExceptionTypeChain)
                    ? "none"
                    : reason.Diagnostic.ExceptionTypeChain
            );
        }
    }
}
