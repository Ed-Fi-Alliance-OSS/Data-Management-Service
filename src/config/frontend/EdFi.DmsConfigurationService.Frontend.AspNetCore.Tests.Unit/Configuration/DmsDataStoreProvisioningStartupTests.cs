// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// Spec Step 1.3: the shipped settings leave DMS data store provisioning disabled; enabling it with invalid
/// static configuration stops the host naming the setting; Step 2.4: the catalog probes run at startup (a
/// valid policy over an empty artifact directory records <c>ArtifactMissing</c> per template); Step 3.9:
/// enabled for PostgreSQL under either catalog datastore with a server nothing listens on, the host runs, the
/// real adapter records the server and <c>psql</c> reasons, and the orchestrator refuses before any engine
/// call; an engine and a catalog that both report ready make readiness <c>Ready</c> with the protected set
/// logged; a secret that cannot be resolved is <c>SecretResolutionFailed</c> and the engine probes do not run;
/// an engine validation that throws is recorded by type and the host keeps running; Step 4.9: enabled for SQL
/// Server under either catalog datastore with a server nothing listens on, the host runs, the real adapter records
/// the server and staging directory reasons, and the orchestrator refuses before any engine call; an otherwise
/// ready deployment whose server is older than the templates' engine is NotReady as EngineIncompatible, and one
/// whose server reaches it is Ready.
/// </summary>
public class DmsDataStoreProvisioningStartupTests
{
    private const string Section = "DmsDataStoreProvisioning";

    private const string PostgresqlCatalog =
        "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1";

    private const string MssqlCatalog =
        "Data Source=127.0.0.1,1;Initial Catalog=unreachable;User ID=none;Password=none;Connect Timeout=1";

    private static string LocalPath(string leaf) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dms-1438-startup-tests", leaf));

    /// <summary>
    /// Every setting goes through <c>UseSetting</c>, because the datastore, <c>Enabled</c> and <c>Provider</c>
    /// are read while the services are registered, before the application configuration is applied.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string> hostSettings,
        ILoggerProvider? logs = null,
        Action<IServiceCollection>? configureServices = null
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            foreach ((string key, string value) in hostSettings)
            {
                builder.UseSetting(key, value);
            }
            if (logs is not null)
            {
                builder.ConfigureServices(services => services.AddSingleton(logs));
            }
            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        });

    /// <summary>A statically valid enabled section for the target provider, booted under the given catalog datastore.</summary>
    private static Dictionary<string, string> EnabledSettings(string provider, string datastore)
    {
        string engine = provider == "postgresql" ? "PostgreSql" : "MsSql";
        return new Dictionary<string, string>
        {
            ["AppSettings:Datastore"] = datastore,
            ["DatabaseSettings:DatabaseConnection"] =
                datastore == "postgresql" ? PostgresqlCatalog : MssqlCatalog,
            [$"{Section}:Enabled"] = "true",
            [$"{Section}:Provider"] = provider,
            [$"{Section}:AdminConnectionString"] =
                provider == "postgresql"
                    ? "Host=127.0.0.1;Port=1;Username=none;Password=none"
                    : "Server=127.0.0.1,1;User Id=none;Password=none",
            [$"{Section}:TrustPolicyPath"] = LocalPath("template-trust-policy.json"),
            [$"{Section}:ArtifactDirectory"] = LocalPath("templates"),
            [$"{Section}:Templates:Minimal:PackageId"] = $"EdFi.Api.Minimal.Template.{engine}.5.2.0",
            [$"{Section}:Templates:Minimal:PackageVersion"] = "1.0.335",
            [$"{Section}:Templates:Sample:PackageId"] = $"EdFi.Api.Populated.Template.{engine}.5.2.0",
            [$"{Section}:Templates:Sample:PackageVersion"] = "1.0.335",
            [$"{Section}:Compatibility:DataStandardVersion"] = "5.2.0",
            [$"{Section}:Compatibility:EffectiveSchemaHash"] = new string('a', 64),
            [$"{Section}:Compatibility:Projects:0"] = "edfi",
            [$"{Section}:Postgresql:PsqlPath"] = LocalPath("psql"),
            [$"{Section}:Mssql:StagingPath"] = LocalPath("staging"),
            [$"{Section}:Mssql:StagingServerPath"] = "/var/opt/dms-template-staging",
        };
    }

    /// <summary>Boot is triggered here: WebApplicationFactory defers the entry point until the server is needed.</summary>
    private static Exception? StartupExceptionFor(WebApplicationFactory<Program> factory)
    {
        try
        {
            using var client = factory.CreateClient();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static DmsDataStoreCreateRequest AnyCreateRequest()
    {
        DmsTargetDatabaseName.TryCreate(
            "EdFi_Ods_Test",
            ProtectedDatabaseNames.Create("edfi_configurationservice", []),
            out DmsTargetDatabaseName name
        );
        return new DmsDataStoreCreateRequest(
            DmsDatabaseTemplate.Minimal,
            name,
            Guid.NewGuid(),
            new DmsTrustedArtifactPin
            {
                ContractVersion = 1,
                Provider = "postgresql",
                PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
                PackageVersion = "1.0.335",
                PackageSha256 = new string('a', 64),
                ArtifactSha256 = new string('b', 64),
                TemplateKind = "Minimal",
                DataStandardVersion = "5.2.0",
                EffectiveSchemaHash = new string('c', 64),
                InventorySha256 = new string('d', 64),
                ApiSchemaFormatVersion = "1.0.0",
                RelationalMappingVersion = "v3",
            }
        );
    }

    [TestFixture]
    public class Given_the_shipped_settings_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private DmsProvisionerReadiness _readiness = null!;
        private IDmsDataStoreProvisioner _provisioner = null!;
        private IDmsTargetEngine? _engine;
        private ITemplateArtifactCatalog? _catalog;
        private IValidateOptions<DmsDataStoreProvisioningOptions>? _validator;
        private string? _shippedEnabled;
        private DmsDataStoreCreateResult _createResult = null!;

        [SetUp]
        public async Task Act()
        {
            _factory = CreateFactory([]);
            _exception = StartupExceptionFor(_factory);
            _readiness = _factory.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>().Current;
            _provisioner = _factory.Services.GetRequiredService<IDmsDataStoreProvisioner>();
            _engine = _factory.Services.GetService<IDmsTargetEngine>();
            _catalog = _factory.Services.GetService<ITemplateArtifactCatalog>();
            _validator = _factory.Services.GetService<IValidateOptions<DmsDataStoreProvisioningOptions>>();
            _shippedEnabled = _factory.Services.GetRequiredService<IConfiguration>().GetSection(Section)[
                "Enabled"
            ];
            _createResult = await _provisioner.CreateAsync(AnyCreateRequest(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _exception.Should().BeNull();

        [Test]
        public void It_ships_the_section_disabled() => _shippedEnabled.Should().BeEquivalentTo("false");

        [Test]
        public void It_records_readiness_as_disabled() =>
            _readiness.Should().BeOfType<DmsProvisionerReadiness.Disabled>();

        [Test]
        public void It_registers_the_refusing_provisioner() =>
            _provisioner.Should().BeOfType<DisabledDmsDataStoreProvisioner>();

        [Test]
        public void It_registers_no_engine_no_catalog_and_no_validation()
        {
            _engine.Should().BeNull();
            _catalog.Should().BeNull();
            _validator.Should().BeNull();
        }

        [Test]
        public void It_refuses_a_create_as_feature_disabled() =>
            _createResult
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Which.Detail.Should()
                .Match<DmsProvisioningFailure>(failure =>
                    failure.Category == DmsProvisioningFailureCategory.NotReady
                    && failure.Code == DmsProvisioningFailureCode.FeatureDisabled
                );
    }

    [TestFixture]
    public class Given_enabled_with_invalid_static_configuration
    {
        private static OptionsValidationException? FindOptionsValidationException(Exception? exception) =>
            exception switch
            {
                null => null,
                OptionsValidationException match => match,
                AggregateException aggregate => aggregate
                    .InnerExceptions.Select(FindOptionsValidationException)
                    .FirstOrDefault(found => found is not null),
                _ => FindOptionsValidationException(exception.InnerException),
            };

        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private OptionsValidationException? _validationFailure;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(
                new Dictionary<string, string>
                {
                    [$"{Section}:Enabled"] = "true",
                    [$"{Section}:Provider"] = "postgresql",
                }
            );
            _exception = StartupExceptionFor(_factory);
            _validationFailure = FindOptionsValidationException(_exception);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_fails_to_start() => _exception.Should().NotBeNull();

        [Test]
        public void It_fails_with_the_validator_text_naming_the_setting() =>
            _validationFailure
                .Should()
                .NotBeNull()
                .And.Subject.As<OptionsValidationException>()
                .Message.Should()
                .Contain($"{Section}:AdminConnectionString is required.");
    }

    /// <summary>
    /// V4-03: enabled with a provider token that names no adapter, or with none, and an otherwise valid section. The
    /// host fails to start with the options validator's text naming the setting, even with the service provider
    /// validating every registration when it is built, so the failure is never a missing engine service.
    /// </summary>
    [TestFixture("oracle")]
    [TestFixture(null)]
    public class Given_enabled_with_a_provider_that_names_no_adapter(string? provider)
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;

        [SetUp]
        public void Act()
        {
            Dictionary<string, string> settings = EnabledSettings("postgresql", "postgresql");
            settings.Remove($"{Section}:Provider");
            if (provider is not null)
            {
                settings[$"{Section}:Provider"] = provider;
            }

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseDefaultServiceProvider(options => options.ValidateOnBuild = true);
                foreach ((string key, string value) in settings)
                {
                    builder.UseSetting(key, value);
                }
            });
            _exception = StartupExceptionFor(_factory);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        private static IEnumerable<Exception> Chain(Exception? exception) =>
            exception switch
            {
                null => [],
                AggregateException aggregate => [aggregate, .. aggregate.InnerExceptions.SelectMany(Chain)],
                _ => [exception, .. Chain(exception.InnerException)],
            };

        [Test]
        public void It_fails_with_the_validator_text_naming_the_provider_setting() =>
            Chain(_exception)
                .OfType<OptionsValidationException>()
                .Should()
                .ContainSingle()
                .Which.Message.Should()
                .Contain($"{Section}:Provider must be exactly postgresql or mssql.");

        [Test]
        public void It_does_not_fail_on_a_missing_engine_service() =>
            Chain(_exception)
                .Select(exception => exception.Message)
                .Should()
                .NotContain(message => message.Contains(nameof(IDmsTargetEngine), StringComparison.Ordinal));
    }

    /// <summary>
    /// Step 4.9: enabled for SQL Server with a server nothing listens on and a staging directory that does not
    /// exist. The real adapter is registered and wrapped by a recording fake, so the fixture sees every call that
    /// reaches it.
    /// </summary>
    [TestFixture("postgresql")]
    [TestFixture("mssql")]
    public class Given_enabled_for_sqlserver_with_an_unreachable_server_under_a_catalog_datastore(
        string datastore
    )
    {
        private CapturingLoggerProvider _logs = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private IDmsTargetEngine? _realEngine;
        private IDmsTargetEngine _recordingEngine = null!;
        private IDmsDataStoreProvisioner _provisioner = null!;
        private DmsProvisionerReadiness _readiness = null!;
        private DmsDataStoreCreateResult _create = null!;
        private DmsDataStoreDeleteResult _delete = null!;
        private string[] _engineCalls = [];
        private CapturedLog[] _criticalEntries = [];

        [SetUp]
        public async Task Act()
        {
            _logs = new CapturingLoggerProvider();
            _factory = CreateFactory(
                EnabledSettings("mssql", datastore),
                _logs,
                services =>
                {
                    ServiceDescriptor registered = services.Single(descriptor =>
                        descriptor.ServiceType == typeof(IDmsTargetEngine)
                    );
                    services.Remove(registered);
                    services.AddSingleton<IDmsTargetEngine>(provider =>
                    {
                        _realEngine = (IDmsTargetEngine)registered.ImplementationFactory!(provider);
                        _recordingEngine = A.Fake<IDmsTargetEngine>(options => options.Wrapping(_realEngine));
                        return _recordingEngine;
                    });
                }
            );
            _exception = StartupExceptionFor(_factory);
            _readiness = _factory.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>().Current;
            _provisioner = _factory.Services.GetRequiredService<IDmsDataStoreProvisioner>();
            _create = await _provisioner.CreateAsync(AnyCreateRequest(), CancellationToken.None);
            _delete = await _provisioner.DeleteAsync(
                new DmsDataStoreDeleteRequest(AnyCreateRequest().TargetName, Guid.NewGuid()),
                CancellationToken.None
            );
            _engineCalls =
            [
                .. Fake.GetCalls(_recordingEngine)
                    .Select(call => call.Method.Name)
                    .Where(name => name != "get_Provider"),
            ];
            _criticalEntries =
            [
                .. _logs.Entries.Where(entry =>
                    entry.Level == LogLevel.Critical && entry.EventId.Id == 1438_02
                ),
            ];
        }

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
            _logs.Dispose();
        }

        private IReadOnlyList<DmsReadinessReason> Reasons =>
            _readiness.Should().BeOfType<DmsProvisionerReadiness.NotReady>().Subject.Reasons;

        [Test]
        public void It_starts_and_keeps_running() => _exception.Should().BeNull();

        [Test]
        public void It_registers_the_sql_server_adapter_whatever_the_catalog_datastore() =>
            _realEngine.Should().BeOfType<MssqlDmsTargetEngine>();

        [Test]
        public void It_registers_the_orchestrator_as_the_provisioner() =>
            _provisioner.Should().BeOfType<DmsDataStoreProvisioningService>();

        [Test]
        public void It_records_the_unreachable_server_as_server_unavailable() =>
            Reasons
                .Should()
                .ContainSingle(reason => reason.Probe == "TargetServer")
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.ServerUnavailable
                    && reason.Diagnostic!.Operation == "TargetServer.Connect"
                    && reason.Diagnostic.ExceptionTypeChain.Length > 0
                );

        [Test]
        public void It_records_the_missing_staging_directory_beside_it() =>
            Reasons
                .Should()
                .ContainSingle(reason => reason.Probe == "StagingDirectory")
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.StartupValidationFailed
                    && reason.Diagnostic!.Operation == "StagingDirectory.Missing"
                );

        [Test]
        public void It_skips_the_visibility_and_lock_probes_on_an_unreachable_server() =>
            Reasons.Select(reason => reason.Probe).Should().NotContain(["StagingVisibility", "TargetLock"]);

        [Test]
        public void It_refuses_a_create_and_a_delete_as_not_ready() =>
            new[] { FailureOf(_create), FailureOf(_delete) }
                .Should()
                .AllSatisfy(failure =>
                    (failure.Category, failure.Code)
                        .Should()
                        .Be(
                            (
                                DmsProvisioningFailureCategory.NotReady,
                                DmsProvisioningFailureCode.StartupValidationFailed
                            )
                        )
                );

        [Test]
        public void It_reaches_the_engine_only_for_the_startup_validation() =>
            _engineCalls.Should().Equal(nameof(IDmsTargetEngine.ValidateEnvironmentAsync));

        [Test]
        public void It_logs_the_engine_reasons_at_critical_naming_the_target_provider() =>
            _criticalEntries
                .Where(log =>
                    Equals(log.State["Probe"], "TargetServer")
                    || Equals(log.State["Probe"], "StagingDirectory")
                )
                .Should()
                .HaveCount(2)
                .And.AllSatisfy(log => log.State["Provider"].Should().Be("mssql"));

        private static DmsProvisioningFailure FailureOf(DmsDataStoreCreateResult result) =>
            result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

        private static DmsProvisioningFailure FailureOf(DmsDataStoreDeleteResult result) =>
            result.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Detail;
    }

    /// <summary>
    /// Step 3.9: enabled for PostgreSQL with a server nothing listens on. The real adapter is registered and
    /// wrapped by a recording fake, so the fixture sees every call that reaches it.
    /// </summary>
    [TestFixture("postgresql")]
    [TestFixture("mssql")]
    public class Given_enabled_for_postgresql_with_an_unreachable_server_under_a_catalog_datastore(
        string datastore
    )
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private IDmsTargetEngine? _realEngine;
        private IDmsTargetEngine _recordingEngine = null!;
        private IDmsDataStoreProvisioner _provisioner = null!;
        private DmsProvisionerReadiness _readiness = null!;
        private DmsDataStoreCreateResult _create = null!;
        private DmsDataStoreDeleteResult _delete = null!;
        private string[] _engineCalls = [];

        [SetUp]
        public async Task Act()
        {
            _factory = CreateFactory(
                EnabledSettings("postgresql", datastore),
                configureServices: services =>
                {
                    ServiceDescriptor registered = services.Single(descriptor =>
                        descriptor.ServiceType == typeof(IDmsTargetEngine)
                    );
                    services.Remove(registered);
                    services.AddSingleton<IDmsTargetEngine>(provider =>
                    {
                        _realEngine = (IDmsTargetEngine)registered.ImplementationFactory!(provider);
                        _recordingEngine = A.Fake<IDmsTargetEngine>(options => options.Wrapping(_realEngine));
                        return _recordingEngine;
                    });
                }
            );
            _exception = StartupExceptionFor(_factory);
            _readiness = _factory.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>().Current;
            _provisioner = _factory.Services.GetRequiredService<IDmsDataStoreProvisioner>();
            _create = await _provisioner.CreateAsync(AnyCreateRequest(), CancellationToken.None);
            _delete = await _provisioner.DeleteAsync(
                new DmsDataStoreDeleteRequest(AnyCreateRequest().TargetName, Guid.NewGuid()),
                CancellationToken.None
            );
            _engineCalls =
            [
                .. Fake.GetCalls(_recordingEngine)
                    .Select(call => call.Method.Name)
                    .Where(name => name != "get_Provider"),
            ];
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts_and_keeps_running() => _exception.Should().BeNull();

        [Test]
        public void It_registers_the_postgresql_adapter_whatever_the_catalog_datastore() =>
            _realEngine.Should().BeOfType<PostgresqlDmsTargetEngine>();

        [Test]
        public void It_registers_the_orchestrator_as_the_provisioner() =>
            _provisioner.Should().BeOfType<DmsDataStoreProvisioningService>();

        [Test]
        public void It_records_the_unreachable_server_as_server_unavailable() =>
            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Should()
                .ContainSingle(reason => reason.Probe == "TargetServer")
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.ServerUnavailable
                    && reason.Diagnostic!.Operation == "TargetServer.Connect"
                    && reason.Diagnostic.ExceptionTypeChain.Length > 0
                );

        [Test]
        public void It_records_the_missing_psql_beside_it() =>
            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Should()
                .ContainSingle(reason => reason.Probe == "PsqlVersion")
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.StartupValidationFailed
                    && reason.Diagnostic!.Operation == "ProbePsqlVersion.StartFailed"
                );

        [Test]
        public void It_skips_the_lock_probe_on_an_unreachable_server() =>
            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Select(reason => reason.Probe)
                .Should()
                .NotContain("TargetLock");

        [Test]
        public void It_refuses_a_create_and_a_delete_as_not_ready() =>
            new[] { FailureOf(_create), FailureOf(_delete) }
                .Should()
                .AllSatisfy(failure =>
                    (failure.Category, failure.Code)
                        .Should()
                        .Be(
                            (
                                DmsProvisioningFailureCategory.NotReady,
                                DmsProvisioningFailureCode.StartupValidationFailed
                            )
                        )
                );

        [Test]
        public void It_reaches_the_engine_only_for_the_startup_validation() =>
            _engineCalls.Should().Equal(nameof(IDmsTargetEngine.ValidateEnvironmentAsync));

        private static DmsProvisioningFailure FailureOf(DmsDataStoreCreateResult result) =>
            result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

        private static DmsProvisioningFailure FailureOf(DmsDataStoreDeleteResult result) =>
            result.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Detail;
    }

    [TestFixture]
    public class Given_a_valid_trust_policy_over_an_empty_artifact_directory_with_a_ready_engine
    {
        private string _root = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private DmsProvisionerReadiness _readiness = null!;

        /// <summary>A policy in the delivered schema listing one ephemeral P-256 producer.</summary>
        private static string PolicyJson()
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] subjectPublicKeyInfo = key.ExportSubjectPublicKeyInfo();
            string keyId = Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));
            string spki = Convert.ToBase64String(subjectPublicKeyInfo);
            return $$"""
                { "version": 1, "producers": [ { "name": "startup-test", "provider": "detached-attestation",
                  "publicKeys": [ { "keyId": "{{keyId}}", "algorithm": "ECDSA_P256_SHA256", "publicKeySpkiB64": "{{spki}}" } ] } ] }
                """;
        }

        [SetUp]
        public void Act()
        {
            _root = LocalPath(Guid.NewGuid().ToString("N"));
            string artifactDirectory = Path.Combine(_root, "templates");
            string policyPath = Path.Combine(_root, "template-trust-policy.json");
            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllText(policyPath, PolicyJson());

            IDmsTargetEngine engine = ReadyEngine();

            Dictionary<string, string> settings = EnabledSettings("postgresql", "postgresql");
            settings[$"{Section}:TrustPolicyPath"] = policyPath;
            settings[$"{Section}:ArtifactDirectory"] = artifactDirectory;
            _factory = CreateFactory(
                settings,
                configureServices: services =>
                {
                    services.RemoveAll<IDmsTargetEngine>();
                    services.AddSingleton(engine);
                }
            );
            _exception = StartupExceptionFor(_factory);
            _readiness = _factory.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>().Current;
        }

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void It_starts() => _exception.Should().BeNull();

        [Test]
        public void It_records_the_catalog_probes_of_both_templates_and_nothing_else() =>
            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Should()
                .BeEquivalentTo([
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.ArtifactMissing,
                        TemplateArtifactCatalog.CatalogProbeFor(DmsDatabaseTemplate.Minimal),
                        DmsProvisioningDiagnostic.ForOperation(TemplateArtifactCatalog.LocateArtifactStep)
                    ),
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.ArtifactMissing,
                        TemplateArtifactCatalog.CatalogProbeFor(DmsDatabaseTemplate.Sample),
                        DmsProvisioningDiagnostic.ForOperation(TemplateArtifactCatalog.LocateArtifactStep)
                    ),
                ]);
    }

    [TestFixture]
    public class Given_an_engine_and_a_catalog_that_both_report_ready
    {
        private CapturingLoggerProvider _logs = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private DmsProvisionerReadiness _readiness = null!;
        private CapturedLog[] _protectedNamesEntries = [];

        [SetUp]
        public void Act()
        {
            _logs = new CapturingLoggerProvider();
            IDmsTargetEngine engine = ReadyEngine();
            ITemplateArtifactCatalog catalog = ReadyCatalog();

            Dictionary<string, string> settings = EnabledSettings("postgresql", "postgresql");
            settings[$"{Section}:AdditionalProtectedDatabaseNames:0"] = "edfi_identity";
            _factory = CreateFactory(
                settings,
                _logs,
                services =>
                {
                    services.RemoveAll<IDmsTargetEngine>();
                    services.AddSingleton(engine);
                    services.RemoveAll<ITemplateArtifactCatalog>();
                    services.AddSingleton(catalog);
                }
            );
            _exception = StartupExceptionFor(_factory);
            _readiness = _factory.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>().Current;
            _protectedNamesEntries = [.. _logs.Entries.Where(entry => entry.EventId.Id == 1438_04)];
        }

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
            _logs.Dispose();
        }

        [Test]
        public void It_starts() => _exception.Should().BeNull();

        [Test]
        public void It_records_ready_for_the_engine_provider() =>
            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.Ready>()
                .Which.Provider.Should()
                .Be("postgresql");

        [Test]
        public void It_logs_the_protected_set_with_the_catalog_database_and_the_operator_entries() =>
            _protectedNamesEntries
                .Should()
                .ContainSingle()
                .Which.State["ProtectedNames"]
                .Should()
                .Be("edfi_identity, unreachable");
    }

    [TestFixture]
    public class Given_the_admin_connection_secret_cannot_be_resolved_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private DmsProvisionerReadiness _readiness = null!;
        private IDmsTargetEngine _engine = null!;

        [SetUp]
        public void Act()
        {
            _engine = ReadyEngine();
            ITemplateArtifactCatalog catalog = ReadyCatalog();
            ISecretResolver resolver = A.Fake<ISecretResolver>();
            A.CallTo(() => resolver.ResolveAsync(A<SecretReference>._, A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("the secret value must never be reported"));

            Dictionary<string, string> settings = EnabledSettings("postgresql", "postgresql");
            settings[$"{Section}:AdminConnectionString"] =
                "Host=127.0.0.1;Port=1;Username=none;Password=${secret:pg-admin}";
            _factory = CreateFactory(
                settings,
                configureServices: services =>
                {
                    services.RemoveAll<ISecretResolver>();
                    services.AddSingleton(resolver);
                    services.RemoveAll<IDmsTargetEngine>();
                    services.AddSingleton(_engine);
                    services.RemoveAll<ITemplateArtifactCatalog>();
                    services.AddSingleton(catalog);
                }
            );
            _exception = StartupExceptionFor(_factory);
            _readiness = _factory.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>().Current;
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _exception.Should().BeNull();

        [Test]
        public void It_records_secret_resolution_failed_by_the_resolver_exception_type_only() =>
            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.SecretResolutionFailed,
                        DmsDataStoreProvisioningStartup.SecretResolutionProbe,
                        new DmsProvisioningDiagnostic(
                            "System.InvalidOperationException",
                            null,
                            DmsAdminConnectionSource.ResolverFailedStep
                        )
                    )
                );

        [Test]
        public void It_does_not_run_the_engine_probes_without_a_connection_string() =>
            A.CallTo(() => _engine.ValidateEnvironmentAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_the_engine_validation_throws
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private DmsProvisionerReadiness _readiness = null!;

        [SetUp]
        public void Act()
        {
            IDmsTargetEngine engine = A.Fake<IDmsTargetEngine>();
            A.CallTo(() => engine.Provider).Returns("postgresql");
            A.CallTo(() => engine.ValidateEnvironmentAsync(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("not for the log"));
            ITemplateArtifactCatalog catalog = ReadyCatalog();
            _factory = CreateFactory(
                EnabledSettings("postgresql", "postgresql"),
                configureServices: services =>
                {
                    services.RemoveAll<IDmsTargetEngine>();
                    services.AddSingleton(engine);
                    services.RemoveAll<ITemplateArtifactCatalog>();
                    services.AddSingleton(catalog);
                }
            );
            _exception = StartupExceptionFor(_factory);
            _readiness = _factory.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>().Current;
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts_and_keeps_running() => _exception.Should().BeNull();

        [Test]
        public void It_records_the_engine_probes_as_failed_by_type() =>
            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        DmsDataStoreProvisioningStartup.EngineProbesProbe,
                        new DmsProvisioningDiagnostic(
                            "System.InvalidOperationException",
                            null,
                            DmsDataStoreProvisioningStartup.EngineProbesProbe
                        )
                    )
                );
    }

    /// <summary>
    /// Step 4.9 review: an engine and a catalog that each report ready, for SQL Server, with templates whose
    /// authenticated manifests name SQL Server 2025. A SQL Server 2022 server is <c>NotReady</c> with one
    /// <c>EngineIncompatible</c> reason per template and every create refused; the control, a server of the
    /// templates' major, is <c>Ready</c>.
    /// </summary>
    [TestFixture(16, false)]
    [TestFixture(17, true)]
    public class Given_an_otherwise_ready_deployment_and_templates_of_a_given_engine(
        int serverMajor,
        bool compatible
    )
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private DmsProvisionerReadiness _readiness = null!;
        private DmsDataStoreCreateResult? _create;

        private static DmsReadinessReason Incompatible(DmsDatabaseTemplate template) =>
            new(
                DmsProvisioningFailureCode.EngineIncompatible,
                DmsEngineCompatibility.ProbeFor(template),
                DmsProvisioningDiagnostic.ForOperation(
                    DmsEngineCompatibility.ProbeFor(template)
                        + "."
                        + DmsEngineCompatibility.ServerBelowManifestCheck
                )
            );

        [SetUp]
        public async Task Act()
        {
            IDmsTargetEngine engine = ReadyEngine(serverMajor, "mssql");
            ITemplateArtifactCatalog catalog = ReadyCatalog(
                new TemplateEngineRequirement(DmsDatabaseTemplate.Minimal, "17.0.900.7"),
                new TemplateEngineRequirement(DmsDatabaseTemplate.Sample, "17.0.900.7")
            );
            _factory = CreateFactory(
                EnabledSettings("mssql", "mssql"),
                configureServices: services =>
                {
                    services.RemoveAll<IDmsTargetEngine>();
                    services.AddSingleton(engine);
                    services.RemoveAll<ITemplateArtifactCatalog>();
                    services.AddSingleton(catalog);
                }
            );
            _exception = StartupExceptionFor(_factory);
            _readiness = _factory.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>().Current;
            if (!compatible)
            {
                _create = await _factory
                    .Services.GetRequiredService<IDmsDataStoreProvisioner>()
                    .CreateAsync(AnyCreateRequest(), CancellationToken.None);
            }
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _exception.Should().BeNull();

        [Test]
        public void It_records_the_verdict_the_engine_versions_imply()
        {
            if (compatible)
            {
                _readiness
                    .Should()
                    .BeOfType<DmsProvisionerReadiness.Ready>()
                    .Which.Provider.Should()
                    .Be("mssql");
                return;
            }

            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Should()
                .Equal(Incompatible(DmsDatabaseTemplate.Minimal), Incompatible(DmsDatabaseTemplate.Sample));
        }

        [Test]
        public void It_refuses_a_create_when_the_server_is_older_than_the_templates()
        {
            if (compatible)
            {
                Assert.Pass("The compatible control records Ready; no create is attempted.");
            }

            _create
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Which.Detail.Category.Should()
                .Be(DmsProvisioningFailureCategory.NotReady);
        }
    }

    private static IDmsTargetEngine ReadyEngine(
        int? serverMajorVersion = null,
        string provider = "postgresql"
    )
    {
        IDmsTargetEngine engine = A.Fake<IDmsTargetEngine>();
        A.CallTo(() => engine.Provider).Returns(provider);
        A.CallTo(() => engine.ValidateEnvironmentAsync(A<CancellationToken>._))
            .Returns(new DmsEngineEnvironment.Ready() { ServerMajorVersion = serverMajorVersion });
        return engine;
    }

    private static ITemplateArtifactCatalog ReadyCatalog(
        params TemplateEngineRequirement[] engineRequirements
    )
    {
        ITemplateArtifactCatalog catalog = A.Fake<ITemplateArtifactCatalog>();
        A.CallTo(() => catalog.ValidateAsync(A<CancellationToken>._))
            .Returns(new TemplateCatalogValidation([], engineRequirements));
        return catalog;
    }
}
