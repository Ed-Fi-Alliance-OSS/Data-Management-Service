// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Proves that a model SQL Server cannot support with native cascades (a self-referencing
/// identity-mutable resource) fails at derivation — before any DDL text exists to execute —
/// and that the same fixture model derives cleanly for PostgreSQL with its reference FK
/// shape and cascade action unchanged.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Ddl_Pipeline_With_An_Unsafe_Cascade_Cycle_Fixture
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/unsafe-cascade-cycle";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }
    }

    [Test]
    public async Task It_should_fail_at_derivation_before_any_ddl_reaches_the_database()
    {
        await using var database = await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();

        // The provisioning-shaped flow: derive + emit first, then apply to the target
        // database. Derivation must throw, leaving no DDL text to apply.
        var provision = async () =>
        {
            var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
            await database.ApplyGeneratedDdlAsync(fixture.GeneratedDdl);
        };

        await provision
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("SqlServerCascadeCycleNotSupported:*Looper*");

        var userTableCount = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables;");
        userTableCount.Should().Be(0, "derivation failed, so no DDL may have reached the database");
    }

    [Test]
    public void It_should_derive_the_same_fixture_cleanly_for_postgresql()
    {
        var fixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            FixtureRelativePath
        );
        var effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(fixtureDirectory);

        var (modelSet, generatedDdl) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Pgsql,
            strict: false
        );

        generatedDdl.Should().NotBeNullOrWhiteSpace();

        var selfReferenceForeignKey = modelSet
            .ConcreteResourcesInNameOrder.Single(model => model.ResourceKey.Resource.ResourceName == "Looper")
            .RelationalModel.Root.Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint =>
                constraint.Columns.Any(column => column.Value == "ParentLooper_DocumentId")
            );

        selfReferenceForeignKey
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("ParentLooper_LoopKey", "ParentLooper_DocumentId");
        selfReferenceForeignKey.OnUpdate.Should().Be(ReferentialAction.Cascade);
    }
}
