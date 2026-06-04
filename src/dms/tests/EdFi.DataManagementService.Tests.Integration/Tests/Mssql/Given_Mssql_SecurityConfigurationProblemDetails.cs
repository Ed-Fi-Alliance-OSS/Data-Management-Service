// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

[Category("SecurityConfiguration")]
public sealed class Given_Mssql_SecurityConfigurationProblemDetails_For_Missing_Metadata
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        SecurityConfigurationProblemDetailsScenario.CreateEmptyClaimSetCatalogProvider();

    [Test]
    public Task It_returns_missing_metadata_problem_details_for_resource_read() =>
        SecurityConfigurationProblemDetailsScenario.It_returns_missing_metadata_problem_details_for_resource_read(
            Harness
        );
}

[Category("SecurityConfiguration")]
public sealed class Given_Mssql_SecurityConfigurationProblemDetails_For_No_Strategies_Write
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        SecurityConfigurationProblemDetailsScenario.CreateNoStrategiesWriteClaimSetProvider(fixture);

    [Test]
    public Task It_returns_no_strategies_problem_details_for_resource_write() =>
        SecurityConfigurationProblemDetailsScenario.It_returns_no_strategies_problem_details_for_resource_write(
            Harness
        );
}

[Category("SecurityConfiguration")]
public sealed class Given_Mssql_SecurityConfigurationProblemDetails_For_Unknown_Resource_Strategy
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        SecurityConfigurationProblemDetailsScenario.CreateUnknownStrategyResourceReadClaimSetProvider(
            fixture
        );

    [Test]
    public Task It_returns_unknown_strategy_problem_details_for_resource_read() =>
        SecurityConfigurationProblemDetailsScenario.It_returns_unknown_strategy_problem_details_for_resource_read(
            Harness
        );
}

[Category("SecurityConfiguration")]
public sealed class Given_Mssql_SecurityConfigurationProblemDetails_For_Descriptor_Strategy
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        SecurityConfigurationProblemDetailsScenario.CreateUnknownStrategyDescriptorReadClaimSetProvider(
            fixture
        );

    [Test]
    public Task It_returns_unknown_strategy_problem_details_for_descriptor_read() =>
        SecurityConfigurationProblemDetailsScenario.It_returns_unknown_strategy_problem_details_for_descriptor_read(
            Harness
        );
}

[Category("SecurityConfiguration")]
public sealed class Given_Mssql_SecurityConfigurationProblemDetails_For_No_Matching_Action
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        SecurityConfigurationProblemDetailsScenario.CreateCreateOnlyRootChildClaimSetProvider();

    [Test]
    public Task It_keeps_no_matching_resource_action_claim_as_forbidden() =>
        SecurityConfigurationProblemDetailsScenario.It_keeps_no_matching_resource_action_claim_as_forbidden(
            Harness
        );
}
