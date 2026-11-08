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
/// options validated and its protected set built (an exception from either propagates and stops startup with
/// its text, which names settings and never values), then runs the probe groups of 7.3 and records every
/// failed probe: secret resolution of the administrative connection string (item 1); the trust policy and
/// catalog probes (items 2 and 3); and, once the connection string is resolved, the engine's probes (items 4
/// to 7). When the engine read the server's major version, it is compared with the <c>engineVersion</c> of
/// every template whose manifest the catalog authenticated (D-9, 6.4 step 2), with the rule every create
/// applies, so a template no create could restore is <c>EngineIncompatible</c> at startup. The protected set
/// is logged with sanitized names (item 8). When every probe passes the verdict is
/// <c>Ready</c> for the engine's provider; otherwise <c>NotReady</c> with each reason logged at Critical as
/// <c>DMSPROV-&lt;Code&gt;</c>. A probe group that throws is recorded as <c>StartupValidationFailed</c> under
/// its probe name rather than stopping the host. The host keeps running either way; the provisioner refuses
/// every call until a restart after remediation.
/// </summary>
public static class DmsDataStoreProvisioningStartup
{
    /// <summary>The probe name of a secret resolution failure (7.3 item 1).</summary>
    public const string SecretResolutionProbe = "SecretResolution";

    /// <summary>The probe name under which a catalog validation that threw is recorded.</summary>
    public const string CatalogProbesProbe = "CatalogProbes";

    /// <summary>The probe name under which an engine validation that threw is recorded.</summary>
    public const string EngineProbesProbe = "EngineProbes";

    private static readonly EventId _disabled = new(
        1438_01,
        nameof(DmsDataStoreProvisioningStartup) + ".Disabled"
    );

    private static readonly EventId _notReady = new(
        1438_02,
        nameof(DmsDataStoreProvisioningStartup) + ".NotReady"
    );

    private static readonly EventId _ready = new(1438_03, nameof(DmsDataStoreProvisioningStartup) + ".Ready");

    private static readonly EventId _protectedNames = new(
        1438_04,
        nameof(DmsDataStoreProvisioningStartup) + ".ProtectedNames"
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

        // 7.3 item 8, built first: a catalog connection string naming no database is static misconfiguration
        // and stops startup as the options validator does.
        ProtectedDatabaseNames protectedNames = services.GetRequiredService<ProtectedDatabaseNames>();
        logger.LogInformation(
            _protectedNames,
            "DMS data store provisioning protects these database names on the target server beside the engine system databases: {ProtectedNames}",
            string.Join(", ", protectedNames.Names.Select(LoggingUtility.SanitizeForLog))
        );

        IDmsAdminConnectionSource adminConnection = services.GetRequiredService<IDmsAdminConnectionSource>();
        ITemplateArtifactCatalog catalog = services.GetRequiredService<ITemplateArtifactCatalog>();
        IDmsTargetEngine engine = services.GetRequiredService<IDmsTargetEngine>();
        TimeProvider timeProvider = services.GetRequiredService<TimeProvider>();

        // Every probe group runs so the operator sees the complete picture in one restart; only the engine's
        // probes wait for the connection string, because they cannot connect without it.
        DmsAdminConnectionResolution resolution = await adminConnection.ResolveAsync(cancellationToken);
        IReadOnlyList<DmsReadinessReason> secretReasons = resolution
            is DmsAdminConnectionResolution.Failed failed
            ?
            [
                new DmsReadinessReason(
                    DmsProvisioningFailureCode.SecretResolutionFailed,
                    SecretResolutionProbe,
                    failed.Diagnostic
                ),
            ]
            : [];
        IReadOnlyList<TemplateEngineRequirement> engineRequirements = [];
        IReadOnlyList<DmsReadinessReason> catalogReasons = await RunGroupAsync(
            async token =>
            {
                TemplateCatalogValidation validation = await catalog.ValidateAsync(token);
                engineRequirements = validation.EngineRequirements;
                return validation.Reasons;
            },
            CatalogProbesProbe,
            cancellationToken
        );
        IReadOnlyList<DmsReadinessReason> engineReasons = [];
        int? serverMajorVersion = null;
        if (resolution is DmsAdminConnectionResolution.Resolved resolved)
        {
            // The engine connects with the connection string bound to its flow; startup binds its own.
            using IDisposable adminBinding = adminConnection.Bind(resolved.Connection);
            engineReasons = await RunGroupAsync(
                async token =>
                {
                    DmsEngineEnvironment environment = await engine.ValidateEnvironmentAsync(token);
                    serverMajorVersion = environment.ServerMajorVersion;
                    return environment is DmsEngineEnvironment.NotReady notReady ? notReady.Reasons : [];
                },
                EngineProbesProbe,
                cancellationToken
            );
        }

        // D-9 and 6.4 step 2 at startup: a server older than any authenticated template's engine is refused here,
        // not by every create later. Nothing is compared when the server or a manifest could not be read.
        IReadOnlyList<DmsReadinessReason> compatibilityReasons = serverMajorVersion is { } serverMajor
            ? DmsEngineCompatibility.Evaluate(serverMajor, engineRequirements)
            : [];

        IReadOnlyList<DmsReadinessReason> reasons =
        [
            .. secretReasons,
            .. catalogReasons,
            .. engineReasons,
            .. compatibilityReasons,
        ];

        string provider = LoggingUtility.SanitizeForLog(engine.Provider);
        if (reasons.Count == 0)
        {
            readiness.Record(new DmsProvisionerReadiness.Ready(engine.Provider, timeProvider.GetUtcNow()));
            logger.LogInformation(
                _ready,
                "DMS data store provisioning is ready for target provider {Provider}",
                provider
            );
            return;
        }

        readiness.Record(new DmsProvisionerReadiness.NotReady(reasons));
        foreach (DmsReadinessReason reason in reasons)
        {
            logger.LogCritical(
                _notReady,
                "DMSPROV-{Code}: DMS data store provisioning is not ready for target provider {Provider}: probe {Probe} failed at {Operation} (exception types {ExceptionTypeChain}, provider error {ProviderErrorCode}). The provisioner refuses every call until the host restarts after remediation",
                reason.Code,
                provider,
                reason.Probe,
                reason.Diagnostic?.Operation ?? "none",
                string.IsNullOrEmpty(reason.Diagnostic?.ExceptionTypeChain)
                    ? "none"
                    : reason.Diagnostic.ExceptionTypeChain,
                reason.Diagnostic?.ProviderErrorCode ?? "none"
            );
        }
    }

    /// <summary>
    /// Runs one probe group; an exception other than the caller's cancellation is recorded as
    /// <c>StartupValidationFailed</c> under <paramref name="probe"/> with its type chain, never its message.
    /// </summary>
    private static async Task<IReadOnlyList<DmsReadinessReason>> RunGroupAsync(
        Func<CancellationToken, Task<IReadOnlyList<DmsReadinessReason>>> group,
        string probe,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await group(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return
            [
                new DmsReadinessReason(
                    DmsProvisioningFailureCode.StartupValidationFailed,
                    probe,
                    DmsProvisioningDiagnostic.From(exception, probe)
                ),
            ];
        }
    }
}
