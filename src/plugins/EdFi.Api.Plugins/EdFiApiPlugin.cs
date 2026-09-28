// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins;

/// <summary>
/// The base class every Ed-Fi API plugin implements.
/// </summary>
/// <remarks>
/// <para>
/// A plugin is published as a directory of assemblies, dropped into a host's plugin root, and named
/// in the host's allowlist. The host loads the directory's entry assembly into an isolated load
/// context and invokes the contribution hooks below on the single instance it finds there. The entry
/// assembly must expose exactly one public, non-abstract subclass of this type with a public
/// parameterless constructor: zero is fatal, because the operator allowlisted a directory that
/// contributes nothing, and more than one is fatal, because choosing between them would be
/// arbitrary.
/// </para>
/// <para>
/// This type is named for the Ed-Fi API platform rather than for one host, because the Data
/// Management Service and the Configuration Service share the same loader and the same contract.
/// </para>
/// <para>
/// The compatibility policy for this package is additive-only for the life of the package: new
/// virtual members with no-op bodies, never a new abstract member, never a signature change, never a
/// removal. A plugin compiled against an older version of this contract therefore runs on a newer
/// host without being rebuilt, which is the direction the upgrade story needs. The reverse is not
/// supported: a plugin compiled against a newer contract than the host carries is refused at load
/// with a named error rather than failing later inside a hook.
/// </para>
/// </remarks>
public abstract class EdFiApiPlugin
{
    /// <summary>
    /// The plugin's name, which must equal the name of the directory the plugin was loaded from.
    /// </summary>
    /// <remarks>
    /// The host verifies this at load time and treats a mismatch as fatal, so that the name an
    /// operator writes in the allowlist and the name that appears in logs and startup diagnostics are
    /// the same string.
    /// </remarks>
    public abstract string Name { get; }

    /// <summary>
    /// Contributes configuration sources to the host after plugins load and before the host registers
    /// its services, which is where it first reads configuration a plugin can supply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Override to add the plugin's own configuration sources, such as one that reads values from a
    /// secrets vault. The host calls this hook on every loaded plugin unconditionally, in allowlist
    /// order, before it calls <see cref="ContributeServices"/> on any of them. The base implementation
    /// is a no-op, so a plugin that contributes no configuration simply does not override it.
    /// </para>
    /// <para>
    /// Contribution is additive only. Add sources; do not remove or reorder a source that was present
    /// when the hook began, which the host detects and treats as fatal. After the hook returns, the
    /// host loads the sources it added, once and in the order they were added, and inserts them into
    /// its own configuration as one source, below the operator's environment variables and
    /// command-line arguments and above every JSON source. So an operator's explicit setting outranks
    /// every source added here, and a later plugin in the allowlist outranks an earlier one. A source
    /// that throws when the host loads it fails startup, naming this plugin.
    /// </para>
    /// </remarks>
    /// <param name="configurationBuilder">
    /// A builder for this hook alone, holding the host's sources as they stand when the hook runs and
    /// the host's builder properties. Adding a source to it loads nothing. Its sources are a mutable
    /// list, so the host compares them with its own after the call: a source that was present before
    /// the hook and is absent or moved afterwards fails startup, naming this plugin. Changing a property
    /// of a pre-existing source object, such as an environment variable source's prefix or a JSON
    /// source's path, is not detectable at this seam and is a trust assumption the host does not
    /// enforce.
    /// </param>
    /// <param name="bootstrapConfiguration">
    /// The configuration already layered when the hook runs, supplied so the plugin can read the
    /// settings it needs to build its sources, such as its own vault address. It is the host's live
    /// configuration rather than a copy or a read-only facade, so nothing stops a plugin writing
    /// through it or casting it back to a builder; not doing so is a trust assumption the host does
    /// not enforce, and the effect on the host is undefined.
    /// </param>
    public virtual void ContributeConfiguration(
        IConfigurationBuilder configurationBuilder,
        IConfiguration bootstrapConfiguration
    )
    {
        // Intentionally does nothing, for the reason ContributeServices gives. This member is the
        // contract's first additive evolution: a plugin compiled against 1.0.0 cannot know of it, so it
        // runs unchanged on a host carrying this version.
    }

    /// <summary>
    /// Contributes service registrations to the host's container before it is built.
    /// </summary>
    /// <remarks>
    /// Override to register the plugin's own services. The body is ordinary registration code; the
    /// host calls it on every loaded plugin unconditionally, so there is no interface to implement
    /// and no way for an allowlisted plugin to be skipped. The base implementation is a no-op, so a
    /// plugin that contributes no services simply does not override it.
    /// </remarks>
    /// <param name="services">The host's service collection, as it stands before the container is built.</param>
    /// <param name="configuration">
    /// The host's own configuration, fully layered, supplied so the plugin can read the settings it
    /// needs. It is the live instance rather than a copy or a read-only facade, so nothing stops a
    /// plugin reaching through <see cref="IConfiguration"/> to write; doing so is outside what this
    /// contract supports and the effect on the host is undefined.
    /// </param>
    public virtual void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // Intentionally does nothing. The host calls this hook on every loaded plugin rather than
        // testing for an optional interface, so the base body is what lets a plugin that contributes
        // no services simply not override it. Adding a further virtual with a no-op body is also the
        // only evolution this contract's additive-only policy allows, and it is binary-compatible
        // with every plugin already published against an earlier version.
    }
}
