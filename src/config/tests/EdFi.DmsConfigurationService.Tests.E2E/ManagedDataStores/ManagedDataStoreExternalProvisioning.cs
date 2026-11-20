// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Trust;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Tests.E2E.ManagedDataStores;

/// <summary>Operator setup for a real external host; no readiness or worker substitutions.</summary>
internal sealed class ManagedDataStoreExternalProvisioning : IAsyncDisposable
{
    private readonly bool _mssql;
    private readonly string _admin;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dms1439-http-provision-" + Guid.NewGuid().ToString("N")
    );
    private bool _loginCreated;
    public string Login { get; } = "dms1439_http_runtime_" + Guid.NewGuid().ToString("N");
    public string Password { get; } = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string RuntimeConnection { get; private set; } = "";
    public string SchemaHash { get; private set; } = "";
    public Dictionary<string, string> Configuration { get; } = [];

    private ManagedDataStoreExternalProvisioning(bool mssql, string admin)
    {
        _mssql = mssql;
        _admin = admin;
    }

    public static async Task<ManagedDataStoreExternalProvisioning> CreateAsync(bool mssql)
    {
        string? admin = Environment.GetEnvironmentVariable(
            mssql ? "ConnectionStrings__MssqlDmsAdmin" : "ConnectionStrings__PostgresqlDmsAdmin"
        );
        string? staging = Environment.GetEnvironmentVariable("DMS_PROVISIONING_MSSQL_STAGING_PATH");
        string? serverStaging = Environment.GetEnvironmentVariable(
            "DMS_PROVISIONING_MSSQL_STAGING_SERVER_PATH"
        );
        string? psql = Environment.GetEnvironmentVariable("DMS_PROVISIONING_PSQL_PATH");
        if (
            string.IsNullOrWhiteSpace(admin)
            || !RealTemplatePackage.IsConfigured
            || (
                mssql
                    ? string.IsNullOrWhiteSpace(staging) || string.IsNullOrWhiteSpace(serverStaging)
                    : !File.Exists(psql)
            )
        )
        {
            Assert.Ignore(
                "External lifecycle requires genuine packages, the provider admin, and its psql/shared staging settings."
            );
        }
        var setup = new ManagedDataStoreExternalProvisioning(mssql, admin!);
        try
        {
            Directory.CreateDirectory(setup._directory);
            string artifacts = Path.Combine(setup._directory, "artifacts");
            string operations = Path.Combine(setup._directory, "operations");
            Directory.CreateDirectory(operations);
            using var signer = new EphemeralAttestationSigner(RealTemplatePackage.ProducerName);
            string provider = mssql ? "mssql" : "postgresql";
            foreach (
                var (wire, kind) in new[]
                {
                    ("Minimal", RestoreManifest.MinimalTemplateKind),
                    ("Sample", RestoreManifest.PopulatedTemplateKind),
                }
            )
            {
                var package = RealTemplatePackage.Find(provider, kind);
                var publication = package.PublishTo(artifacts, signer);
                setup.Configuration[$"DmsDataStoreProvisioning__Templates__{wire}__PackageId"] =
                    package.PackageId;
                setup.Configuration[$"DmsDataStoreProvisioning__Templates__{wire}__PackageVersion"] =
                    package.PackageVersion;
                setup.Configuration["DmsDataStoreProvisioning__TrustPolicyPath"] =
                    publication.TrustPolicyPath;
                if (wire == "Minimal")
                {
                    var compatibility = package.Compatibility();
                    setup.SchemaHash = compatibility.EffectiveSchemaHash!;
                    setup.Configuration["DmsDataStoreProvisioning__Compatibility__DataStandardVersion"] =
                        compatibility.DataStandardVersion!;
                    setup.Configuration["DmsDataStoreProvisioning__Compatibility__EffectiveSchemaHash"] =
                        setup.SchemaHash;
                    setup.Configuration["DmsDataStoreProvisioning__Compatibility__ApiSchemaFormatVersion"] =
                        compatibility.ApiSchemaFormatVersion!;
                    setup.Configuration["DmsDataStoreProvisioning__Compatibility__RelationalMappingVersion"] =
                        compatibility.RelationalMappingVersion!;
                    for (int index = 0; index < compatibility.Projects!.Length; index++)
                    {
                        setup.Configuration[$"DmsDataStoreProvisioning__Compatibility__Projects__{index}"] =
                            compatibility.Projects[index];
                    }
                }
            }
            await using var connection = setup.Connection(admin!);
            await connection.OpenAsync();
            // All interpolated names/passwords are generated above, never caller input.
            await connection.ExecuteAsync(
                mssql
                    ? $"CREATE LOGIN [{setup.Login}] WITH PASSWORD = '{setup.Password}', CHECK_POLICY = OFF;"
                    : $"CREATE ROLE \"{setup.Login}\" LOGIN PASSWORD '{setup.Password}';"
            );
            setup._loginCreated = true;
            await connection.ExecuteAsync(
                mssql
                    ? $"GRANT CONNECT ANY DATABASE TO [{setup.Login}]; GRANT SELECT ALL USER SECURABLES TO [{setup.Login}];"
                    : $"GRANT pg_read_all_data TO \"{setup.Login}\";"
            );
            setup.RuntimeConnection = mssql
                ? new SqlConnectionStringBuilder(admin!)
                {
                    UserID = setup.Login,
                    Password = setup.Password,
                    IntegratedSecurity = false,
                    Pooling = false,
                }.ConnectionString
                : new NpgsqlConnectionStringBuilder(admin!)
                {
                    Username = setup.Login,
                    Password = setup.Password,
                    Pooling = false,
                }.ConnectionString;
            setup.Configuration["DmsDataStoreProvisioning__Enabled"] = "true";
            setup.Configuration["DmsDataStoreProvisioning__Provider"] = provider;
            setup.Configuration["DmsDataStoreProvisioning__AdminConnectionString"] = admin!;
            setup.Configuration["DmsDataStoreProvisioning__ArtifactDirectory"] = artifacts;
            setup.Configuration["DmsDataStoreProvisioning__OperationDirectory"] = operations;
            setup.Configuration["DataStoreManagement__RuntimeConnectionString"] = setup.RuntimeConnection;
            if (mssql)
            {
                setup.Configuration["DmsDataStoreProvisioning__Mssql__StagingPath"] = staging!;
                setup.Configuration["DmsDataStoreProvisioning__Mssql__StagingServerPath"] = serverStaging!;
            }
            else
            {
                setup.Configuration["DmsDataStoreProvisioning__Postgresql__PsqlPath"] = psql!;
            }
            return setup;
        }
        catch
        {
            await setup.DisposeAsync();
            throw;
        }
    }

    public DbConnection Connection(string value) =>
        _mssql ? new SqlConnection(value) : new NpgsqlConnection(value);

    public async Task<bool> TargetExistsAsync(string name)
    {
        await using var connection = Connection(_admin);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(
                _mssql
                    ? "SELECT COUNT(*) FROM sys.databases WHERE name = @Name;"
                    : "SELECT COUNT(*) FROM pg_database WHERE datname = @Name;",
                new { Name = name }
            ) != 0;
    }

    public bool OperationsAreEmpty =>
        !Directory.EnumerateFileSystemEntries(Path.Combine(_directory, "operations")).Any();

    public void SetMinimalPackageAvailable(bool available)
    {
        string id = Configuration["DmsDataStoreProvisioning__Templates__Minimal__PackageId"];
        string version = Configuration["DmsDataStoreProvisioning__Templates__Minimal__PackageVersion"];
        string package = Path.GetFullPath(Path.Combine(_directory, "artifacts", $"{id}.{version}.nupkg"));
        string root = Path.GetFullPath(_directory) + Path.DirectorySeparatorChar;
        if (!package.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected fixture package path.");
        }
        // Rename only our signed copy; never modify the source download or another process's files.
        File.Move(available ? package + ".withheld" : package, available ? package : package + ".withheld");
    }

    /// <summary>Explicit post-assertion cleanup of an expected retained target, after the CMS child stops.</summary>
    public async Task RemoveVerifiedTargetAsync(string name, Guid identity, string endpoint)
    {
        var protectedNames = ProtectedDatabaseNames.Create("dms1439_http_cleanup", []);
        if (
            !name.StartsWith("EdFi_Ods_Http", StringComparison.Ordinal)
            || DmsTargetDatabaseName.TryCreate(name, protectedNames, out var target)
                != DmsTargetNameValidation.Valid
        )
        {
            throw new InvalidOperationException("Unexpected cleanup target name.");
        }
        var settings = new DmsDataStoreProvisioningOptions
        {
            Enabled = true,
            Provider = _mssql ? "mssql" : "postgresql",
            AdminConnectionString = _admin,
            OperationDirectory = Configuration["DmsDataStoreProvisioning__OperationDirectory"],
        };
        var options = Options.Create(settings);
        IDmsTargetEngine engine = _mssql
            ? new MssqlDmsTargetEngine(
                new MssqlAdminConnection(_admin, settings.CommandTimeout),
                new MssqlDmsEngineSettings(
                    settings.LockAcquireTimeout,
                    settings.LockHeartbeatInterval,
                    settings.SessionTerminationTimeout,
                    settings.RestoreTimeout,
                    new MssqlStagingSettings(
                        Configuration["DmsDataStoreProvisioning__Mssql__StagingPath"],
                        Configuration["DmsDataStoreProvisioning__Mssql__StagingServerPath"],
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                        settings.CleanupAttempts,
                        settings.CommandTimeout
                    )
                ),
                TimeProvider.System,
                NullLogger.Instance
            )
            : new PostgresqlDmsTargetEngine(
                new PostgresqlAdminConnection(_admin, settings.CommandTimeout),
                new PostgresqlDmsEngineSettings(
                    settings.LockAcquireTimeout,
                    settings.LockHeartbeatInterval,
                    settings.SessionTerminationTimeout,
                    settings.RestoreTimeout,
                    Configuration["DmsDataStoreProvisioning__Postgresql__PsqlPath"]
                ),
                TimeProvider.System,
                NullLogger.Instance
            );
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        if (await engine.ValidateEnvironmentAsync(deadline.Token) is not DmsEngineEnvironment.Ready)
        {
            throw new InvalidOperationException(
                "Cleanup engine is not ready; retained target requires inspection."
            );
        }
        var readiness = new DmsDataStoreProvisionerReadiness();
        readiness.Record(new DmsProvisionerReadiness.Ready(settings.Provider, DateTimeOffset.UtcNow));
        var secrets = Options.Create(new SecretsOptions());
        var provisioner = new DmsDataStoreProvisioningService(
            readiness,
            engine,
            new TemplateArtifactCatalog(options, NullLogger<TemplateArtifactCatalog>.Instance),
            options,
            protectedNames,
            new DmsAdminConnectionSource(
                options,
                new SecretValueCache(secrets),
                secrets,
                TimeProvider.System
            ),
            TimeProvider.System,
            NullLogger<DmsDataStoreProvisioningService>.Instance
        );
        // The actual provisioner holds its target lock, verifies the persisted endpoint/source identity
        // and instance, fences other sessions and drops only that verified target. No CMS state is reset.
        var result = await provisioner.DeleteAsync(
            new(target!, identity),
            ManagedDataStoreEndpoint.FromCanonicalEncoding(endpoint),
            (_, _) => Task.CompletedTask,
            deadline.Token
        );
        if (result is not DmsDataStoreDeleteResult.Dropped)
        {
            throw new InvalidOperationException(
                "Verified cleanup did not drop the retained target; inspect residue."
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Caller disposes the CMS child first. Never drop a target by name on a failed test:
        // unknown physical residue requires identity inspection, even with a generated name.
        if (_loginCreated)
        {
            await using var connection = Connection(_admin);
            await connection.OpenAsync();
            await connection.ExecuteAsync(_mssql ? $"DROP LOGIN [{Login}];" : $"DROP ROLE \"{Login}\";");
            _loginCreated = false;
        }
        string directory = Path.GetFullPath(_directory);
        if (
            !directory.StartsWith(
                Path.Combine(Path.GetFullPath(Path.GetTempPath()), "dms1439-http-provision-"),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException("Unexpected provisioning test cleanup directory.");
        }
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
