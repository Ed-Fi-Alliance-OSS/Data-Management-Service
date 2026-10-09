// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_CompactDescriptorCustomView : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CompactDescriptorResources;
    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (resource, _) =>
                resource.ResourceName.StartsWith("CompactDescriptor", StringComparison.Ordinal)
                    ? [CompactDescriptorResourceScenario.CustomViewStrategy]
                    : [AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired]
        );

    [Test]
    public Task It_authorizes_custom_view_membership_by_the_owning_descriptor_document() =>
        CompactDescriptorResourceScenario.It_authorizes_custom_view_membership_by_the_owning_descriptor_document(
            Harness
        );
}
