// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Verifies that a real SQL Server database provisioned with generated DDL exposes a
/// non-trivial <see cref="DatabaseFingerprint.EffectiveSchemaHash"/> via
/// <see cref="MssqlDatabaseFingerprintReader"/>.
///
/// This hash is consumed at startup by <c>ValidateDatabaseFingerprintMiddleware</c>
/// (described in <c>docs/new-startup-flow.md</c>) to detect schema / code mismatches
/// before the service begins serving traffic.
/// </summary>
[TestFixture]
[Category("CompatibilityGate")]
[NonParallelizable]
public class Given_A_Mssql_Database_Provisioned_With_Generated_DDL_For_EffectiveSchemaHash_Mismatch_Detection
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private MssqlGeneratedDdlTestDatabase? _database;
    private DatabaseFingerprint? _fingerprint;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var fixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            FixtureRelativePath
        );

        var effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(fixtureDirectory);
        var (_, combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, SqlDialect.Mssql);

        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(combinedSql);

        var reader = new MssqlDatabaseFingerprintReader(
            NullLogger<MssqlDatabaseFingerprintReader>.Instance
        );

        _fingerprint = await reader.ReadFingerprintAsync(_database.ConnectionString);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null;
        }
    }

    [Test]
    public void It_reads_a_non_empty_EffectiveSchemaHash_from_the_provisioned_database()
    {
        _fingerprint.Should().NotBeNull();
        _fingerprint!.EffectiveSchemaHash.Should().NotBeNullOrEmpty();
        _fingerprint.EffectiveSchemaHash.Should().NotBe("NOT_A_REAL_HASH");
    }
}
