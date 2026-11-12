// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

public class ApplicationInfoTests
{
    // An application whose approval nothing read must be refused, never taken as approved.
    [TestFixture]
    public class Given_An_Application_Nothing_Populated : ApplicationInfoTests
    {
        private ApplicationInfo _application = null!;

        [SetUp]
        public void Setup() => _application = new ApplicationInfo();

        [Test]
        public void It_defaults_to_unapproved() =>
            _application.ApiClientApproval.Should().Be(ApiClientApprovalState.Unapproved);

        [Test]
        public void It_is_not_token_limit_exempt() => _application.IsTokenLimitExempt.Should().BeFalse();
    }

    // The token-limit exemption needs no API client row and a nonempty set of registered scopes
    // that are all recognized admin scopes, matched exactly. Any other state, an empty set, a mixed
    // set or a differently cased scope keeps the limit.
    [TestFixture(ApiClientApprovalState.NoApiClientRow, "edfi_admin_api/full_access", true)]
    [TestFixture(ApiClientApprovalState.NoApiClientRow, "edfi_admin_api/readonly_access", true)]
    [TestFixture(ApiClientApprovalState.NoApiClientRow, "edfi_admin_api/authMetadata_readonly_access", true)]
    [TestFixture(
        ApiClientApprovalState.NoApiClientRow,
        "edfi_admin_api/readonly_access,edfi_admin_api/full_access,edfi_admin_api/authMetadata_readonly_access",
        true
    )]
    [TestFixture(ApiClientApprovalState.NoApiClientRow, "", false)]
    [TestFixture(ApiClientApprovalState.NoApiClientRow, "edfi_admin_api/full_access,EdFiSandbox", false)]
    [TestFixture(ApiClientApprovalState.NoApiClientRow, "EDFI_ADMIN_API/full_access", false)]
    [TestFixture(
        ApiClientApprovalState.NoApiClientRow,
        "edfi_admin_api/full_access,edfi_admin_api/sis",
        false
    )]
    [TestFixture(ApiClientApprovalState.Approved, "edfi_admin_api/full_access", false)]
    [TestFixture(ApiClientApprovalState.Unapproved, "edfi_admin_api/full_access", false)]
    [TestFixture((ApiClientApprovalState)7, "edfi_admin_api/full_access", false)]
    public class Given_The_Token_Limit_Exemption(
        ApiClientApprovalState approval,
        string registeredScopes,
        bool expectedExemption
    ) : ApplicationInfoTests
    {
        private ApplicationInfo _application = null!;

        [SetUp]
        public void Setup() =>
            _application = new ApplicationInfo
            {
                ApiClientApproval = approval,
                Scopes = registeredScopes.Split(',', StringSplitOptions.RemoveEmptyEntries),
            };

        [Test]
        public void It_decides_the_exemption_from_the_state_and_the_registered_scopes() =>
            _application.IsTokenLimitExempt.Should().Be(expectedExemption);
    }
}
