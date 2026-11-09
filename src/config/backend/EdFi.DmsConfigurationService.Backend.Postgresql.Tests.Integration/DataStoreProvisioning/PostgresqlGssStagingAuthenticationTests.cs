// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using FakePsql = EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql.FakePsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// GSSAPI authentication of the administrative login is unsupported, and <c>pg_hba.conf</c> is matched per database.
/// Against a live server whose rules authenticate the login named by <see cref="AdminEnvironmentVariableName"/> to
/// <c>postgres</c> by another method and to every other database through GSSAPI (with this process holding a Kerberos
/// ticket, so Npgsql succeeds), readiness passes because it sees only <c>postgres</c>, and the lock and the staging
/// database are created there; the replay is then refused before <c>psql</c> starts, with a fixed diagnostic the
/// orchestrator reports as a permanent <c>RestoreFailed</c>, and the staging database can still be dropped. Outside CI:
/// it needs a KDC, and it is skipped when the variable is unset.
/// </summary>
[Category(KerberosCategory)]
public class PostgresqlGssStagingAuthenticationTests
{
    public const string KerberosCategory = "DmsProvisioningKerberos";

    /// <summary>A connection string for a superuser login authenticated to <c>postgres</c> without GSSAPI and to other databases through it.</summary>
    public const string AdminEnvironmentVariableName = "DMS_PROVISIONING_GSS_ADMIN_CONNECTION";

    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    private string _root = string.Empty;
    private FakePsql? _fake;
    private PostgresqlAdminConnection _admin = null!;
    private DmsOperationId _operation;
    private IDmsTargetLockHolder? _holder;
    private bool _maintenanceGss;
    private bool _stagingGss;
    private IReadOnlyList<DmsReadinessReason> _readinessReasons = [];
    private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
    private bool _psqlStarted;
    private DmsCommandOutcome _dropped = null!;
    private long _stagingDatabasesLeft = -1;

    private static DmsTargetDatabaseName TargetName() =>
        DmsTargetDatabaseName.TryCreate(
            $"dms1438_gss_{DmsOperationId.New().Value}",
            _protectedNames,
            out DmsTargetDatabaseName name
        ) == DmsTargetNameValidation.Valid
            ? name
            : throw new InvalidOperationException("The target name does not satisfy the rules.");

    private async Task<bool> IsGssAuthenticatedAsync(string database)
    {
        await using NpgsqlConnection connection = await _admin.OpenAsync(database, "dms1438-test", default);
        return await PostgresqlEnvironment.IsGssAuthenticatedAsync(connection, default);
    }

    [OneTimeSetUp]
    public async Task Setup()
    {
        string? connectionString = Environment.GetEnvironmentVariable(AdminEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore($"{AdminEnvironmentVariableName} is not set; the Kerberos lane is skipped.");
        }

        _admin = new PostgresqlAdminConnection(connectionString, TimeSpan.FromSeconds(30));
        _operation = DmsOperationId.New();
        _root = Path.Combine(Path.GetTempPath(), "dms1438-gss-" + _operation.Value);
        Directory.CreateDirectory(_root);
        PrivateOperationDirectory directory = PrivateOperationDirectory.Create(_root, _operation);
        string artifactPath = directory.PathOf("artifact.sql");
        byte[] artifact = "SELECT 1;\n"u8.ToArray();
        await File.WriteAllBytesAsync(artifactPath, artifact);

        string started = Path.Combine(_root, "psql-ran");
        _fake = FakePsql.ExitingSuccessfully(started);
        PostgresqlDmsTargetEngine engine = new(
            _admin,
            DmsProvisioningTestServer.Settings(),
            TimeProvider.System,
            NullLogger.Instance,
            new PsqlRestoreRunner(
                _fake.ExecutablePath,
                TimeSpan.FromMinutes(1),
                TimeProvider.System,
                NullLogger.Instance
            )
        );

        _maintenanceGss = await IsGssAuthenticatedAsync(PostgresqlAdminConnection.MaintenanceDatabaseName);
        _readinessReasons = await engine.ValidateEnvironmentAsync(default) switch
        {
            DmsEngineEnvironment.NotReady notReady => notReady.Reasons,
            _ => [],
        };

        _holder = await engine.AcquireTargetLockAsync(TargetName(), _operation, default)
            is DmsTargetLockResult.Acquired acquired
            ? acquired.Holder
            : throw new InvalidOperationException("The target lock was not acquired.");
        DmsDatabaseName stage = _operation.StagingDatabaseName;
        DmsInstanceIdentity instance = (
            (DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)
                await engine.CreateStagingAsync(
                    _holder,
                    stage,
                    new DmsProvisionClaim(_operation, Guid.NewGuid()),
                    default
                )
        ).Value;
        _stagingGss = await IsGssAuthenticatedAsync(stage.Value);

        _outcome = await engine.RestoreIntoStagingAsync(
            stage,
            TargetName(),
            new StagedArtifact(
                artifactPath,
                Convert.ToHexStringLower(SHA256.HashData(artifact)),
                DmsArtifactKind.PostgresqlSqlDump
            ),
            _operation,
            default
        );
        _psqlStarted = File.Exists(started);

        _dropped = await engine.DropStagingAsync(stage, instance, default);
        await using NpgsqlConnection maintenance = await _admin.OpenMaintenanceAsync(default);
        await using NpgsqlCommand count = new(
            "SELECT count(*) FROM pg_catalog.pg_database WHERE datname = @name;",
            maintenance
        );
        count.Parameters.AddWithValue("name", stage.Value);
        _stagingDatabasesLeft = (long)(await count.ExecuteScalarAsync())!;
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_holder is not null)
        {
            await _holder.DisposeAsync();
            await using NpgsqlConnection maintenance = await _admin.OpenMaintenanceAsync(default);
            await using NpgsqlCommand drop = new(
                $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(_operation.StagingDatabaseName.Value)} WITH (FORCE);",
                maintenance
            );
            await drop.ExecuteNonQueryAsync();
        }

        if (_fake is not null)
        {
            await _fake.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void It_runs_where_the_two_databases_authenticate_the_login_differently() =>
        (_maintenanceGss, _stagingGss).Should().Be((false, true));

    [Test]
    public void It_passes_readiness_which_sees_only_the_maintenance_database() =>
        _readinessReasons
            .Select(reason => reason.Probe)
            .Should()
            .NotContain([
                PostgresqlEnvironment.GssAuthenticationProbe,
                PostgresqlEnvironment.TargetServerProbe,
            ]);

    [Test]
    public void It_refuses_the_replay_with_a_fixed_diagnostic() =>
        _outcome
            .Should()
            .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
            .Which.Failure.Should()
            .Be(
                new DmsCommandFailure(
                    DmsCommandFailureClass.Rejected,
                    DmsProvisioningDiagnostic.ForOperation(
                        PostgresqlDmsTargetEngine.RestoreGssAuthenticationStep
                    )
                )
            );

    [Test]
    public void It_never_starts_psql() => _psqlStarted.Should().BeFalse();

    [Test]
    public void It_leaves_the_staging_database_for_cleanup_to_drop()
    {
        _dropped.Should().BeOfType<DmsCommandOutcome.Succeeded>();
        _stagingDatabasesLeft.Should().Be(0);
    }
}
