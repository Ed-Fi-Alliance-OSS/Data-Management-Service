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

public sealed class Given_Postgresql_DerivativeTrackedChanges_CustomViewIncludingDeletes
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CompactDescriptorResources;
    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (resource, action) =>
                action == "ReadChanges"
                && resource.ProjectName == "Ed-Fi"
                && resource.ResourceName
                    is "SchoolTypeDescriptor"
                        or "CompactDescriptorItem"
                        or "CompactDescriptorBridge"
                    ? [CompactDescriptorHistoryScenario.IncludingDeletesStrategy]
                    : [AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired],
            grantReadChanges: true
        );

    [Test]
    public Task It_authorizes_descriptor_history_tombstone_probes_by_qualified_document_membership() =>
        CompactDescriptorHistoryScenario.It_authorizes_descriptor_history_tombstone_probes_by_qualified_document_membership(
            Harness
        );
}
