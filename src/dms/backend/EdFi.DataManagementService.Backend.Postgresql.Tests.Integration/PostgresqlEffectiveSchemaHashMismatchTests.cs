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

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Verifies that a real PostgreSQL database provisioned with generated DDL exposes a
/// non-trivial <see cref="DatabaseFingerprint.EffectiveSchemaHash"/> via
/// <see cref="PostgresqlDatabaseFingerprintReader"/>.
///
/// This hash is consumed at startup by <c>ValidateDatabaseFingerprintMiddleware</c>
/// (described in <c>reference/design/backend-redesign/design-docs/new-startup-flow.md</c>) to detect schema / code mismatches
/// before the service begins serving traffic.
/// </summary>
[TestFixture]
[Category("CompatibilityGate")]
public class Given_A_Postgresql_Database_Provisioned_With_Generated_DDL_For_EffectiveSchemaHash_Mismatch_Detection
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private PostgresqlGeneratedDdlTestDatabase? _database;
    private DatabaseFingerprint? _fingerprint;
    private EffectiveSchemaSet? _effectiveSchemaSet;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            FixtureRelativePath
        );

        _effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(fixtureDirectory);
        var (_, combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(_effectiveSchemaSet, SqlDialect.Pgsql);

        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(combinedSql);

        var reader = new PostgresqlDatabaseFingerprintReader(
            NullLogger<PostgresqlDatabaseFingerprintReader>.Instance
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
        _fingerprint
            .EffectiveSchemaHash.Should()
            .Be(_effectiveSchemaSet!.EffectiveSchema.EffectiveSchemaHash);
    }
}
