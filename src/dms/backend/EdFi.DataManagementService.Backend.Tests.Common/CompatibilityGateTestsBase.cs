// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Abstract base class for compatibility gate integration tests that verify the
/// <see cref="ResourceKeyValidator"/> detects database tampering via the fast-path
/// fingerprint check and slow-path row diff. Subclasses supply dialect-specific SQL
/// identifiers, a fixture path, and database lifecycle operations.
///
/// This base class is exercised by both the PostgreSQL and MSSQL integration test
/// suites and mirrors the production startup flow described in new-startup-flow.md §6
/// (ValidateResourceKeySeedMiddleware, ResourceKeyValidationCacheProvider,
/// DatabaseFingerprintProvider).
/// </summary>
[TestFixture]
[Category("CompatibilityGate")]
[NonParallelizable]
public abstract class CompatibilityGateTestsBase
{
    // -------------------------------------------------------------------------
    // Abstract SQL identifiers — dialect-specific quoting supplied by subclass
    // -------------------------------------------------------------------------

    /// <summary>Fully-qualified, dialect-quoted ResourceKey table, e.g. dms."ResourceKey" or [dms].[ResourceKey].</summary>
    protected abstract string ResourceKeyTable { get; }

    /// <summary>Dialect-quoted ResourceKeyId column, e.g. "ResourceKeyId" or [ResourceKeyId].</summary>
    protected abstract string ResourceKeyIdColumn { get; }

    /// <summary>Dialect-quoted ResourceName column.</summary>
    protected abstract string ResourceNameColumn { get; }

    /// <summary>Dialect-quoted ProjectName column.</summary>
    protected abstract string ProjectNameColumn { get; }

    /// <summary>Dialect-quoted ResourceVersion column.</summary>
    protected abstract string ResourceVersionColumn { get; }

    // -------------------------------------------------------------------------
    // Abstract fixture configuration
    // -------------------------------------------------------------------------

    /// <summary>Path to the fixture directory, relative to the repository root.</summary>
    protected abstract string FixtureRelativePath { get; }

    // -------------------------------------------------------------------------
    // Abstract dialect-specific operations
    // -------------------------------------------------------------------------

    /// <summary>Creates the dialect-specific <see cref="IResourceKeyRowReader"/> implementation.</summary>
    protected abstract IResourceKeyRowReader CreateResourceKeyRowReader();

    /// <summary>Creates the dialect-specific <see cref="IDatabaseFingerprintReader"/> implementation.</summary>
    protected abstract IDatabaseFingerprintReader CreateDatabaseFingerprintReader();

    /// <summary>Executes an arbitrary SQL statement against the test database (used for tamper operations).</summary>
    protected abstract Task ExecuteTamperAsync(string sql);

    /// <summary>Returns the connection string for the provisioned test database.</summary>
    protected abstract string GetConnectionString();

    /// <summary>
    /// Truncates the ResourceKey table and re-inserts the supplied rows in id order,
    /// restoring the table to the pre-test state.
    /// </summary>
    protected abstract Task RestoreResourceKeyRowsAsync(IReadOnlyList<ResourceKeyRow> rows);

    /// <summary>Provisions the test database by executing the supplied DDL SQL.</summary>
    protected abstract Task ProvisionDatabaseAsync(string ddl);

    /// <summary>Tears down the test database after the fixture completes.</summary>
    protected abstract Task DisposeDatabaseAsync();

    /// <summary>
    /// Called at the start of <see cref="OneTimeSetUp"/> before any provisioning.
    /// Subclasses can override to skip the fixture when required infrastructure is
    /// unavailable (e.g., call <c>Assert.Ignore</c> or <c>Assume.That</c>).
    /// </summary>
    protected virtual void GuardAgainstMissingInfrastructure() { }

    // -------------------------------------------------------------------------
    // Protected shared state populated by [OneTimeSetUp]
    // -------------------------------------------------------------------------

    protected EffectiveSchemaSet _effectiveSchemaSet = null!;
    protected IReadOnlyList<ResourceKeyRow> _originalRows = null!;
    protected DatabaseFingerprint _databaseFingerprint = null!;

    // -------------------------------------------------------------------------
    // Fixture setup / teardown
    // -------------------------------------------------------------------------

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        GuardAgainstMissingInfrastructure();

        var solutionRoot = GoldenFixtureTestHelpers.FindSolutionRoot(
            AppContext.BaseDirectory
        );

        var repositoryRoot = Path.GetFullPath(Path.Combine(solutionRoot, "..", ".."));
        var fixtureDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, FixtureRelativePath));

        _effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(
            fixtureDirectory,
            repositoryRoot
        );

        var dialect = GetSqlDialect();
        var (_, combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(_effectiveSchemaSet, dialect);

        await ProvisionDatabaseAsync(combinedSql);

        var fingerprintReader = CreateDatabaseFingerprintReader();
        _databaseFingerprint =
            await fingerprintReader.ReadFingerprintAsync(GetConnectionString())
            ?? throw new InvalidOperationException(
                "Database fingerprint could not be read after provisioning. "
                + "Ensure ProvisionDatabaseAsync wrote the dms.EffectiveSchema singleton row."
            );

        var rowReader = CreateResourceKeyRowReader();
        _originalRows = await rowReader.ReadResourceKeyRowsAsync(GetConnectionString());
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await DisposeDatabaseAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await RestoreResourceKeyRowsAsync(_originalRows);
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Test]
    public async Task It_returns_success_when_fingerprint_matches()
    {
        var validator = new ResourceKeyValidator(
            CreateResourceKeyRowReader(),
            NullLogger<ResourceKeyValidator>.Instance
        );

        var effectiveSchema = _effectiveSchemaSet.EffectiveSchema;
        var expectedRows = effectiveSchema.ResourceKeysInIdOrder.ToResourceKeyRows();

        var result = await validator.ValidateAsync(
            _databaseFingerprint,
            effectiveSchema.ResourceKeyCount,
            [.. effectiveSchema.ResourceKeySeedHash],
            expectedRows,
            GetConnectionString()
        );

        result.Should().BeOfType<ResourceKeyValidationResult.ValidationSuccess>();
    }

    [Test]
    public async Task It_returns_failure_when_extra_row_inserted()
    {
        // Determine MAX(ResourceKeyId) + 1 as the tampered id
        var maxId = _originalRows.Max(r => r.ResourceKeyId);
        short tamperedId = (short)(maxId + 1);

        await ExecuteTamperAsync(
            $"INSERT INTO {ResourceKeyTable} "
                + $"({ResourceKeyIdColumn}, {ProjectNameColumn}, {ResourceNameColumn}, {ResourceVersionColumn}) "
                + $"VALUES ({tamperedId}, 'TAMPERED', 'TAMPERED', '1.0.0')"
        );

        // Construct a fingerprint with mismatched count to bypass the fast-path and force the slow path.
        var mismatchedFingerprint = new DatabaseFingerprint(
            _databaseFingerprint.ApiSchemaFormatVersion,
            _databaseFingerprint.EffectiveSchemaHash,
            (short)(_databaseFingerprint.ResourceKeyCount + 1),
            ImmutableArray<byte>.Empty
        );

        var validator = new ResourceKeyValidator(
            CreateResourceKeyRowReader(),
            NullLogger<ResourceKeyValidator>.Instance
        );

        var effectiveSchema = _effectiveSchemaSet.EffectiveSchema;
        var expectedRows = effectiveSchema.ResourceKeysInIdOrder.ToResourceKeyRows();

        var result = await validator.ValidateAsync(
            mismatchedFingerprint,
            effectiveSchema.ResourceKeyCount,
            [.. effectiveSchema.ResourceKeySeedHash],
            expectedRows,
            GetConnectionString()
        );

        var failure = result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
        failure.DiffReport.Should().Contain("TAMPERED");
    }

    [Test]
    public async Task It_returns_failure_when_last_row_deleted()
    {
        var lastRow = _originalRows[_originalRows.Count - 1];

        await ExecuteTamperAsync(
            $"DELETE FROM {ResourceKeyTable} WHERE {ResourceKeyIdColumn} = {lastRow.ResourceKeyId}"
        );

        // Construct a fingerprint with count - 1 to force slow-path diff.
        var mismatchedFingerprint = new DatabaseFingerprint(
            _databaseFingerprint.ApiSchemaFormatVersion,
            _databaseFingerprint.EffectiveSchemaHash,
            (short)(_databaseFingerprint.ResourceKeyCount - 1),
            ImmutableArray<byte>.Empty
        );

        var validator = new ResourceKeyValidator(
            CreateResourceKeyRowReader(),
            NullLogger<ResourceKeyValidator>.Instance
        );

        var effectiveSchema = _effectiveSchemaSet.EffectiveSchema;
        var expectedRows = effectiveSchema.ResourceKeysInIdOrder.ToResourceKeyRows();

        var result = await validator.ValidateAsync(
            mismatchedFingerprint,
            effectiveSchema.ResourceKeyCount,
            [.. effectiveSchema.ResourceKeySeedHash],
            expectedRows,
            GetConnectionString()
        );

        var failure = result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
        failure.DiffReport.Should().Contain(lastRow.ResourceName);
    }

    [Test]
    public async Task It_returns_failure_when_first_row_resource_name_modified()
    {
        var firstRow = _originalRows[0];

        await ExecuteTamperAsync(
            $"UPDATE {ResourceKeyTable} "
                + $"SET {ResourceNameColumn} = 'TAMPERED' "
                + $"WHERE {ResourceKeyIdColumn} = {firstRow.ResourceKeyId}"
        );

        // Same count but zeroed hash to force slow-path diff.
        var mismatchedFingerprint = new DatabaseFingerprint(
            _databaseFingerprint.ApiSchemaFormatVersion,
            _databaseFingerprint.EffectiveSchemaHash,
            _databaseFingerprint.ResourceKeyCount,
            ImmutableArray<byte>.Empty
        );

        var validator = new ResourceKeyValidator(
            CreateResourceKeyRowReader(),
            NullLogger<ResourceKeyValidator>.Instance
        );

        var effectiveSchema = _effectiveSchemaSet.EffectiveSchema;
        var expectedRows = effectiveSchema.ResourceKeysInIdOrder.ToResourceKeyRows();

        var result = await validator.ValidateAsync(
            mismatchedFingerprint,
            effectiveSchema.ResourceKeyCount,
            [.. effectiveSchema.ResourceKeySeedHash],
            expectedRows,
            GetConnectionString()
        );

        var failure = result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
        failure.DiffReport.Should().Contain("TAMPERED");
    }

    // -------------------------------------------------------------------------
    // Abstract helper
    // -------------------------------------------------------------------------

    /// <summary>Returns the <see cref="SqlDialect"/> for this subclass, used to build DDL.</summary>
    protected abstract SqlDialect GetSqlDialect();
}
