// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The decision a token store makes from the state it read under the application row lock. The
/// engine's SQL produces only the named approval states; a state with no name is covered here,
/// against the decision itself, because no store can read one. Nothing is allowed by default. The
/// secret comparison is ordinal although the database's default collation is not.
/// </summary>
public class OpenIddictStoreRefusalReasonTests
{
    private const string Secret = "hashed-secret";

    [TestFixture(Secret, 1, ApiClientApprovalState.Approved, null)]
    [TestFixture(Secret, 2, ApiClientApprovalState.NoApiClientRow, null)]
    [TestFixture(Secret, 0, ApiClientApprovalState.Approved, "approval")]
    [TestFixture(Secret, 0, ApiClientApprovalState.NoApiClientRow, "approval")]
    [TestFixture(Secret, 2, ApiClientApprovalState.Approved, "api-client-row")]
    [TestFixture(Secret, 1, ApiClientApprovalState.NoApiClientRow, "api-client-row")]
    [TestFixture(Secret, 7, ApiClientApprovalState.Approved, "api-client-row")]
    [TestFixture(Secret, 7, ApiClientApprovalState.NoApiClientRow, "api-client-row")]
    [TestFixture("rotated-secret", 0, ApiClientApprovalState.Approved, "secret")]
    [TestFixture("HASHED-SECRET", 1, ApiClientApprovalState.Approved, "secret")]
    public class Given_A_Stored_Client_State(
        string storedSecret,
        int storedApproval,
        ApiClientApprovalState expectedApproval,
        string? expectedReason
    ) : OpenIddictStoreRefusalReasonTests
    {
        private string? _reason;

        [SetUp]
        public void Setup() =>
            _reason = OpenIddictDataRepository.RefusalReason(
                new OpenIddictDataRepository.StoredClientState(storedSecret, storedApproval),
                Secret,
                expectedApproval
            );

        [Test]
        public void It_allows_only_the_admissible_state_the_grant_authenticated_with() =>
            _reason.Should().Be(expectedReason);
    }

    [TestFixture]
    public class Given_No_Stored_Client_State : OpenIddictStoreRefusalReasonTests
    {
        [Test]
        public void It_refuses_the_store_as_missing() =>
            OpenIddictDataRepository
                .RefusalReason(null, Secret, ApiClientApprovalState.Approved)
                .Should()
                .Be("missing");
    }
}
