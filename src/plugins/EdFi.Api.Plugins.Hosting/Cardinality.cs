// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// How many implementations of a plugin contract the host accepts, and what happens when more arrive.
/// </summary>
/// <remarks>
/// Cardinality is metadata the <em>host</em> holds about each contract rather than something a plugin
/// declares, so a plugin cannot opt out of being counted. Each host supplies its own set of
/// contract-and-cardinality entries; nothing here is read from configuration or from a plugin.
/// </remarks>
public enum Cardinality
{
    /// <summary>
    /// Many implementations, all of which the host invokes. Registered with
    /// <c>TryAddEnumerable</c> and a transient lifetime; two plugins contributing is not a conflict,
    /// because more implementations is more of what the contract is for.
    /// </summary>
    FanIn,

    /// <summary>
    /// Zero or one implementation, displacing a host default. Registered with a plain <c>Add</c> and
    /// never a <c>TryAdd</c>, because a contract with one claimant has nothing to try. Two claims are
    /// fatal, whether they come from two plugins or from one plugin registering twice: which of two
    /// vendors is live is an operator decision and the operator is the only party who can resolve it.
    /// </summary>
    Replace,

    /// <summary>
    /// Many configuration sources, ordered by the operator's allowlist. Later plugin sources win over
    /// earlier ones, and every plugin source sits below the operator's own explicit sources.
    /// </summary>
    Contribute,
}
