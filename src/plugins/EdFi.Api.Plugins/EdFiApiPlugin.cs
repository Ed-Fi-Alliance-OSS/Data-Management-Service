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
/// context and invokes the contribution hook below on the single instance it finds there. The entry
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
