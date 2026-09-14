// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The audit record of what each loaded plugin brought into the process, emitted once the logger
/// exists and before any startup task runs.
/// </summary>
/// <remarks>
/// <para>
/// This is an audit record rather than a convenience. The trust model does not claim to contain a
/// plugin, so an incident responder has to be able to say which third-party code was available to the
/// process, at what version, and from which bytes.
/// </para>
/// <para>
/// It takes the audit input and nothing else. There is deliberately no way to reach configuration
/// from here: a plugin type exists to carry secrets into configuration, so a diagnostic that could
/// see configuration is a diagnostic that could log secrets. The rule holds by construction rather
/// than by everyone remembering it.
/// </para>
/// <para>
/// Every value projected below is metadata: names, versions, digests and states. No service
/// descriptor, implementation instance, factory delegate or configuration object is passed to the
/// logger, so nothing here can render an object whose <c>ToString</c> the host does not control.
/// </para>
/// </remarks>
internal static class PluginInventoryLog
{
    /// <summary>
    /// Identifies this event apart from the registration guard's own per-plugin diagnostics, which are
    /// built from the same records at a later point in startup.
    /// </summary>
    private static readonly EventId PluginInventoryEvent = new(1499, "PluginInventory");

    /// <summary>
    /// Emits one structured event per loaded plugin. Nothing is written when no plugin was loaded,
    /// which is the shipped default.
    /// </summary>
    public static void Emit(ILogger logger, PluginAuditInput auditInput)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(auditInput);

        foreach (PluginContributionRecord record in auditInput.Records)
        {
            // Materialized here rather than earlier. Both reads are late-bound against the plugin's own
            // load context, so a dependency first touched inside the contribution hook reports as
            // loaded and a substitution the hook provoked is present.
            IReadOnlyList<PluginInventoryRow> inventory = record.MaterializeInventory();
            IReadOnlyList<HostFirstSubstitution> substitutions = record.MaterializeSubstitutions();

            logger.LogInformation(
                PluginInventoryEvent,
                "Plugin inventory for {PluginName} version {AssemblyVersion}: declared files "
                    + "{@DeclaredFiles}; registered service types {@RegisteredServiceTypes}; removed "
                    + "descriptors {@RemovedDescriptors}; host-first substitutions "
                    + "{@HostFirstSubstitutions}",
                PluginLogText.Loggable(record.PluginName),
                record.Plugin.EntryAssemblyVersion.ToString(),
                inventory.Select(DeclaredFileOf).ToArray(),
                record.Additions.Select(addition => PluginLogText.TypeName(addition.ServiceType)).ToArray(),
                record.Removals.Select(RemovedDescriptorOf).ToArray(),
                substitutions.Select(SubstitutionOf).ToArray()
            );
        }
    }

    private static PluginInventoryFileEntry DeclaredFileOf(PluginInventoryRow row) =>
        new(
            PluginLogText.Loggable(row.FileName)!,
            PluginLogText.Loggable(row.DeclaredPath)!,
            PluginLogText.Loggable(row.ResolvedRelativePath),
            row.Kind.ToString(),
            row.DeclaredAssemblyVersion?.ToString(),
            row.EffectiveVersion?.ToString(),
            row.EffectiveVersionSource.ToString(),
            row.Availability.ToString(),
            PluginLogText.Loggable(row.Sha256),
            row.LoadState.ToString()
        );

    private static PluginRemovedDescriptorEntry RemovedDescriptorOf(PluginDescriptorDisplacement removal) =>
        new(
            PluginLogText.TypeName(removal.ServiceType),
            removal.DisplacedImplementationType is null
                ? null
                : PluginLogText.TypeName(removal.DisplacedImplementationType)
        );

    private static PluginSubstitutionEntry SubstitutionOf(HostFirstSubstitution substitution) =>
        new(
            PluginLogText.Loggable(substitution.AssemblyName)!,
            substitution.RequestedVersion?.ToString(),
            substitution.HostVersion.ToString(),
            substitution.DeclaredVersion?.ToString()
        );
}

/// <summary>
/// One file a plugin's dependency manifest declared, as the inventory event reports it.
/// </summary>
/// <remarks>
/// A projection rather than the loader's own row, and versions are rendered as strings. The event is
/// a record an operator reads and a responder greps, so it carries the declaration and what the
/// process actually has side by side, with the source of each stated rather than filled in.
/// </remarks>
/// <param name="FileName">The file's name as it was published.</param>
/// <param name="DeclaredPath">The path the manifest wrote, which is package-relative.</param>
/// <param name="ResolvedRelativePath">Where the file was found, or null when it was not found.</param>
/// <param name="Kind">Managed, native or resource.</param>
/// <param name="DeclaredAssemblyVersion">
/// What the manifest declared, which stays null where it declared nothing. Kept apart from
/// <paramref name="EffectiveVersion"/> so that an absent declaration stays visible.
/// </param>
/// <param name="EffectiveVersion">The version the process has, where that is knowable.</param>
/// <param name="EffectiveVersionSource">Where that version came from.</param>
/// <param name="Availability">Whether the declared file is in the plugin directory.</param>
/// <param name="Sha256">The digest of the file, non-null exactly when it is present.</param>
/// <param name="LoadState">Whether this file's own bytes were loaded when the event was emitted.</param>
internal sealed record PluginInventoryFileEntry(
    string FileName,
    string DeclaredPath,
    string? ResolvedRelativePath,
    string Kind,
    string? DeclaredAssemblyVersion,
    string? EffectiveVersion,
    string EffectiveVersionSource,
    string Availability,
    string? Sha256,
    string LoadState
);

/// <summary>
/// A pre-existing descriptor a plugin removed or overwrote.
/// </summary>
/// <remarks>
/// Permitted outside the host-owned and logging-pipeline sets, and this event is the only trace one
/// leaves, which is why the implementation it displaced travels with the service type.
/// </remarks>
/// <param name="ServiceType">The service type whose descriptor went away.</param>
/// <param name="DisplacedImplementationType">
/// What that descriptor implemented, or null where it was a factory and there is no type to name.
/// </param>
internal sealed record PluginRemovedDescriptorEntry(string ServiceType, string? DisplacedImplementationType);

/// <summary>
/// An assembly the host served a plugin in place of the plugin's own copy.
/// </summary>
/// <remarks>
/// Host-first substitution is silent and almost always correct, which is why it needs a record: it is
/// the evidence behind the one report that otherwise arrives with none, that a plugin works on its
/// author's machine and not in the host.
/// </remarks>
/// <param name="AssemblyName">The simple name the plugin asked for.</param>
/// <param name="RequestedVersion">The version the reference carried, which is what the runtime asked for.</param>
/// <param name="HostVersion">The version the host served.</param>
/// <param name="DeclaredVersion">
/// What the plugin's manifest declared for that assembly, or null where it declares none, as a
/// shared-framework assembly does.
/// </param>
internal sealed record PluginSubstitutionEntry(
    string AssemblyName,
    string? RequestedVersion,
    string HostVersion,
    string? DeclaredVersion
);
