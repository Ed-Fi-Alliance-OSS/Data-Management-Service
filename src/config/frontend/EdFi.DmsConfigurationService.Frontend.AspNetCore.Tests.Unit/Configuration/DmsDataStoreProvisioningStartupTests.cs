// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;
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
/// static configuration stops the host naming the setting; enabling it for either target provider under
/// either catalog datastore registers the unavailable adapter and records <c>NotReady(AdapterUnavailable)</c>;
/// and readiness can never be <c>Ready</c> in this build, even when an engine reports its environment ready.
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

    [TestFixture]
    public class Given_the_shipped_settings_at_startup
    {
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

        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private DmsProvisionerReadiness _readiness = null!;
        private IDmsDataStoreProvisioner _provisioner = null!;
        private IDmsTargetEngine? _engine;
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
        public void It_registers_no_engine_and_no_validation()
        {
            _engine.Should().BeNull();
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

    [TestFixture("postgresql", "postgresql")]
    [TestFixture("postgresql", "mssql")]
    [TestFixture("mssql", "postgresql")]
    [TestFixture("mssql", "mssql")]
    public class Given_enabled_for_a_target_provider_under_a_catalog_datastore(
        string provider,
        string datastore
    )
    {
        private CapturingLoggerProvider _logs = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private IDmsTargetEngine _engine = null!;
        private DmsProvisionerReadiness _readiness = null!;
        private DmsArtifactResolution _resolution = null!;
        private CapturedLog[] _criticalEntries = [];

        [SetUp]
        public async Task Act()
        {
            _logs = new CapturingLoggerProvider();
            _factory = CreateFactory(EnabledSettings(provider, datastore), _logs);
            _exception = StartupExceptionFor(_factory);
            _engine = _factory.Services.GetRequiredService<IDmsTargetEngine>();
            _readiness = _factory.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>().Current;
            _resolution = await _factory
                .Services.GetRequiredService<IDmsDataStoreProvisioner>()
                .ResolveArtifactAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);
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

        [Test]
        public void It_starts_and_keeps_running() => _exception.Should().BeNull();

        [Test]
        public void It_registers_the_unavailable_adapter_for_the_target_provider_not_the_catalog_datastore() =>
            _engine.Should().BeOfType<UnavailableDmsTargetEngine>().Which.Provider.Should().Be(provider);

        [Test]
        public void It_records_not_ready_because_the_adapter_is_unavailable() =>
            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdapterUnavailable,
                        UnavailableDmsTargetEngine.Probe
                    )
                );

        [Test]
        public void It_refuses_calls_as_adapter_unavailable() =>
            _resolution
                .Should()
                .BeOfType<DmsArtifactResolution.Failure>()
                .Which.Detail.Should()
                .Match<DmsProvisioningFailure>(failure =>
                    failure.Category == DmsProvisioningFailureCategory.NotReady
                    && failure.Code == DmsProvisioningFailureCode.AdapterUnavailable
                );

        [Test]
        public void It_logs_the_reason_at_critical_naming_the_target_provider()
        {
            CapturedLog entry = _criticalEntries.Should().ContainSingle().Subject;
            entry.State["Code"].Should().Be(DmsProvisioningFailureCode.AdapterUnavailable);
            entry.State["Provider"].Should().Be(provider);
            entry.State["Probe"].Should().Be(UnavailableDmsTargetEngine.Probe);
        }
    }

    [TestFixture]
    public class Given_an_engine_that_reports_its_environment_ready
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
                .Returns(new DmsEngineEnvironment.Ready());

            _factory = CreateFactory(
                EnabledSettings("postgresql", "postgresql"),
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
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _exception.Should().BeNull();

        [Test]
        public void It_still_records_not_ready_because_the_other_probes_do_not_exist_yet() =>
            _readiness
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        DmsDataStoreProvisioningStartup.ProbesIncompleteProbe
                    )
                );
    }
}
