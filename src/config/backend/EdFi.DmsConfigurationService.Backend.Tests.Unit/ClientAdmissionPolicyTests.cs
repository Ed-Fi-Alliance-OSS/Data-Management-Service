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
/// "X" is the database's answer to whether the two configured role names resolve to one role row.
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
        bool configuredRoleNamesShareARoleRow,
        string configServiceRole = AdminRole,
        string clientRole = VendorRole
    ) =>
        ClientAdmissionPolicy.Decide(
            state,
            ListOf(roles),
            ListOf(scopes),
            configServiceRole,
            clientRole,
            configuredRoleNamesShareARoleRow
        );

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
                configuredRoleNamesShareARoleRow: false,
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
                configuredRoleNamesShareARoleRow: false,
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
                configuredRoleNamesShareARoleRow: false,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_refuses_the_client_for_its_role_configuration() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowRoleConfiguration);
    }

    // Row 3, clause 3: names that differ only by trailing white space collide (either way round),
    // as SQL Server's padded comparison treats them. Spaces, an ideographic space and a tab are all
    // removed. The client holds the administrative role name exactly, so row 8 would otherwise
    // admit it.
    [TestFixture(AdminRole, AdminRole + " ")]
    [TestFixture(AdminRole + " ", AdminRole)]
    [TestFixture(AdminRole, AdminRole + "   ")]
    [TestFixture(AdminRole, AdminRole + "　")]
    [TestFixture(AdminRole, AdminRole + "\t")]
    public class Given_No_Api_Client_Row_Under_Role_Names_That_Differ_Only_By_Trailing_White_Space(
        string configServiceRole,
        string clientRole
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                configServiceRole,
                FullAccess,
                configuredRoleNamesShareARoleRow: false,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_refuses_the_client_for_its_role_configuration() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowRoleConfiguration);
    }

    // Row 3, clause 3: names that differ only by character width collide, full-width letters
    // against their ASCII forms, alone or combined with case.
    [TestFixture("Admin", "Ａdmin")]
    [TestFixture("admin", "ＡＤＭＩＮ")]
    public class Given_No_Api_Client_Row_Under_Role_Names_That_Differ_Only_By_Width(
        string configServiceRole,
        string clientRole
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                configServiceRole,
                FullAccess,
                configuredRoleNamesShareARoleRow: false,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_refuses_the_client_for_its_role_configuration() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowRoleConfiguration);
    }

    // Row 3, clause 3: names that differ only by kana type collide, hiragana against katakana
    // (either way round).
    [TestFixture("かんり", "カンリ")]
    [TestFixture("カンリ", "かんり")]
    public class Given_No_Api_Client_Row_Under_Role_Names_That_Differ_Only_By_Kana_Type(
        string configServiceRole,
        string clientRole
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                configServiceRole,
                FullAccess,
                configuredRoleNamesShareARoleRow: false,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_refuses_the_client_for_its_role_configuration() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowRoleConfiguration);
    }

    // Row 3, clause 3: names that differ only by a non-spacing mark (an accent) collide.
    [TestFixture("Admin", "Ádmin")]
    [TestFixture("Ádmin", "Admin")]
    public class Given_No_Api_Client_Row_Under_Role_Names_That_Differ_Only_By_Non_Spacing_Marks(
        string configServiceRole,
        string clientRole
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                configServiceRole,
                FullAccess,
                configuredRoleNamesShareARoleRow: false,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_refuses_the_client_for_its_role_configuration() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowRoleConfiguration);
    }

    // Row 3, clause 3: a whitespace-only administrative role name is not empty, so clause 1 does
    // not fire, but it collides with an empty client role name once trailing white space is
    // removed. The client holds the whitespace-only name, so row 8 would otherwise admit it.
    [TestFixture]
    public class Given_No_Api_Client_Row_Under_A_White_Space_Only_Administrative_Role_And_An_Empty_Client_Role
        : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                "  ",
                FullAccess,
                configuredRoleNamesShareARoleRow: false,
                "  ",
                ""
            );

        [Test]
        public void It_refuses_the_client_for_its_role_configuration() =>
            _admission.Should().Be(ClientAdmission.NoApiClientRowRoleConfiguration);
    }

    // Row 3, clause 2 alone: names that are distinct under every comparison the policy makes still
    // refuse when the database resolves them to one role row.
    [TestFixture(AdminRole, VendorRole)]
    [TestFixture("operator", "")]
    public class Given_No_Api_Client_Row_Under_Distinct_Role_Names_The_Database_Resolves_To_One_Role_Row(
        string configServiceRole,
        string clientRole
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                configServiceRole,
                FullAccess,
                configuredRoleNamesShareARoleRow: true,
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
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                roles,
                scopes,
                configuredRoleNamesShareARoleRow: false
            );

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
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                roles,
                scopes,
                configuredRoleNamesShareARoleRow: false
            );

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
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                roles,
                "",
                configuredRoleNamesShareARoleRow: false
            );

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
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                roles,
                scopes,
                configuredRoleNamesShareARoleRow: false
            );

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
                configuredRoleNamesShareARoleRow: false,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_admits_the_client_as_administrative() =>
            _admission.Should().Be(ClientAdmission.AdministrativeWithoutApiClientRow);
    }

    // Row 8 at the collision boundary: leading white space is significant on both engines, and a
    // whitespace-only administrative role name that does not collide with the client role is left
    // to role membership (clause 1 tests emptiness only). In each case the client holds the
    // administrative role name exactly and the database keeps the two names apart.
    [TestFixture(AdminRole, " " + AdminRole)]
    [TestFixture(" " + AdminRole, AdminRole)]
    [TestFixture("  ", VendorRole)]
    public class Given_No_Api_Client_Row_Under_Role_Names_That_Do_Not_Collide(
        string configServiceRole,
        string clientRole
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(
                ApiClientApprovalState.NoApiClientRow,
                configServiceRole,
                FullAccess,
                configuredRoleNamesShareARoleRow: false,
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
                configuredRoleNamesShareARoleRow: false,
                configServiceRole,
                clientRole
            );

        [Test]
        public void It_refuses_the_client_as_having_an_unknown_state() =>
            _admission.Should().Be(ClientAdmission.UnknownApprovalState);
    }

    // Rows 1, 2 and 9 do not depend on the database's answer: when it resolves the two configured
    // names to one role row, an approved client is still admitted, an unapproved one still refused,
    // and an undefined state still refused as unknown, with inputs row 8 would otherwise admit.
    [TestFixture(ApiClientApprovalState.Approved, ClientAdmission.Approved)]
    [TestFixture(ApiClientApprovalState.Unapproved, ClientAdmission.Unapproved)]
    [TestFixture((ApiClientApprovalState)3, ClientAdmission.UnknownApprovalState)]
    public class Given_An_Api_Client_State_When_The_Database_Resolves_The_Role_Names_To_One_Role_Row(
        ApiClientApprovalState state,
        ClientAdmission expected
    ) : ClientAdmissionPolicyTests
    {
        private ClientAdmission _admission;

        [SetUp]
        public void Setup() =>
            _admission = Decide(state, AdminRole, FullAccess, configuredRoleNamesShareARoleRow: true);

        [Test]
        public void It_decides_as_the_state_alone_does() => _admission.Should().Be(expected);
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
