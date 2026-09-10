// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Core.Startup;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Runs the plugin registration checks that can only be made once the container exists, and records
/// what each plugin contributed.
/// </summary>
/// <remarks>
/// <para>
/// Thin by design. Every decision is in the host-agnostic audit function; this owns the startup
/// placement, the fatal path, and the log. It lives in the frontend rather than in Core because the
/// records it reads are produced in the plugin hosting assembly, and Core must not see that tree.
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

        // Logged before anything is thrown, because it is the record of what each plugin contributed
        // and a startup that is about to abort is exactly when an operator needs it. The full
        // structured inventory event, emitted before any startup task runs, belongs to the
        // host-integration story; this is the same information from the same records, at the only
        // point this story has a logger at.
        LogWhatEachPluginContributed();

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

    private void LogWhatEachPluginContributed()
    {
        foreach (PluginContributionRecord record in auditInput.Records)
        {
            logger.LogInformation(
                "Plugin '{PluginName}' registered {ServiceTypes}; removed {Removals}; host-first "
                    + "substitutions {Substitutions}; declared files {DeclaredFiles}",
                Loggable(record.PluginName),
                Describe(record.Additions.Select(descriptor => TypeNameForLog(descriptor.ServiceType))),
                Describe(
                    record.Removals.Select(removal =>
                        $"{TypeNameForLog(removal.ServiceType)} (displacing "
                        + $"{(removal.DisplacedImplementationType is null ? "a factory" : TypeNameForLog(removal.DisplacedImplementationType))})"
                    )
                ),
                Describe(
                    record
                        .MaterializeSubstitutions()
                        .Select(substitution =>
                            $"{Loggable(substitution.AssemblyName)} plugin declared "
                            + $"{substitution.DeclaredVersion?.ToString() ?? "none"}, host served {substitution.HostVersion}"
                        )
                ),
                Describe(
                    record
                        .MaterializeInventory()
                        .Select(row =>
                            $"{Loggable(row.FileName)} {row.EffectiveVersion?.ToString() ?? "no version"} "
                            + $"sha256:{Loggable(row.Sha256) ?? "absent"} {row.LoadState}"
                        )
                )
            );
        }
    }

    private static string Describe(IEnumerable<string> values)
    {
        string joined = string.Join(", ", values);

        return joined.Length == 0 ? "none" : joined;
    }

    /// <summary>
    /// A type name for a log record. Type names come from assembly metadata, so the only hazard one
    /// carries is a control character forging a record; stripping just those keeps a nested or generic
    /// name searchable in the source it came from.
    /// </summary>
    private static string TypeNameForLog(Type type) => Loggable(type.FullName ?? type.Name)!;

    /// <summary>
    /// A value from outside the process, rendered so it cannot forge a log record.
    /// </summary>
    /// <remarks>
    /// Plugin names, file names and assembly names all originate in a directory a third party
    /// published, and these records are line-oriented.
    /// </remarks>
    private static string? Loggable(string? value) =>
        value is null ? null : string.Concat(value.Where(static character => !char.IsControl(character)));
}
