// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// The projection on PostgreSQL against a primary that publishes a snapshot and a read replica, each
/// its own leased database.
/// </summary>
public sealed class Given_Postgresql_EducationOrganizationProjectionDerivatives
    : PostgresqlEducationOrganizationProjectionTestBase
{
    protected override IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives =>
        [DataStoreDerivativeType.Snapshot, DataStoreDerivativeType.ReadReplica];

    protected override IReadOnlyDictionary<DataStoreDerivativeType, string> PrimaryDerivatives =>
        LeasedDerivatives.ToDictionary(derivative => derivative, DerivativeConnectionString);

    [Test]
    public Task It_reads_the_primary_when_the_store_publishes_derivatives() =>
        EducationOrganizationProjectionScenario.It_reads_the_primary_when_the_store_publishes_derivatives(
            Context,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot),
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica)
        );
}
