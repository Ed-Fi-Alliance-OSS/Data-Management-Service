// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture("pgsql", Category = "PostgresqlIntegration")]
[TestFixture("mssql", Category = "MssqlIntegration")]
[Category("DatabaseIntegration")]
public class Given_Managed_Database_Provisioning_With_A_Real_Provider(string dialect)
{
    private string _databaseName = null!;
    private string _connection = null!;
    private string _root = null!;
    private IDatabaseProvisioner _provider = null!;
    private CdcTargetIdentity _target = null!;

    [SetUp]
    public void SetUp()
    {
        if (dialect == "mssql" && !MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("MssqlAdmin connection string is not configured.");
        }
        _databaseName = "receipt_" + Guid.NewGuid().ToString("N");
        _connection =
            dialect == "pgsql"
                ? PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
                : MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        _provider =
            dialect == "pgsql"
                ? new PgsqlDatabaseProvisioner(NullLogger.Instance)
                : new MssqlDatabaseProvisioner(NullLogger.Instance);
        _root = Path.Combine(Path.GetTempPath(), "creation-receipt-" + Guid.NewGuid().ToString("N"));
        _target = new(
            "local",
            "default",
            "42",
            "datastore-42",
            1,
            dialect == "pgsql" ? CdcProvider.Postgresql : CdcProvider.SqlServer
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (dialect == "pgsql")
        {
            PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
        else
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private (int ExitCode, string Output, string Error) RunManaged() =>
        CliTestHelper.RunCli(
            "ddl",
            "provision",
            "--schema",
            CliTestHelper.GetMinimalSchemaPath(),
            "--connection-string",
            _connection,
            "--dialect",
            dialect,
            "--create-database",
            "--managed-state-path",
            _root,
            "--data-store-id",
            "42",
            "--instance-key",
            "datastore-42"
        );

    [TestCase(false, "Created")]
    [TestCase(true, "Reused")]
    public async Task It_reports_actual_creation_and_associates_the_live_source(
        bool precreate,
        string outcome
    )
    {
        if (precreate)
        {
            _provider.CreateDatabaseIfNotExists(_connection).Should().BeTrue();
        }
        var result = RunManaged();
        result.ExitCode.Should().Be(0, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        json.RootElement.GetProperty("creationReceipt")
            .GetProperty("outcome")
            .GetString()
            .Should()
            .Be(outcome);
        json.RootElement.GetProperty("physicalSourceFingerprint").GetString().Should().StartWith("sha256:");
        var store = new LocalCdcWorkflowJournalStore(_root);
        await using (
            var session = await store.AcquireAsync(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                default
            )
        )
        {
            var journal = await session.ReadAsync(_target, default);
            journal.Operations.Should().HaveCount(2);
        }
        var retry = RunManaged();
        retry.ExitCode.Should().Be(0, retry.Error);
        retry.Output.Should().Be(result.Output);
        (result.Output + result.Error).Should().NotContain(_databaseName).And.NotContain("EdFi_Dms1!");
        foreach (string path in Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories))
        {
            (await File.ReadAllTextAsync(path))
                .Should()
                .NotContain(_databaseName)
                .And.NotContain("EdFi_Dms1!");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_creation_evidence_across_failed_ddl_or_lost_create_response(
        bool loseReceipt
    )
    {
        var store = new LocalCdcWorkflowJournalStore(_root);
        var controller = new CdcManagedDatabaseProvisioning(store);
        var adapter = new InterruptedProvisioner(_provider, _connection, loseReceipt);
        await FluentActions
            .Awaiting(() => controller.ProvisionAsync(_target, adapter))
            .Should()
            .ThrowAsync<Exception>();
        // Both interruptions happened after a real committed CREATE; this lookup may report reuse
        // but cannot supply the missing receipt or promote another operation's database to owned.
        _provider.CreateDatabaseIfNotExists(_connection).Should().BeFalse();
        await using (
            var session = await store.AcquireAsync(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                default
            )
        )
        {
            var journal = await session.ReadAsync(_target, default);
            journal.Operations.Should().ContainSingle();
            journal.Operations[0].Completions.Length.Should().Be(loseReceipt ? 0 : 1);
            if (!loseReceipt)
            {
                ((CdcWorkflowCompletion.Database)journal.Operations[0].Completions[0].Evidence)
                    .Receipt.Outcome.Should()
                    .Be(CdcDatabaseCreationOutcome.Created);
            }
        }
        var retry = RunManaged();
        retry.ExitCode.Should().Be(1);
        retry.Error.Should().Contain("reprovision");
        retry.Output.Should().BeEmpty();
    }

    private sealed class InterruptedProvisioner(
        IDatabaseProvisioner provider,
        string connection,
        bool loseReceipt
    ) : ICdcManagedDatabaseProvisioner
    {
        public bool CreateDatabase()
        {
            bool created = provider.CreateDatabaseIfNotExists(connection);
            if (loseReceipt)
            {
                throw new IOException("simulated lost CREATE result");
            }
            return created;
        }

        public void ProvisionSchema(bool databaseWasCreated) =>
            provider.ExecuteInTransaction(connection, "INVALID DDL FOR RECEIPT TEST");

        public void ValidateSchema() =>
            throw new AssertionException("Interrupted provisioning must not validate a retry");

        public Task<string> ReadSourceFingerprintAsync(CancellationToken cancellationToken) =>
            throw new AssertionException("DDL failure must prevent source association");
    }
}
