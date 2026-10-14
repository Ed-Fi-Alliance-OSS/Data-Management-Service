// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Fresh_Postgresql_CompactDescriptor_Schema : CompactDescriptorSchemaTests
{
    private PostgresqlGeneratedDdlTestDatabase _database = default!;
    protected override string Dialect => "pgsql";

    protected override DbConnection CreateConnection() => new NpgsqlConnection(_database.ConnectionString);

    [TestCaseSource(nameof(DescriptorAliasMutations), new object[] { "pgsql" })]
    public Task It_rejects_invalid_descriptor_aliases_even_with_the_same_v3_fingerprint(string mutation) =>
        AssertDescriptorAliasMutationAsync(mutation);

    protected override async Task<IAsyncDisposable> ProvisionAsync()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            CompactDescriptorCatalogAssertions.FixtureRelativePath
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
        return _database;
    }
}
