// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Core.Startup;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Runs the plugin registration checks that can only be made once the container exists.
/// </summary>
/// <remarks>
/// <para>
/// Thin by design. Every decision is in the host-agnostic audit function; this owns the startup
/// placement, the fatal path, and the acceptance line. It lives in the frontend rather than in Core
/// because the records it reads are produced in the plugin hosting assembly, and Core must not see
/// that tree.
/// </para>
/// <para>
/// What each plugin contributed is not logged here. <see cref="PluginInventoryLog"/> emits that as a
/// structured event immediately after the container is built, which is both earlier than this task and
/// earlier than every other guard that can abort startup naming a type. A second rendering here would
/// duplicate the output and could disagree with it, because the audit activates services in between.
/// </para>
/// <para>
/// It resolves no service collection. Its input arrives by constructor as the instance the composition
/// extension registered after every hook had run, which is what a plugin's own registration of the
/// same type cannot displace.
/// </para>
/// </remarks>
internal sealed class PluginRegistrationGuard(
    PluginAuditInput auditInput,
    IServiceProvider rootServiceProvider,
    ILogger<PluginRegistrationGuard> logger
) : IDmsStartupTask
{
    // Inside the 200-299 window Program.cs executes, and above the custom validator guard at 250, so
    // these checks read a collection that audit has already accepted. Lowering it below 250 was
    // rejected for that reason.
    public int Order => 260;

    public string Name => "Validate Plugin Service Registrations";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PluginAuditResult result = await PluginRegistrationAudit.AuditAsync(
            auditInput,
            rootServiceProvider,
            cancellationToken
        );

        if (result.ScopeCleanupFailure is not null)
        {
            // A warning rather than a failure, and reported separately so it can neither stand in for
            // nor hide an activation failure. Releasing the probe's scope runs implementer code, and
            // the container abandons the rest of a scope's disposables after the first one throws.
            logger.LogWarning(
                result.ScopeCleanupFailure,
                "Releasing the plugin activation scope threw. Any instance the scope had not yet "
                    + "released stays unreleased; this did not affect the registration checks below"
            );
        }

        if (result.Findings.Count > 0)
        {
            throw new InvalidOperationException(
                $"Startup aborted: {result.Findings.Count} plugin registration problem(s). Correct the "
                    + "plugin, or remove it from Plugins:Allowed, then restart: "
                    + string.Join(" | ", result.Findings.Select(finding => finding.Message)),
                // The first original activation exception, where there was one. The rest travel in the
                // messages above; a wrapper that dropped every one of them would leave an operator
                // with a description of the failure and no stack.
                result
                    .Findings.Select(finding => finding.ActivationException)
                    .FirstOrDefault(activationException => activationException is not null)
            );
        }

        logger.LogInformation(
            "Plugin registration guard accepted the contributions of {PluginCount} plugin(s) against "
                + "{ContractCount} declared plugin contract(s)",
            auditInput.Records.Count,
            auditInput.Registry.Entries.Count
        );
    }
}
