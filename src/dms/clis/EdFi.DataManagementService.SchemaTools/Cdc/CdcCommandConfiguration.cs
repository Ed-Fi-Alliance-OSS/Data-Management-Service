// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CoreProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;
using DdlProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;

namespace EdFi.DataManagementService.SchemaTools.Cdc;

/// <summary>Configuration is desired policy, never binding/creation/continuity evidence.</summary>
public sealed class CdcCommandConfiguration(IConfigurationRoot settings)
{
    public IConfigurationRoot Settings { get; } = settings;

    public string Required(string key) =>
        string.IsNullOrWhiteSpace(Settings[key])
            ? throw new ArgumentException("CDC command input is invalid.")
            : Settings[key]!;

    public string Project => Required("Cdc:Compose:Project");
    public string ComposeFile => Required("Cdc:Compose:File");
    public string EnvironmentFile => Required("Cdc:Compose:EnvironmentFile");
    public string BrokerSizeOverride => Required("Cdc:Compose:BrokerSizeOverrideFile");
    public long LagThreshold =>
        long.Parse(Required("Cdc:LagThresholdMilliseconds"), CultureInfo.InvariantCulture);
    public CdcDeploymentTiming Timing =>
        new(
            TimeSpan.FromMilliseconds(Settings.GetValue("Cdc:Timing:CallMilliseconds", 30000)),
            TimeSpan.FromMilliseconds(Settings.GetValue("Cdc:Timing:WaitMilliseconds", 300000)),
            TimeSpan.FromMilliseconds(Settings.GetValue("Cdc:Timing:PollMilliseconds", 1000)),
            TimeSpan.FromMilliseconds(
                Settings.GetValue("Cdc:Timing:MaximumObservationAgeMilliseconds", 10000)
            )
        );

    public CoreProvider GetProvider()
    {
        if (!RelationalProviderToken.TryNormalize(Required("Cdc:Provider"), out var provider))
        {
            throw new ArgumentException("CDC command input is invalid.");
        }
        return provider == RelationalProviderToken.Postgresql
            ? CoreProvider.Postgresql
            : CoreProvider.SqlServer;
    }

    public CdcTargetIdentity Target =>
        new(
            Required("Cdc:DeploymentKey"),
            CdcTargetValidator.MapE18TenantKeyToBindingTenantKey(Settings["Cdc:TenantKey"] ?? "")!,
            Required("Cdc:DataStoreId"),
            Required("Cdc:InstanceKey"),
            Settings.GetValue<long>("Cdc:Generation"),
            GetProvider()
        );

    public static CdcCommandConfiguration Load(string path) =>
        new(
            new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(path), optional: false, reloadOnChange: false)
                .AddEnvironmentVariables("DMS_CDC__")
                .Build()
        );

    public void Validate()
    {
        ValidateControllerSettings();
        ValidateProjectionTarget();
    }

    public void ValidateControllerSettings()
    {
        if (
            !CdcTargetValidator
                .ValidateBindingIdentity(CdcBindingIdentity.FromTargetIdentity(Target))
                .Succeeded
            || LagThreshold < 0
            || Settings["AppSettings:Datastore"]
                != (GetProvider() == CoreProvider.Postgresql ? "postgresql" : "mssql")
        )
        {
            throw new ArgumentException("CDC command input is invalid.");
        }
        _ = Timing;
        _ = Project;
        _ = ComposeFile;
        _ = EnvironmentFile;
        _ = BrokerSizeOverride;
        // This shipped adapter has authority only over the local single-broker Compose deployment.
        if (
            Required("Cdc:DurabilityProfile") != "LocalSingleBroker"
            || Required("Cdc:AuthorizationProfile") != "AuthorizationDisabledLocal"
        )
        {
            throw new ArgumentException("CDC command input is invalid.");
        }
    }

    private void ValidateProjectionTarget()
    {
        var options = new DocumentCacheOptions();
        Settings.GetSection(DocumentCacheOptions.SectionName).Bind(options);
        var key = DocumentCacheTargetKey.Create(
            Settings["Cdc:TenantKey"] ?? "",
            long.Parse(Target.DataStoreId, CultureInfo.InvariantCulture)
        );
        if (!options.GetTargetKeys().Contains(key))
        {
            throw new ArgumentException("CDC command input is invalid.");
        }
    }

    public DbConnection CreateConnection()
    {
        string connection = Required("Cdc:SetupConnectionString");
        return GetProvider() == CoreProvider.Postgresql
            ? new NpgsqlConnection(connection)
            : new SqlConnection(connection);
    }

    public async Task<CdcDeploymentRequest> CreateRequestAsync(
        string statePath,
        DbConnection connection,
        IApiSchemaFileLoader loader,
        EffectiveSchemaSetBuilder builder,
        CancellationToken token,
        bool deferProjectionPreparation = false,
        bool useRetainedBinding = false
    )
    {
        ValidateControllerSettings();
        var target = Target;
        var timing = Timing;
        string fingerprint;
        CdcBinding retainedBinding = null!;
        if (useRetainedBinding)
        {
            // Status/watch need the original binding to contain retained incidents even when the
            // journal cannot be read. Journal-dependent observation remains the controller's job.
            var services = new ServiceCollection();
            services.AddDmsCdcControlPlane();
            services.Configure<CdcBindingStateStoreOptions>(options => options.RootPath = statePath);
            await using var provider = services.BuildServiceProvider();
            var retained = await provider
                .GetRequiredService<ICdcBindingLifecycleService>()
                .ReadBindingAsync(CdcBindingIdentity.FromTargetIdentity(target), token);
            if (
                retained.Status != CdcControlPlaneOperationStatus.Succeeded
                || retained.State?.Binding is null
            )
            {
                throw new ArgumentException("CDC retained binding is unavailable or invalid.");
            }
            retainedBinding = retained.State.Binding;
            fingerprint = retainedBinding.PhysicalSourceFingerprint;
        }
        else
        {
            // Read only existing trusted provenance. Every controller revalidates it under its own lock.
            await using (
                var session = await new LocalCdcWorkflowJournalStore(statePath).AcquireAsync(
                    timing.CallTimeout,
                    timing.PollInterval < timing.CallTimeout ? timing.PollInterval : timing.CallTimeout,
                    token
                )
            )
            {
                var journal = await session.ReadAsync(target, token);
                fingerprint = journal
                    .Operations.Where(o => o.Effect == CdcWorkflowEffect.AssociateSource)
                    .SelectMany(o => o.Completions)
                    .Select(c => c.Evidence)
                    .OfType<CdcWorkflowCompletion.Source>()
                    .Single()
                    .PhysicalSourceFingerprint;
            }
        }
        var names = CdcArtifactNameGenerator.Render(
            new(
                target.DeploymentKey,
                Required("Cdc:TopicPrefix"),
                target.InstanceKey,
                target.Generation,
                target.Provider
            )
        );
        if (!names.Succeeded)
        {
            throw new ArgumentException("CDC command input is invalid.");
        }
        var inventory = names.Inventory!;
        var binding = new CdcBinding(
            CdcJsonContract.CurrentContractVersion,
            target.DeploymentKey,
            target.TenantKey,
            target.DataStoreId,
            target.InstanceKey,
            target.Generation,
            target.Provider,
            fingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            Settings.GetValue<int>("Cdc:PartitionCount"),
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CdcJsonContract.CurrentContractVersion
        );
        if (useRetainedBinding)
        {
            if (binding != retainedBinding)
            {
                throw new ArgumentException("CDC command input does not match the retained binding.");
            }
            binding = retainedBinding;
        }
        var ddlProvider =
            GetProvider() == CoreProvider.Postgresql ? DdlProvider.Postgresql : DdlProvider.SqlServer;
        CdcProviderSetupRequest PrepareProviderSetup()
        {
            ValidateProjectionTarget();
            var schemaPaths = Settings.GetSection("Cdc:Schemas").Get<string[]>() ?? [];
            if (schemaPaths.Length == 0)
            {
                throw new ArgumentException("CDC command input is invalid.");
            }
            token.ThrowIfCancellationRequested();
            var loaded = loader.Load(schemaPaths[0], schemaPaths.Skip(1).ToList());
            if (loaded is not ApiSchemaFileLoadResult.SuccessResult success)
            {
                throw new ArgumentException("CDC command input is invalid.");
            }
            var schema = builder.Build(success.NormalizedNodes);
            var emission = DdlPipelineHelpers.BuildDdlEmissionForDialect(
                schema,
                GetProvider() == CoreProvider.Postgresql ? SqlDialect.Pgsql : SqlDialect.Mssql
            );
            token.ThrowIfCancellationRequested();
            return new CdcProviderSetupRequest(
                ddlProvider,
                EdFi.DataManagementService.Backend.Ddl.CdcProviderSetupMode.ValidateOnly,
                new(CdcSourceFingerprintMetadata.Version, fingerprint),
                new(new(Required("Cdc:SetupPrincipal"))),
                new(new(Required("Cdc:DatabaseConnectorPrincipal"))),
                CdcDeploymentRequest.GetProviderArtifactNames(binding),
                new(false),
                emission.CdcSourceInventory,
                emission.CdcDmsManagedTableInventory,
                databaseExecutor: new DbConnectionCdcProviderDatabaseExecutor(connection)
            );
        }
        var worker = new CdcWorkerDeploymentPolicy(
            new(Required("Cdc:Worker:Key")),
            new(Required("Cdc:Worker:OffsetStorageTopic")),
            CdcQualifiedWorkerImage.Digest,
            Settings.GetValue<long>("Cdc:Worker:HeapBytes"),
            "All",
            CdcKafkaDurabilityProfile.LocalSingleBroker,
            CdcKafkaAuthorizationProfile.AuthorizationDisabledLocal,
            new(Required("Cdc:Worker:Principal")),
            new(Required("Cdc:Worker:ConnectorPrincipal")),
            new(Required("Cdc:Worker:AdministratorPrincipal")),
            Settings
                .GetSection("Cdc:Consumers")
                .GetChildren()
                .Select(c => new CdcConsumerAccess(new(c["Principal"]!), new(c["Group"]!)))
                .ToArray()
        );
        var request = CdcDeploymentRequest.CreateDeferred(
            binding,
            Settings,
            PrepareProviderSetup,
            new(Required("Cdc:ConnectEndpoint")),
            new(Required("Cdc:WorkerMetricsEndpoint")),
            new(
                Required("Cdc:KafkaBootstrapServers"),
                Settings.GetValue<int>("Cdc:MaxRecordBytes"),
                Settings.GetValue<int?>("Cdc:ProducerBufferBytes"),
                TimeSpan.FromMilliseconds(Settings.GetValue("Cdc:HeartbeatMilliseconds", 1000)),
                TimeSpan.FromMilliseconds(Settings.GetValue("Cdc:SqlServerPollMilliseconds", 500))
            ),
            worker,
            new(ddlProvider, Properties("Cdc:ProviderConnectionProperties")),
            new(Properties("Cdc:KafkaClientSecurityProperties")),
            timing
        );
        if (!deferProjectionPreparation)
        {
            _ = request.ProviderSetup;
        }
        return request;
    }

    public Dictionary<string, string> Properties(string key) =>
        Settings
            .GetSection(key)
            .GetChildren()
            .ToDictionary(
                c => c.Key,
                c => c.Value ?? throw new ArgumentException("CDC command input is invalid.")
            );

    public override string ToString() => nameof(CdcCommandConfiguration);
}
