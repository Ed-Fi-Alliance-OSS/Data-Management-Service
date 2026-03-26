// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Verifies that the Phase 0 preflight SQL in the generated DDL fails fast when an attempt
/// is made to provision a PostgreSQL database with a different <c>EffectiveSchemaHash</c>
/// than the one already stored.
///
/// This test exercises the CLI provisioning contract: once a database is provisioned for
/// hash A, applying DDL for hash B must raise an error immediately (before any schema
/// mutations occur), preventing silent in-place upgrades.
///
/// The preflight is emitted by <see cref="SeedDmlEmitter.EmitPreflightOnly"/> and wired
/// into the full DDL script by <see cref="FullDdlEmitter.Emit"/>. The equivalent runtime
/// check is performed by <c>ValidateDatabaseFingerprintMiddleware</c> (see
/// <c>docs/new-startup-flow.md</c>).
/// </summary>
[TestFixture]
[Category("CompatibilityGate")]
[NonParallelizable]
public class Given_A_Postgresql_Database_Provisioned_With_Hash_A_When_Applying_DDL_For_Hash_B
{
    private const string FixtureARelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private const string FixtureBRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/ext";

    private PostgresqlGeneratedDdlTestDatabase? _database;
    private string _fixtureBDdl = null!;
    private Exception? _caughtException;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixtureBDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            FixtureBRelativePath
        );

        var fixtureADirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            FixtureARelativePath
        );

        var effectiveSchemaSetA = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(fixtureADirectory);
        var (_, fixtureADdl) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSetA, SqlDialect.Pgsql);

        var effectiveSchemaSetB = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(fixtureBDirectory);
        var (_, fixtureBDdl) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSetB, SqlDialect.Pgsql);
        _fixtureBDdl = fixtureBDdl;

        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixtureADdl);

        try
        {
            await _database.ApplyGeneratedDdlAsync(_fixtureBDdl);
        }
        catch (Exception ex)
        {
            _caughtException = ex;
        }
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
    public void It_throws_an_exception_when_applying_DDL_for_a_different_hash()
    {
        _caughtException.Should().NotBeNull();
    }

    [Test]
    public void It_throws_an_exception_with_a_hash_mismatch_message()
    {
        _caughtException.Should().NotBeNull();
        _caughtException!.Message.Should().Contain("EffectiveSchemaHash mismatch");
    }
}
