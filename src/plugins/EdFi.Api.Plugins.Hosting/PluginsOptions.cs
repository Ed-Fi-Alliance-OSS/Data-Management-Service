// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The bindable shape of the host-owned <c>Plugins</c> configuration section, which is the only
/// configuration surface plugins have.
/// </summary>
/// <remarks>
/// <para>
/// A mutable class with a public parameterless constructor is what configuration binding requires, so
/// this is deliberately not a record. The property defaults are the shipped defaults: an absent
/// section binds to exactly the same values as a section that writes them out.
/// </para>
/// <para>
/// The whole section binds from environment variables in the standard way, so
/// <c>Plugins__Allowed=A,B</c> is equivalent to the JSON form, which is how a container deployment
/// sets it.
/// </para>
/// </remarks>
public sealed class PluginsOptions
{
    /// <summary>
    /// The plugin root, defaulting to <c>/app/plugins</c> and resolved against
    /// <see cref="AppContext.BaseDirectory"/> when it is not fully qualified.
    /// </summary>
    public string Directory { get; set; } = "/app/plugins";

    /// <summary>
    /// The comma-delimited list of plugin directory names that may load, defaulting to the empty
    /// string, which asks for no plugins at all.
    /// </summary>
    /// <remarks>
    /// The written order is the invocation order for the composition phases, so the list is never
    /// sorted. A delimited string rather than an array follows the convention
    /// <c>AppSettings:AllowIdentityUpdateOverrides</c> already sets for a list with no entries.
    /// </remarks>
    public string Allowed { get; set; } = string.Empty;
}
