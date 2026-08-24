// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server twin of the public cursor contract, restricted to the rows whose answer is produced by
/// page selection or by the selected keyset that anchors a continuation: the zero-size page, the
/// terminal page that ends a walk, and the traditional page that carries a continuation. Those are
/// compiled and executed per provider, so each engine has to be asked.
/// </summary>
/// <remarks>
/// The parameter-validation rows are deliberately absent. Canonicalization and cursor validation run
/// before any provider is involved and return the same rejection from the same code on both engines,
/// so binding them here would duplicate a PostgreSQL answer rather than test SQL Server.
/// </remarks>
public sealed class Given_Mssql_CursorPublicContract : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    [Test]
    public Task It_returns_an_empty_page_without_a_continuation_for_a_zero_size_page() =>
        CursorPublicContractScenario.It_returns_an_empty_page_without_a_continuation_for_a_zero_size_page(
            Harness
        );

    [Test]
    public Task It_ends_a_walk_with_one_trailing_empty_page() =>
        CursorPublicContractScenario.It_ends_a_walk_with_one_trailing_empty_page(Harness);

    [Test]
    public Task It_preserves_the_traditional_page_contract_and_adds_a_continuation() =>
        CursorPublicContractScenario.It_preserves_the_traditional_page_contract_and_adds_a_continuation(
            Harness
        );
}
