// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// Collection reads with <c>limit=0</c> on descriptor endpoints and on <c>/deletes</c> and <c>/keyChanges</c>,
/// answered relationally, with and without <c>totalCount</c>.
/// </summary>
public sealed class Given_Mssql_LimitZeroCountReads : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override string AllowIdentityUpdateOverrides => "Student";

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new AllowAllClaimSetProvider(fixture, grantReadChanges: true);

    [SetUp]
    public Task SeedGradeLevelDescriptors() =>
        LimitZeroCountReadScenario.SeedGradeLevelDescriptorsAsync(Harness);

    [Test]
    public Task It_returns_an_empty_page_and_the_total_count_for_limit_zero_with_total_count() =>
        LimitZeroCountReadScenario.It_returns_an_empty_page_and_the_total_count_for_limit_zero_with_total_count(
            Harness
        );

    [Test]
    public Task It_returns_an_empty_page_without_a_total_count_header_for_limit_zero() =>
        LimitZeroCountReadScenario.It_returns_an_empty_page_without_a_total_count_header_for_limit_zero(
            Harness
        );

    [Test]
    public Task It_returns_a_zero_total_count_for_limit_zero_on_an_empty_descriptor_collection() =>
        LimitZeroCountReadScenario.It_returns_a_zero_total_count_for_limit_zero_on_an_empty_descriptor_collection(
            Harness
        );

    [Test]
    public Task It_returns_an_empty_page_without_a_total_count_header_for_limit_zero_on_an_empty_descriptor_collection() =>
        LimitZeroCountReadScenario.It_returns_an_empty_page_without_a_total_count_header_for_limit_zero_on_an_empty_descriptor_collection(
            Harness
        );

    [Test]
    public Task It_returns_one_item_and_the_total_count_for_limit_one_with_total_count() =>
        LimitZeroCountReadScenario.It_returns_one_item_and_the_total_count_for_limit_one_with_total_count(
            Harness
        );

    [Test]
    public Task It_returns_an_empty_page_and_the_total_count_for_student_deletes_with_limit_zero() =>
        LimitZeroCountReadScenario.It_returns_an_empty_page_and_the_total_count_for_student_deletes_with_limit_zero(
            Harness
        );

    [Test]
    public Task It_returns_an_empty_page_without_a_total_count_header_for_student_deletes_with_limit_zero() =>
        LimitZeroCountReadScenario.It_returns_an_empty_page_without_a_total_count_header_for_student_deletes_with_limit_zero(
            Harness
        );

    [Test]
    public Task It_returns_the_total_count_for_student_deletes_with_limit_zero_in_a_change_version_window_that_excludes_a_change() =>
        LimitZeroCountReadScenario.It_returns_the_total_count_for_student_deletes_with_limit_zero_in_a_change_version_window_that_excludes_a_change(
            Harness
        );

    [Test]
    public Task It_returns_an_empty_page_and_the_total_count_for_student_key_changes_with_limit_zero() =>
        LimitZeroCountReadScenario.It_returns_an_empty_page_and_the_total_count_for_student_key_changes_with_limit_zero(
            Harness
        );

    [Test]
    public Task It_returns_an_empty_page_without_a_total_count_header_for_student_key_changes_with_limit_zero() =>
        LimitZeroCountReadScenario.It_returns_an_empty_page_without_a_total_count_header_for_student_key_changes_with_limit_zero(
            Harness
        );

    [Test]
    public Task It_returns_the_total_count_for_student_key_changes_with_limit_zero_in_a_change_version_window_that_excludes_a_change() =>
        LimitZeroCountReadScenario.It_returns_the_total_count_for_student_key_changes_with_limit_zero_in_a_change_version_window_that_excludes_a_change(
            Harness
        );

    [Test]
    public Task It_returns_a_zero_total_count_for_limit_zero_on_a_resource_with_no_tracked_deletes() =>
        LimitZeroCountReadScenario.It_returns_a_zero_total_count_for_limit_zero_on_a_resource_with_no_tracked_deletes(
            Harness
        );

    [Test]
    public Task It_returns_the_total_count_for_descriptor_deletes_with_limit_zero() =>
        LimitZeroCountReadScenario.It_returns_the_total_count_for_descriptor_deletes_with_limit_zero(Harness);
}
