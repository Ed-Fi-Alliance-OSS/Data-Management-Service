// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services;

/// <summary>
/// The decision <see cref="ClientAdmissionPolicy"/> makes for a client that presented a valid
/// secret. Only <see cref="Approved"/> and <see cref="AdministrativeWithoutApiClientRow"/> admit
/// the client; every other value refuses it. The names are fixed content that may be logged. The
/// zero value refuses, so a decision nothing assigned is never read as an admission.
/// </summary>
public enum ClientAdmission
{
    /// <summary>
    /// The approval state is not one of the named <c>ApiClientApprovalState</c> values. Refused.
    /// </summary>
    UnknownApprovalState = 0,

    /// <summary>Every API client row under the client id is approved. Admitted.</summary>
    Approved,

    /// <summary>At least one API client row under the client id is not approved. Refused.</summary>
    Unapproved,

    /// <summary>
    /// No API client row, and the configured administrative role name is empty or equals the
    /// configured client role name ignoring case, so role membership cannot classify the client.
    /// Refused.
    /// </summary>
    NoApiClientRowRoleConfiguration,

    /// <summary>
    /// No API client row, and the client holds the client role but not the administrative role:
    /// shaped like a vendor client whose API client row is gone. Refused.
    /// </summary>
    NoApiClientRowHoldsClientRoleOnly,

    /// <summary>
    /// No API client row, and the client holds neither the administrative role nor the client role.
    /// Refused.
    /// </summary>
    NoApiClientRowNoAdministrativeRole,

    /// <summary>
    /// No API client row, and the client holds the administrative role but has no registered
    /// scopes. Refused.
    /// </summary>
    NoApiClientRowNoScopes,

    /// <summary>
    /// No API client row, and the client holds the administrative role but at least one of its
    /// registered scopes is not a Management API scope. Refused.
    /// </summary>
    NoApiClientRowNonAdministrativeScope,

    /// <summary>
    /// No API client row, and the client holds the administrative role and only Management API
    /// scopes. Admitted.
    /// </summary>
    AdministrativeWithoutApiClientRow,
}
