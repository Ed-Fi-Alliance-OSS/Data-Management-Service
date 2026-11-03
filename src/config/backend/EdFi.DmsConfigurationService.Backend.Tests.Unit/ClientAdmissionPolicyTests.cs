// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// The admission decision for a client that presented a valid secret, one fixture per decision.
/// Roles and scopes are passed to the fixtures as comma-separated lists ("" is an empty list).
/// The scope names are spelled out rather than read from <c>AuthorizationScopes</c>, so a change
/// to the recognized names fails here instead of moving the expectation with it.
/// </summary>
public class ClientAdmissionPolicyTests
{
    private const string AdminRole = "cms-client";
    private const string VendorRole = "dms-client";
    private const string FullAccess = "edfi_admin_api/full_access";
    private const string ReadOnlyAccess = "edfi_admin_api/readonly_access";
    private const string AuthMetadataReadOnlyAccess = "edfi_admin_api/authMetadata_readonly_access";
    private const string ClaimSetShapedScope = "EdFiSandbox";

    private static IReadOnlyCollection<string> ListOf(string commaSeparated) =>
        commaSeparated.Length == 0 ? [] : commaSeparated.Split(',');

    private static ClientAdmission Decide(
        ApiClientApprovalState state,
        string roles,
        string scopes,
        string configServiceRole = AdminRole,
        string clientRole = VendorRole
    ) => ClientAdmissionPolicy.Decide(state, ListOf(roles), ListOf(scopes), configServiceRole, clientRole);

    // Row 1: an approved client is admitted under any role configuration, whatever its roles and
    // scopes, including the inputs that refuse a client without an API client row.
    [TestFixture(AdminRole, VendorRole, "", ClaimSetShapedScope)]
    [TestFixture(AdminRole, VendorRole, VendorRole, "")]
    [TestFixture(AdminRole, AdminRole, AdminRole, FullAccess)]
    [TestFixture("Admin", "admin", "admin", ClaimSetShapedScope)]
    [TestFixture("", VendorRole, VendorRole, ClaimSetShapedScope)]
    public class Given_An_Approved_Client(
        string configServiceRole,
        string clientRole,
        string roles,
        string scopes
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.Approved,
                roles,
                scopes,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_admits_the_client() => _admission.Should().Be(ClientAdmission.Approved);
    }

    // Row 2: an unapproved client is refused under any role configuration, including with the
    // inputs that admit a client without an API client row.
    [TestFixture(AdminRole, VendorRole, AdminRole, FullAccess)]
    [TestFixture(AdminRole, VendorRole, AdminRole + "," + VendorRole, ReadOnlyAccess)]
    [TestFixture(AdminRole, AdminRole, AdminRole, FullAccess)]
    [TestFixture("Admin", "admin", "Admin", FullAccess)]
    [TestFixture("", VendorRole, "", FullAccess)]
    public class Given_An_Unapproved_Client(
        string configServiceRole,
        string clientRole,
        string roles,
        string scopes
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.Unapproved,
                roles,
                scopes,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_refuses_the_client() => _admission.Should().Be(ClientAdmission.Unapproved);
    }

    // Row 3: without an API client row, a role configuration that cannot tell an administrative
    // client from a vendor client refuses the client, even one that holds the configured
    // administrative role and only Management API scopes. Names equal ordinally, names that differ
    // only by case (either way round), and an empty administrative role name all refuse.
    [TestFixture(AdminRole, AdminRole, AdminRole)]
    [TestFixture("Admin", "admin", "Admin")]
    [TestFixture("Admin", "admin", "admin")]
    [TestFixture("admin", "ADMIN", "admin")]
    [TestFixture("", VendorRole, "")]
    [TestFixture("", "", "")]
    public class Given_No_Api_Client_Row_Under_A_Role_Configuration_That_Cannot_Classify_It(
        string configServiceRole,
        string clientRole,
        string roles
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                // An empty role name stands for itself here: a client holding the role "" would be
                // admitted if the empty-name rule were missing.
                roles.Length == 0
                    ? ","
                    : roles,
                FullAccess,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_refuses_the_client_for_its_role_configuration() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowRoleConfiguration);
    }

    // Row 4: without an API client row, a client holding the client role but not the
    // administrative role is shaped like an orphaned vendor client and refused, whatever its scopes,
    // including a Management API scope. The administrative role in another casing is not held.
    [TestFixture(VendorRole, ClaimSetShapedScope)]
    [TestFixture(VendorRole, FullAccess)]
    [TestFixture(VendorRole, "")]
    [TestFixture(VendorRole + ",other-role", FullAccess)]
    [TestFixture(VendorRole + ",CMS-CLIENT", FullAccess)]
    public class Given_No_Api_Client_Row_Holding_Only_The_Client_Role(string roles, string scopes)
        : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() => _admission = Decide(ApiClientApprovalState.NoApiClientRow, roles, scopes);

        [Test]
        public void It_refuses_the_client_as_holding_only_the_client_role() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowHoldsClientRoleOnly);
    }

    // Row 5: without an API client row, a client holding neither configured role is refused. Role
    // names are compared ordinally, so either role in another casing is not held.
    [TestFixture("", FullAccess)]
    [TestFixture("other-role", FullAccess)]
    [TestFixture("Cms-Client", FullAccess)]
    [TestFixture("CMS-CLIENT,DMS-CLIENT", FullAccess)]
    [TestFixture("other-role", ClaimSetShapedScope)]
    public class Given_No_Api_Client_Row_Without_A_Recognized_Role(string roles, string scopes)
        : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() => _admission = Decide(ApiClientApprovalState.NoApiClientRow, roles, scopes);

        [Test]
        public void It_refuses_the_client_as_holding_no_administrative_role() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowNoAdministrativeRole);
    }

    // Row 6: without an API client row, the administrative role alone is not enough: a client with
    // no registered scopes is refused, with or without the client role.
    [TestFixture(AdminRole)]
    [TestFixture(AdminRole + "," + VendorRole)]
    public class Given_No_Api_Client_Row_With_The_Administrative_Role_And_No_Scopes(string roles)
        : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() => _admission = Decide(ApiClientApprovalState.NoApiClientRow, roles, "");

        [Test]
        public void It_refuses_the_client_as_having_no_scopes() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowNoScopes);
    }

    // Row 7: without an API client row, a client holding the administrative role is refused when
    // any of its scopes is not a Management API scope: a claim-set-shaped scope, a mixed set, a
    // Management API scope in another casing, or a near miss.
    [TestFixture(AdminRole, ClaimSetShapedScope)]
    [TestFixture(AdminRole, FullAccess + "," + ClaimSetShapedScope)]
    [TestFixture(AdminRole + "," + VendorRole, ReadOnlyAccess + "," + ClaimSetShapedScope)]
    [TestFixture(AdminRole, "EDFI_ADMIN_API/FULL_ACCESS")]
    [TestFixture(AdminRole, "edfi_admin_api")]
    [TestFixture(AdminRole, FullAccess + " ")]
    public class Given_No_Api_Client_Row_With_The_Administrative_Role_And_A_Non_Administrative_Scope(
        string roles,
        string scopes
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() => _admission = Decide(ApiClientApprovalState.NoApiClientRow, roles, scopes);

        [Test]
        public void It_refuses_the_client_as_having_a_non_administrative_scope() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowNonAdministrativeScope);
    }

    // Row 8: without an API client row, a client holding the administrative role (with or without
    // the client role) and only Management API scopes is admitted, under role names that differ
    // by more than case.
    [TestFixture(AdminRole, VendorRole, AdminRole, FullAccess)]
    [TestFixture(AdminRole, VendorRole, AdminRole + "," + VendorRole, FullAccess)]
    [TestFixture(AdminRole, VendorRole, VendorRole + "," + AdminRole, AuthMetadataReadOnlyAccess)]
    [TestFixture(
        AdminRole,
        VendorRole,
        AdminRole,
        FullAccess + "," + ReadOnlyAccess + "," + AuthMetadataReadOnlyAccess
    )]
    [TestFixture("Admin", "Vendor", "Admin", ReadOnlyAccess)]
    [TestFixture("operator", "", "operator", FullAccess)]
    public class Given_No_Api_Client_Row_With_The_Administrative_Role_And_Administrative_Scopes(
        string configServiceRole,
        string clientRole,
        string roles,
        string scopes
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                roles,
                scopes,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_admits_the_client_as_administrative() =>
            _admission.Should().Be(ClientAdmission.AdministrativeWithoutApiClientRow);
    }

    // Row 9: a state that is not a named value is refused before any other rule, both with every
    // other input admissible and under a role configuration that would otherwise refuse first.
    [TestFixture(3, AdminRole, VendorRole)]
    [TestFixture(7, AdminRole, VendorRole)]
    [TestFixture(-1, AdminRole, VendorRole)]
    [TestFixture(3, "Admin", "admin")]
    [TestFixture(3, "", VendorRole)]
    public class Given_An_Approval_State_That_Is_Not_Defined(
        int state,
        string configServiceRole,
        string clientRole
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                (ApiClientApprovalState)state,
                configServiceRole.Length == 0 ? "," : configServiceRole,
                FullAccess,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_refuses_the_client_as_having_an_unknown_state() =>
            _admission.Should().Be(ClientAdmission.UnknownApprovalState);
    }

    [TestFixture]
    public class Given_A_Decision_Nothing_Assigned : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() => _admission = default;

        [Test]
        public void It_is_a_refusal() => _admission.Should().Be(ClientAdmission.UnknownApprovalState);
    }
}
