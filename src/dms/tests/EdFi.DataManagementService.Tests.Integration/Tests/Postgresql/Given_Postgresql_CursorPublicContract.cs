// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// The public cursor contract over HTTP on PostgreSQL. This binding carries the whole scenario,
/// including the parameter-validation rows: request validation and parameter canonicalization run
/// ahead of any provider, so a second engine would re-answer the same rejection from the same code
/// rather than add evidence. The SQL Server twin carries only the rows whose answer comes from page
/// selection or the selected keyset.
/// </summary>
/// <remarks>
/// Leases the descriptor runtime fixture, whose ApiSchema declares the regular resource these pages
/// are served from and the descriptor its reference resolves to. The deployed maximum page size is
/// left in place, because the messages under test quote it.
/// </remarks>
public sealed class Given_Postgresql_CursorPublicContract : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    [Test]
    public Task It_answers_a_cursor_parameter_fault_with_the_parameter_validation_shell() =>
        CursorPublicContractScenario.It_answers_a_cursor_parameter_fault_with_the_parameter_validation_shell(
            Harness
        );

    [Test]
    public Task It_reports_one_error_from_the_first_failing_phase() =>
        CursorPublicContractScenario.It_reports_one_error_from_the_first_failing_phase(Harness);

    [Test]
    public Task It_reports_a_mixed_mode_conflict_for_a_case_folded_limit() =>
        CursorPublicContractScenario.It_reports_a_mixed_mode_conflict_for_a_case_folded_limit(Harness);

    [Test]
    public Task It_keeps_only_the_last_value_of_a_repeated_page_size() =>
        CursorPublicContractScenario.It_keeps_only_the_last_value_of_a_repeated_page_size(Harness);

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
