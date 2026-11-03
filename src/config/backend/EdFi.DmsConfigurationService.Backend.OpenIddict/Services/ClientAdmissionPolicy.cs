// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services;

/// <summary>
/// Decides whether a client that presented a valid secret is admitted, from its API client
/// approval state and, when it has no API client row, from its stored roles and registered
/// scopes. Pure: it reads nothing and logs nothing.
/// </summary>
public static class ClientAdmissionPolicy
{
    /// <summary>
    /// The engine-independent collision comparison of the two configured role names, applied
    /// after trailing white space is removed.
    /// </summary>
    private const CompareOptions RoleNameCollisionOptions =
        CompareOptions.IgnoreCase
        | CompareOptions.IgnoreWidth
        | CompareOptions.IgnoreKanaType
        | CompareOptions.IgnoreNonSpace;

    /// <summary>
    /// Decides the admission of a client that presented a valid secret.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A state that is not one of the named <see cref="ApiClientApprovalState"/> values is refused
    /// whatever the other inputs are. <see cref="ApiClientApprovalState.Approved"/> is admitted and
    /// <see cref="ApiClientApprovalState.Unapproved"/> refused under any role configuration, without
    /// regard to roles or scopes.
    /// </para>
    /// <para>
    /// A client with <see cref="ApiClientApprovalState.NoApiClientRow"/> is admitted only as an
    /// administrative client. First, the role configuration must be able to classify it:
    /// <paramref name="configServiceRole"/> is nonempty, the database does not resolve the two
    /// configured names to one role row (<paramref name="configuredRoleNamesShareARoleRow"/>
    /// false), and the two names do not collide after trailing white space is removed, compared
    /// with the invariant culture ignoring case, width, kana type and non-spacing marks. The
    /// database answer is what makes membership sound under any deployed collation; the
    /// comparison is a conservative restriction that refuses the same pairs on both engines.
    /// Then the client must hold <paramref name="configServiceRole"/> (ordinal comparison, as the
    /// role claim is compared), and its registered scopes must be nonempty and all Management API
    /// scopes (ordinal comparison). Holding <paramref name="clientRole"/> as well does not
    /// disqualify it.
    /// </para>
    /// </remarks>
    /// <param name="state">The approval state the client's credential carried.</param>
    /// <param name="roles">The client's stored role names. Read only for a client without an API client row.</param>
    /// <param name="scopes">The client's registered scope names. Read only for a client without an API client row.</param>
    /// <param name="configServiceRole">The configured administrative role name.</param>
    /// <param name="clientRole">The configured role name of DMS vendor clients.</param>
    /// <param name="configuredRoleNamesShareARoleRow">
    /// Whether the database resolves <paramref name="configServiceRole"/> and
    /// <paramref name="clientRole"/> to one role row. Read only for a client without an API client row.
    /// </param>
    public static ClientAdmission Decide(
        ApiClientApprovalState state,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> scopes,
        string configServiceRole,
        string clientRole,
        bool configuredRoleNamesShareARoleRow
    ) =>
        state switch
        {
            ApiClientApprovalState.Approved => ClientAdmission.Approved,
            ApiClientApprovalState.Unapproved => ClientAdmission.Unapproved,
            ApiClientApprovalState.NoApiClientRow => DecideWithoutApiClientRow(
                roles,
                scopes,
                configServiceRole,
                clientRole,
                configuredRoleNamesShareARoleRow
            ),
            _ => ClientAdmission.UnknownApprovalState,
        };

    private static ClientAdmission DecideWithoutApiClientRow(
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> scopes,
        string configServiceRole,
        string clientRole,
        bool configuredRoleNamesShareARoleRow
    )
    {
        if (
            string.IsNullOrEmpty(configServiceRole)
            || configuredRoleNamesShareARoleRow
            || RoleNamesCollide(configServiceRole, clientRole)
        )
        {
            return ClientAdmission.NoApiClientRowRoleConfiguration;
        }

        if (!roles.Contains(configServiceRole, StringComparer.Ordinal))
        {
            return roles.Contains(clientRole, StringComparer.Ordinal)
                ? ClientAdmission.NoApiClientRowHoldsClientRoleOnly
                : ClientAdmission.NoApiClientRowNoAdministrativeRole;
        }

        if (scopes.Count == 0)
        {
            return ClientAdmission.NoApiClientRowNoScopes;
        }

        return AdministrativeScopes.AllRecognized(scopes)
            ? ClientAdmission.AdministrativeWithoutApiClientRow
            : ClientAdmission.NoApiClientRowNonAdministrativeScope;
    }

    private static bool RoleNamesCollide(string configServiceRole, string clientRole) =>
        CultureInfo.InvariantCulture.CompareInfo.Compare(
            configServiceRole.TrimEnd(),
            clientRole.TrimEnd(),
            RoleNameCollisionOptions
        ) == 0;
}
