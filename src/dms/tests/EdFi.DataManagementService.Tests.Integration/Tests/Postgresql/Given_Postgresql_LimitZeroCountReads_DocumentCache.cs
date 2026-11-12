// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// Descriptor collection reads with <c>limit=0</c> when document cache read acceleration is enabled,
/// with and without <c>totalCount</c>.
/// </summary>
public sealed class Given_Postgresql_LimitZeroCountReads_DocumentCache : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool EnableDocumentCacheReadAcceleration => true;

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
}
