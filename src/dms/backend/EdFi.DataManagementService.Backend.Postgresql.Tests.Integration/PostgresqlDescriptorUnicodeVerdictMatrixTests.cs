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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Descriptor_Unicode_Verdict_Matrix : DescriptorUnicodeVerdictMatrixTests
{
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    protected override SqlDialect Dialect => SqlDialect.Pgsql;

    protected override string ConnectionString => _database.ConnectionString;

    protected override DbConnection CreateConnection() => new NpgsqlConnection(_database.ConnectionString);

    private protected override INaturalKeyLookupCommandBuilder CreateLookupBuilder() =>
        new PostgresqlNaturalKeyLookupCommandBuilder();

    private protected override IRelationalParameterConfigurator CreateParameterConfigurator() =>
        DefaultRelationalParameterConfigurator.Instance;

    protected override ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.AddTestReadableProfileProjector();
        services.AddPostgresqlBackendIntegrationTestServices();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    protected override async Task<(IAsyncDisposable Database, MappingSet MappingSet)> ProvisionAsync(
        string fixtureRelativePath
    )
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            fixtureRelativePath,
            strict: true
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
        return (_database, fixture.MappingSet);
    }
}
