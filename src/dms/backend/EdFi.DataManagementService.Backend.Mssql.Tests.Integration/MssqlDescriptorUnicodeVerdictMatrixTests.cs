// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Descriptor_Unicode_Verdict_Matrix : DescriptorUnicodeVerdictMatrixTests
{
    private MssqlGeneratedDdlTestDatabase _database = null!;

    protected override SqlDialect Dialect => SqlDialect.Mssql;

    protected override string ConnectionString => _database.ConnectionString;

    protected override DbConnection CreateConnection() => new SqlConnection(_database.ConnectionString);

    private protected override INaturalKeyLookupCommandBuilder CreateLookupBuilder() =>
        new MssqlNaturalKeyLookupCommandBuilder();

    private protected override IRelationalParameterConfigurator CreateParameterConfigurator() =>
        new MssqlRelationalParameterConfigurator();

    protected override ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddTestReadableProfileProjector();
        services.AddMssqlBackendIntegrationTestServices();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    protected override async Task<(IAsyncDisposable Database, MappingSet MappingSet)> ProvisionAsync(
        string fixtureRelativePath
    )
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "The descriptor Unicode verdict matrix requires a reachable SQL Server admin connection."
        );
        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            fixtureRelativePath,
            strict: true
        );
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
        return (_database, fixture.MappingSet);
    }
}
