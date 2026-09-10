// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.ObjectModel;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The rule a plugin's registrations broke, decided after the container was built.
/// </summary>
/// <remarks>
/// These are the checks the wrapper cannot make, because each of them needs either the host's own
/// contract metadata or a built container. The wrapper's own refusals are
/// <see cref="PluginCompositionFailure"/> and happen earlier.
/// </remarks>
public enum PluginAuditFailure
{
    /// <summary>
    /// More than one descriptor was contributed for a replace-cardinality contract, whether by two
    /// plugins or by one plugin registering twice. A replace contract has one claimant, and which
    /// claimant is live is an operator decision.
    /// </summary>
    ReplaceContractClaimedMoreThanOnce,

    /// <summary>
    /// A plugin registered a service type declared in a host assembly that is no declared contract. A
    /// guardrail against an implementer reading "contribute services" as "register whatever the host
    /// resolves", not an isolation property.
    /// </summary>
    HostOwnedServiceTypeClaimed,

    /// <summary>
    /// A plugin's hook ran and left no surviving declared-contract registration, so it contributed
    /// nothing the host will ever call. Covers both a plugin that registered no declared contract at
    /// all and one whose declared registrations were all removed before composition ended; the two
    /// are told apart in the message.
    /// </summary>
    NoDeclaredContractRegistered,

    /// <summary>
    /// A registration of a declared contract could not be constructed. The exception the container
    /// raised travels on the finding.
    /// </summary>
    DeclaredContractRegistrationNotActivatable,

    /// <summary>
    /// A declared contract was registered under the wildcard service key, which the host cannot
    /// activate at startup because no key it holds selects that registration. Refused whoever
    /// registered it; a finding the records cannot attribute to a plugin names none.
    /// </summary>
    DeclaredContractRegisteredUnderWildcardKey,
}

/// <summary>
/// One thing the plugin startup checks found.
/// </summary>
/// <remarks>
/// The plugin names are the ones the host recorded as contributing to whatever the finding is about,
/// which is not always the same as the party at fault: an activation failure can originate in a
/// dependency no plugin registered. The message says which of the two it is claiming. An empty list
/// means the records attribute the finding to nobody, which is a statement about the evidence rather
/// than an assertion that the host is at fault.
/// </remarks>
public sealed class PluginAuditFinding
{
    internal PluginAuditFinding(
        PluginAuditFailure reason,
        IReadOnlyList<string> pluginNames,
        Type? contract,
        string message,
        Exception? activationException = null
    )
    {
        Reason = reason;
        PluginNames = new ReadOnlyCollection<string>([.. pluginNames]);
        Contract = contract;
        Message = message;
        ActivationException = activationException;
    }

    /// <summary>The rule that was broken.</summary>
    public PluginAuditFailure Reason { get; }

    /// <summary>The plugins the host recorded as contributing to this, in allowlist order.</summary>
    public IReadOnlyList<string> PluginNames { get; }

    /// <summary>The declared contract this is about, where the finding is about one.</summary>
    public Type? Contract { get; }

    /// <summary>What an operator reads.</summary>
    public string Message { get; }

    /// <summary>
    /// The exception the container raised, preserved rather than reformatted, for an activation
    /// failure.
    /// </summary>
    public Exception? ActivationException { get; }
}

/// <summary>
/// What the plugin startup checks produced, with a failure to release the probe's scope kept apart
/// from the findings.
/// </summary>
/// <remarks>
/// Releasing the scope is cleanup, and a cleanup failure must not stand in for, or hide, whatever the
/// checks found. So it comes back here for the caller to log rather than being thrown or folded into
/// the findings.
/// </remarks>
public sealed class PluginAuditResult
{
    internal PluginAuditResult(IReadOnlyList<PluginAuditFinding> findings, Exception? scopeCleanupFailure)
    {
        Findings = new ReadOnlyCollection<PluginAuditFinding>([.. findings]);
        ScopeCleanupFailure = scopeCleanupFailure;
    }

    /// <summary>Everything the checks found, so one startup attempt reports every problem.</summary>
    public IReadOnlyList<PluginAuditFinding> Findings { get; }

    /// <summary>The failure releasing the activation scope, when there was one.</summary>
    public Exception? ScopeCleanupFailure { get; }
}
