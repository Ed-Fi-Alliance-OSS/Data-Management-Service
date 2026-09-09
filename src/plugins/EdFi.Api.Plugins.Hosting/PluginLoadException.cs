// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Thrown when an allowlisted plugin cannot be loaded. Every plugin failure is fatal, so this is the
/// single exception a host sees from the loader.
/// </summary>
/// <remarks>
/// The message is what an operator reads and always names the entry and the rule that refused it. The
/// <see cref="Reason"/> is what code reads, so a caller does not have to match on message text, and
/// the original failure is preserved as the inner exception wherever the runtime produced one.
/// </remarks>
public sealed class PluginLoadException(
    PluginLoadFailure reason,
    string? pluginName,
    string message,
    Exception? innerException = null
) : Exception(message, innerException)
{
    /// <summary>The rule that refused the plugin.</summary>
    public PluginLoadFailure Reason { get; } = reason;

    /// <summary>
    /// The allowlist entry the failure belongs to, or <see langword="null"/> for a failure of the
    /// configuration or the plugin root, which belongs to no single entry.
    /// </summary>
    public string? PluginName { get; } = pluginName;
}
