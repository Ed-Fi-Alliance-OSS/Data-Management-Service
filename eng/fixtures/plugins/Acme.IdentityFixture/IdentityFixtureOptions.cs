// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Identity;

namespace Acme.IdentityFixture;

/// <summary>
/// Everything the fixture reads from configuration, bound from the <c>IdentityFixture</c> section.
/// Every key is documented in the fixture README.
/// </summary>
public sealed class IdentityFixtureOptions
{
    public const string SectionName = "IdentityFixture";

    /// <summary>The operations the fixture advertises. Defaults to all five.</summary>
    public IdentityCapabilities Capabilities { get; set; } =
        IdentityCapabilities.Create
        | IdentityCapabilities.GetById
        | IdentityCapabilities.Find
        | IdentityCapabilities.Search
        | IdentityCapabilities.Results;

    /// <summary>The identity namespaces, each mapped from one or more tenant/qualifier contexts.</summary>
    public List<FixtureNamespaceOptions> Namespaces { get; set; } = [];

    /// <summary>The client/namespace grants the configuration policy source answers from.</summary>
    public List<FixtureGrantOptions> Grants { get; set; } = [];

    /// <summary>
    /// How long a control-channel policy answer is cached. Zero disables the cache. The configuration
    /// policy source is static and has no cache.
    /// </summary>
    public int PolicyCacheSeconds { get; set; }

    /// <summary>
    /// How many polls of an async job answer incomplete before the next one answers complete. Counted
    /// in polls, never in time.
    /// </summary>
    public int PollsUntilComplete { get; set; } = 1;

    /// <summary>
    /// The base address of the control channel. When set, the control channel is the policy source and
    /// the job-expiry source; when unset, configuration is.
    /// </summary>
    public string? ControlBaseAddress { get; set; }

    /// <summary>
    /// Where the provider throws a deliberate nested exception carrying person-shaped text. Unset by
    /// default.
    /// </summary>
    public FixtureThrowAt ThrowAt { get; set; } = FixtureThrowAt.None;

    /// <summary>
    /// Whether a search object carrying only <c>upstreamKey</c> is the exact reconciliation lookup.
    /// When false the fixture offers no reliable reconciliation.
    /// </summary>
    public bool ReconciliationLookup { get; set; } = true;
}

/// <summary>The stage at which <see cref="IdentityFixtureOptions.ThrowAt"/> makes the provider throw.</summary>
public enum FixtureThrowAt
{
    None,

    /// <summary>The service factory throws on every activation after the startup probe's.</summary>
    Factory,

    /// <summary>The provider constructor throws on every activation after the startup probe's.</summary>
    Constructor,

    /// <summary>The <c>Capabilities</c> getter throws.</summary>
    Capabilities,

    /// <summary>Every authorized operation throws.</summary>
    Operation,
}

public sealed class FixtureNamespaceOptions
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The contexts that select this namespace. Several may share one namespace.</summary>
    public List<FixtureContextOptions> Contexts { get; set; } = [];

    /// <summary>
    /// Persons that exist in this namespace from the start, under the explicit ids given. Independent
    /// namespaces may seed the same id.
    /// </summary>
    public List<FixtureSeedPersonOptions> SeedPersons { get; set; } = [];
}

public sealed class FixtureSeedPersonOptions
{
    public string UniqueId { get; set; } = string.Empty;

    /// <summary>The person's attributes by name, for example <c>LastSurname</c> and <c>FirstName</c>.</summary>
    public Dictionary<string, string> Attributes { get; set; } = [];
}

public sealed class FixtureContextOptions
{
    /// <summary>The tenant, or unset for single-tenant mode.</summary>
    public string? Tenant { get; set; }

    /// <summary>The complete route-qualifier set, by name.</summary>
    public Dictionary<string, string> Qualifiers { get; set; } = [];
}

public sealed class FixtureGrantOptions
{
    /// <summary>The client id, or <c>*</c> for an explicit tenant-wide grant.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string? Tenant { get; set; }

    public string Namespace { get; set; } = string.Empty;
}
