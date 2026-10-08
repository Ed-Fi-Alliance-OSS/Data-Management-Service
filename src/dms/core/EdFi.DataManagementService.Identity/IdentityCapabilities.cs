// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Identity;

/// <summary>
/// The set of identity operations a registered <c>IIdentityService</c> supports, exposed through its
/// <c>Capabilities</c> getter.
/// The getter must be inexpensive, perform no I/O, and return a stable value across requests and
/// instances of the registration for the configured deployment; an upstream outage does not change it.
/// DMS reads the getter once per request after activation and uses that captured value for both the
/// requested-operation gate and the results-capability invariant, so a configuration change requiring a
/// different capability set only takes effect on restart.
/// This first contract is deployment-wide rather than per-tenant or per-route-qualifier; namespace access
/// within a capability is governed by provider policy instead, and an unknown or unauthorized namespace
/// returns <c>IdentityResultStatus.NotFound</c> rather than a missing-capability response.
/// </summary>
[Flags]
public enum IdentityCapabilities
{
    /// <summary>
    /// No identity operation is supported.
    /// </summary>
    None = 0,

    /// <summary>
    /// The provider supports <c>CreateAsync</c>.
    /// </summary>
    Create = 1,

    /// <summary>
    /// The provider supports <c>GetByIdAsync</c>.
    /// </summary>
    GetById = 2,

    /// <summary>
    /// The provider supports <c>FindAsync</c>.
    /// </summary>
    Find = 4,

    /// <summary>
    /// The provider supports <c>SearchAsync</c>.
    /// </summary>
    Search = 8,

    /// <summary>
    /// The provider supports <c>ResultsAsync</c>, which retrieves the outcome of a previously
    /// accepted asynchronous find or search job.
    /// This capability, not <c>Find</c> or <c>Search</c> alone, is what DMS checks before
    /// dispatching a request token to <c>ResultsAsync</c>.
    /// </summary>
    Results = 16,
}
