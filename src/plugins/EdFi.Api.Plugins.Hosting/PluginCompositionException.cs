// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The rule that refused a plugin's service contribution.
/// </summary>
/// <remarks>
/// Separate from <see cref="PluginLoadFailure"/> because those rows are decided while a plugin is
/// being loaded and these are decided while its hook is running, after it loaded successfully.
/// </remarks>
public enum PluginCompositionFailure
{
    /// <summary>
    /// The plugin removed or overwrote a descriptor that was present before its hook began and whose
    /// service type is declared in a host assembly. A plugin contributes registrations; it does not
    /// edit the host's.
    /// </summary>
    HostOwnedDescriptorDisplaced,

    /// <summary>
    /// The plugin removed or overwrote a pre-existing descriptor for one of the four logging-pipeline
    /// service types. Clearing the host's providers silences every host log line and the plugin
    /// inventory record with it.
    /// </summary>
    LoggingPipelineDescriptorDisplaced,

    /// <summary>
    /// The plugin added an unkeyed registration of a logging service the host reserves:
    /// <c>ILoggerFactory</c>, the non-generic <c>ILogger</c>, or <c>ILogger&lt;&gt;</c> open or closed.
    /// Such a registration displaces the host's logging by winning a later single-service resolve,
    /// without removing anything, so the removal rules never see it. Adding an <c>ILoggerProvider</c>
    /// is untouched, and so is a keyed logging registration.
    /// </summary>
    ReservedLoggingServiceRegistered,

    /// <summary>The plugin cleared the service collection, which necessarily removes the host's own
    /// registrations.</summary>
    ServiceCollectionCleared,

    /// <summary>
    /// The plugin's contribution hook threw. The original exception travels as the inner exception,
    /// because it is the only thing that says what the plugin was doing.
    /// </summary>
    ContributeServicesThrew,
}

/// <summary>
/// Thrown when a plugin's service contribution is refused. Every refusal is fatal.
/// </summary>
/// <remarks>
/// The message is what an operator reads and always names the plugin and the service type involved.
/// <see cref="Reason"/> is what code reads, so a caller does not have to match on message text.
/// </remarks>
public sealed class PluginCompositionException(
    PluginCompositionFailure reason,
    string pluginName,
    string message,
    Exception? innerException = null
) : Exception(message, innerException)
{
    /// <summary>The rule that refused the contribution.</summary>
    public PluginCompositionFailure Reason { get; } = reason;

    /// <summary>The plugin whose hook was running.</summary>
    public string PluginName { get; } = pluginName;
}
