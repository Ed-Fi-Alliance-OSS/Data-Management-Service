// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

public partial class DmsDataStoreProvisioningStartupTests
{
    [TestFixture]
    public class Given_managed_data_store_startup
    {
        public static WebApplicationFactory<Program> Host(
            string? management,
            bool provisioning,
            string target = "postgresql",
            string catalog = "postgresql",
            string identity = "keycloak",
            Dictionary<string, string>? overrides = null,
            bool ready = true,
            Action<IServiceCollection>? configure = null
        )
        {
            var settings = provisioning ? EnabledSettings(target, catalog) : new Dictionary<string, string>();
            settings["AppSettings:Datastore"] = catalog;
            settings["AppSettings:IdentityProvider"] = identity;
            settings["AppSettings:DeployDatabaseOnStartup"] = "false";
            settings["JobSettings:WorkerEnabled"] = "false";
            settings["JobSettings:SchedulerEnabled"] = "false";
            settings["JobSettings:RetentionEnabled"] = "false";
            settings["DatabaseSettings:EncryptionKey"] = "TestEncryptionKey32CharactersLong1";
            settings["IdentitySettings:ClientSecret"] = "ValidClientSecret1234567890!Abcd";
            settings["IdentitySettings:Authority"] = "https://localhost/realms/dms";
            settings["IdentitySettings:TokenCleanup:Enabled"] = "false";
            settings[$"{Section}:Enabled"] = provisioning.ToString();
            if (provisioning && target == "mssql")
            {
                settings[$"{Section}:AdminConnectionString"] =
                    "Server=tcp:127.0.0.1,1;User Id=none;Password=none";
            }
            settings["DataStoreManagement:RuntimeConnectionString"] =
                target == "postgresql"
                    ? "Host=127.0.0.1;Port=1;Username=runtime;Password=test-only"
                    : "Server=tcp:127.0.0.1,1;User Id=runtime;Password=test-only";
            if (management is not null)
            {
                settings["AppSettings:EnableDataStoreManagement"] = management;
            }
            foreach (var pair in overrides ?? [])
            {
                settings[pair.Key] = pair.Value;
            }
            var factory = CreateFactory(
                settings,
                configureServices: services =>
                {
                    services.AddTestAuthentication();
                    var engine = ReadyEngine(provider: target);
                    if (!ready)
                    {
                        A.CallTo(() => engine.ValidateEnvironmentAsync(A<CancellationToken>._))
                            .ThrowsAsync(new InvalidOperationException("test environment unavailable"));
                    }
                    services.AddSingleton(engine);
                    services.AddSingleton(ReadyCatalog());
                    configure?.Invoke(services);
                }
            );
            return factory.WithWebHostBuilder(builder =>
            {
                // Omitted means the normal shipped configuration, without the explicit test opt-out.
                builder.UseEnvironment(management is null ? "Production" : "Test");
                builder.UseDefaultServiceProvider(options =>
                {
                    // Existing Keycloak registration includes an unused OpenIddict token manager
                    // without its repository. Exercise whole-container validation in self-contained mode.
                    options.ValidateOnBuild = identity == "self-contained";
                    options.ValidateScopes = true;
                });
            });
        }

        [Test]
        public void It_refuses_the_normal_unconfigured_host_without_an_operator_flag()
        {
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseEnvironment("Production")
            );
            StartupExceptionFor(host)
                .Should()
                .NotBeNull()
                .And.Subject.ToString()
                .Should()
                .Contain("DmsDataStoreProvisioning:Enabled=true");
        }

        private static IEnumerable<TestCaseData> InvalidSecretBearingEndpoints()
        {
            string[] postgresql =
            [
                "Username=runtime",
                "Host=",
                "Host=db.example,other.example",
                "Host=bad@host",
                "Host=db.example;Port=0",
                "Host=db.example;Port=65536",
                "Host=[not-ipv6]",
                "Host=${secret:host};Port=65536",
                "Host=db.example,${secret:host}",
                "Host=db-${secret:host}:bad",
                "Username=runtime;Port=${secret:port}",
                "Host=bad@${secret:host}",
                "Host=db.example;Port=bad${secret:port}",
                "Host=db.example;Port=65536${secret:port}",
            ];
            string[] mssql =
            [
                "User ID=runtime",
                "Server=",
                "Server=db.example,1433",
                "Server=np:db.example",
                "Server=tcp:bad@host,1433",
                "Server=tcp:db.example,0",
                "Server=tcp:db.example,65536",
                "Server=tcp:db.example,1433;MultiSubnetFailover=true",
                "Server=tcp:db.example;Failover Partner=other.example",
                "Server=tcp:db.example;User Instance=true",
                "Server=tcp:db.example;Application Intent=ReadOnly",
                "Server=tcp:db.example;AttachDBFilename=file.mdf",
                "Server=np:${secret:host}",
                "Server=tcp:${secret:host},bad",
                "Server=tcp:${secret:host}\\instance,1433",
                "Server=${secret:server};MultiSubnetFailover=true",
                "Server=db-${secret:host}",
                "Server=tcp:${secret:host},1433,1444",
                "Server=${secret:scheme}np:db.example",
                "Server=tcp:db.example;MultiSubnetFailover=tr${secret:flag}",
                "Server=tcp:db.example;Application Intent=ReadOnly${secret:intent}",
                "Server=tcp:db.example,65536${secret:port}",
            ];
            foreach (var (provider, cases) in new[] { ("postgresql", postgresql), ("mssql", mssql) })
            {
                for (int index = 0; index < cases.Length; index++)
                {
                    yield return new TestCaseData(
                        provider,
                        cases[index] + ";Password=${secret:runtime}"
                    ).SetName($"It_rejects_literal_routing_with_secrets_{provider}_{index}");
                }
            }
        }

        [TestCaseSource(nameof(InvalidSecretBearingEndpoints))]
        public void It_rejects_literal_routing_with_secrets(string target, string connection)
        {
            var resolver = A.Fake<ISecretResolver>();
            using var host = Host(
                "true",
                true,
                target,
                overrides: new()
                {
                    [$"{Section}:AdminConnectionString"] = MatchingAdmin(target),
                    ["DataStoreManagement:RuntimeConnectionString"] = connection,
                },
                configure: services => services.AddSingleton(resolver)
            );
            StartupExceptionFor(host)
                .Should()
                .NotBeNull()
                .And.Subject.ToString()
                .Should()
                .Contain("DataStoreManagement:RuntimeConnectionString")
                .And.NotContain("${secret:runtime}");
            Fake.GetCalls(resolver).Should().BeEmpty();
        }

        private static IEnumerable<TestCaseData> ValidSecretBearingEndpoints()
        {
            string[] postgresql =
            [
                "Host=db.example",
                "Host=${secret:host};Port=5432",
                "Host=db.example;Port=${secret:port}",
                "Host=db.example;Port=+${secret:prefix}5432",
                "Host=db-${secret:suffix}.example",
                "Host=[${secret:ipv6}]:5432",
                "Host=/var/run/${secret:socket}",
                "Host=@${secret:socket}",
                "Host=@${secret:socket}:directory",
                "Host=${secret:root}/socket",
                "Host=${secret:socketRoot}:directory",
            ];
            string[] mssql =
            [
                "Server=tcp:db.example,1433",
                "Server=${secret:server}",
                "Server=tcp:${secret:host},1433",
                "Server=tcp:db.example,${secret:port}",
                "Server=tcp:db.example,${secret:prefix}1433",
                "Server=tcp:db.example\\${secret:instance}",
                "Server=tcp:db-${secret:suffix}.example,1433",
                "Server=tcp:[${secret:ipv6}],1433",
                "Server=${secret:tcpPrefix}[::1],1433",
                "Server=${secret:scheme}:db.example,1433",
                "Server=tcp:db.example;MultiSubnetFailover=${secret:flag};Application Intent=${secret:intent}",
                "Server=tcp:db.example;Application Intent=ReadOnly;ApplicationIntent=${secret:intent}",
            ];
            foreach (var (provider, cases) in new[] { ("postgresql", postgresql), ("mssql", mssql) })
            {
                for (int index = 0; index < cases.Length; index++)
                {
                    yield return new TestCaseData(
                        provider,
                        cases[index] + ";Password=${secret:runtime}"
                    ).SetName(
                        $"It_accepts_secret_dependent_components_without_resolution_{provider}_{index}"
                    );
                }
            }
        }

        [TestCaseSource(nameof(ValidSecretBearingEndpoints))]
        public void It_accepts_secret_dependent_components_without_resolution(
            string target,
            string connection
        )
        {
            var resolver = A.Fake<ISecretResolver>();
            using var host = Host(
                "true",
                true,
                target,
                overrides: new()
                {
                    [$"{Section}:AdminConnectionString"] = MatchingAdmin(target),
                    ["DataStoreManagement:RuntimeConnectionString"] = connection,
                },
                configure: services => services.AddSingleton(resolver)
            );
            StartupExceptionFor(host).Should().BeNull();
            Fake.GetCalls(resolver).Should().BeEmpty();
        }

        private const string EndpointMismatch =
            "DmsDataStoreProvisioning:AdminConnectionString and DataStoreManagement:RuntimeConnectionString must name the same canonical target endpoint (host and port, or named instance). Credentials and database names may differ.";

        /// <summary>A literal destination the fixture's runtime vectors share; credentials differ.</summary>
        private static string MatchingAdmin(string target) =>
            target == "postgresql"
                ? "Host=db.example;Username=admin;Password=test-only"
                : "Server=tcp:db.example,1433;User ID=admin;Password=test-only";

        private static ServiceProvider StaticServices(
            string target,
            string admin,
            string runtime,
            ISecretResolver resolver
        ) =>
            new ServiceCollection()
                .AddSingleton(new DataStoreManagementOptions())
                .AddSingleton(
                    Options.Create(
                        new DmsDataStoreProvisioningOptions { Enabled = true, AdminConnectionString = admin }
                    )
                )
                .AddSingleton(
                    Options.Create(new ManagedDataStoreRuntimeOptions { RuntimeConnectionString = runtime })
                )
                .AddSingleton(ProtectedDatabaseNames.Create("cms", []))
                .AddSingleton<IManagedDataStoreRuntimeConnectionProvider>(
                    target == "postgresql"
                        ? new PostgresqlManagedRuntimeConnectionProvider()
                        : new MssqlManagedRuntimeConnectionProvider()
                )
                .AddSingleton(resolver)
                .BuildServiceProvider();

        private static IEnumerable<TestCaseData> MismatchedEndpoints()
        {
            (string Target, string Admin, string Runtime)[] cases =
            [
                (
                    "postgresql",
                    "Host=admin-db.example;Username=admin;Password=admin-only",
                    "Host=runtime-db.example;Username=runtime;Password=runtime-only"
                ),
                ("postgresql", "Host=db.example;Port=5432", "Host=db.example;Port=5433"),
                ("postgresql", "Host=db.example", "Host=db.example:5433"),
                // Credential-only references leave literal routing comparable, in either setting.
                (
                    "postgresql",
                    "Host=db.example;Password=${secret:admin}",
                    "Host=other.example;Password=runtime-only"
                ),
                (
                    "postgresql",
                    "Host=db.example;Port=5433;Password=admin-only",
                    "Host=db.example;Password=${secret:runtime}"
                ),
                // A later literal alias replaces an earlier secret host.
                (
                    "postgresql",
                    "Host=db.example",
                    "Server=${secret:host};Host=other.example;Password=${secret:runtime}"
                ),
                // An inline port overrides a secret Port, so the effective port is literal.
                (
                    "postgresql",
                    "Host=db.example:5433",
                    "Host=db.example:5434;Port=${secret:port};Password=${secret:runtime}"
                ),
                // A secret port cannot reconcile different literal hosts, in either setting.
                (
                    "postgresql",
                    "Host=db-a.example",
                    "Host=db-b.example;Port=${secret:port};Password=${secret:runtime}"
                ),
                (
                    "postgresql",
                    "Host=db-a.example;Port=${secret:port};Password=${secret:admin}",
                    "Host=db-b.example;Port=5432"
                ),
                (
                    "postgresql",
                    "Host=db-a.example;Port=${secret:adminPort}",
                    "Server=db-b.example;Port=${secret:runtimePort}"
                ),
                ("postgresql", "Host=db.example", "Host=/var/run/postgresql;Port=${secret:port}"),
                (
                    "mssql",
                    "Server=tcp:admin-db.example,1433;User ID=admin;Password=admin-only",
                    "Server=tcp:runtime-db.example,1433;User ID=runtime;Password=runtime-only"
                ),
                ("mssql", "Server=tcp:db.example,1433", "Server=tcp:db.example,1434"),
                ("mssql", "Server=tcp:db.example", "Server=tcp:db.example,1434"),
                ("mssql", "Server=tcp:db.example\\one", "Server=tcp:db.example\\two"),
                ("mssql", "Server=tcp:db.example\\one", "Server=tcp:db.example,1433"),
                (
                    "mssql",
                    "Server=tcp:db.example,1433;Password=${secret:admin}",
                    "Server=tcp:other.example,1433;Password=runtime-only"
                ),
                (
                    "mssql",
                    "Server=tcp:db.example,1434;Password=admin-only",
                    "Server=tcp:db.example;Password=${secret:runtime}"
                ),
                (
                    "mssql",
                    "Server=tcp:db.example,1433",
                    "Server=${secret:server};Data Source=tcp:other.example,1433;Password=${secret:runtime}"
                ),
                // A secret after the literal port or instance separator cannot change the host.
                (
                    "mssql",
                    "Server=tcp:db-a.example,1433",
                    "Server=tcp:db-b.example,${secret:port};Password=${secret:runtime}"
                ),
                (
                    "mssql",
                    "Server=tcp:db-a.example\\one",
                    "Server=tcp:db-b.example\\${secret:instance};Password=${secret:runtime}"
                ),
                (
                    "mssql",
                    "Server=tcp:db-a.example,${secret:port};Password=${secret:admin}",
                    "Server=tcp:db-b.example,1433"
                ),
            ];
            for (int index = 0; index < cases.Length; index++)
            {
                yield return new TestCaseData(
                    cases[index].Target,
                    cases[index].Admin,
                    cases[index].Runtime
                ).SetName($"It_rejects_a_known_endpoint_mismatch_{cases[index].Target}_{index}");
            }
        }

        [TestCaseSource(nameof(MismatchedEndpoints))]
        public void It_rejects_a_known_endpoint_mismatch(string target, string admin, string runtime)
        {
            var resolver = A.Fake<ISecretResolver>();
            using var services = StaticServices(target, admin, runtime, resolver);
            var validate = () => ManagedDataStoreStartup.Validate(services);
            var failure = validate.Should().Throw<OptionsValidationException>().Which;
            failure.Failures.Should().Equal(EndpointMismatch);
            failure.Message.Should().NotContain(admin).And.NotContain(runtime).And.NotContain("${secret");
            Fake.GetCalls(resolver).Should().BeEmpty();
        }

        private static IEnumerable<TestCaseData> ComparableOrDeferredEndpoints()
        {
            (string Target, string Admin, string Runtime)[] equivalent =
            [
                (
                    "postgresql",
                    "Host=DB.Example;Username=admin;Password=admin-only;Database=postgres",
                    "Server=db.example;Port=5432;Username=runtime;Password=runtime-only;Database=other"
                ),
                ("postgresql", "Host=db.example:5433", "Host=db.example;Port=5433"),
                (
                    "postgresql",
                    "Host=db.example;Password=${secret:admin}",
                    "Host=DB.EXAMPLE;Port=5432;Password=${secret:runtime}"
                ),
                (
                    "postgresql",
                    "Host=db.example:5433",
                    "Host=db.example:5433;Port=${secret:port};Password=${secret:runtime}"
                ),
                (
                    "mssql",
                    "Server=tcp:DB.Example;User ID=admin;Password=admin-only;Initial Catalog=master",
                    "Data Source=TCP:db.example,1433;User ID=runtime;Password=runtime-only;Initial Catalog=other"
                ),
                ("mssql", "Server=tcp:db.example\\School", "Address=tcp:DB.EXAMPLE\\school"),
                (
                    "mssql",
                    "Server=tcp:db.example,1433;Password=${secret:admin}",
                    "Server=tcp:DB.example;Password=${secret:runtime}"
                ),
            ];
            // Secret-dependent routing keeps static syntax checks but defers equality to the operation-time
            // guard. Each admin value differs from the static representative its runtime partner parses to.
            (string Target, string Admin, string Runtime)[] deferred =
            [
                ("postgresql", "Host=db.example", "Host=${secret:host};Password=${secret:runtime}"),
                (
                    "postgresql",
                    "Host=db.example;Port=5433",
                    "Host=db.example;Port=${secret:port};Password=${secret:runtime}"
                ),
                (
                    "postgresql",
                    "Host=other.example",
                    "Host=db.example;Server=${secret:host};Password=${secret:runtime}"
                ),
                ("postgresql", "Host=${secret:host};Password=${secret:admin}", "Host=db.example;Port=5433"),
                (
                    "postgresql",
                    "Host=db.example;Port=${secret:adminPort}",
                    "Host=DB.EXAMPLE;Port=${secret:runtimePort}"
                ),
                // A reference inside a PostgreSQL host can add routing syntax (here an IPv6 address
                // reaching the administrative host), so no part of that host is compared.
                ("postgresql", "Host=fe80::1", "Host=fe80:${secret:rest};Password=${secret:runtime}"),
                (
                    "mssql",
                    "Server=tcp:db.example,1434",
                    "Server=tcp:db.example,${secret:port};Password=${secret:runtime}"
                ),
                (
                    "mssql",
                    "Server=tcp:db.example,${secret:port};Password=${secret:admin}",
                    "Server=tcp:DB.example,1434"
                ),
                (
                    "mssql",
                    "Server=tcp:db.example\\one",
                    "Server=tcp:db.example\\${secret:instance};Password=${secret:runtime}"
                ),
                (
                    "mssql",
                    "Server=tcp:other.example,1433",
                    "Data Source=tcp:db.example,1433;Server=${secret:server};Password=${secret:runtime}"
                ),
                ("mssql", "Server=${secret:server};Password=${secret:admin}", "Server=tcp:db.example,1433"),
            ];
            foreach (var (kind, cases) in new[] { ("equivalent", equivalent), ("deferred", deferred) })
            {
                for (int index = 0; index < cases.Length; index++)
                {
                    yield return new TestCaseData(
                        cases[index].Target,
                        cases[index].Admin,
                        cases[index].Runtime
                    ).SetName($"It_accepts_{kind}_endpoints_{cases[index].Target}_{index}");
                }
            }
        }

        [TestCaseSource(nameof(ComparableOrDeferredEndpoints))]
        public void It_accepts_equivalent_or_secret_dependent_endpoints(
            string target,
            string admin,
            string runtime
        )
        {
            var resolver = A.Fake<ISecretResolver>();
            using var services = StaticServices(target, admin, runtime, resolver);
            ManagedDataStoreStartup.Validate(services);
            Fake.GetCalls(resolver).Should().BeEmpty();
        }

        [Test]
        public void It_refuses_to_start_with_a_known_endpoint_mismatch(
            [Values("postgresql", "mssql")] string target,
            [Values(false, true)] bool secretPort
        )
        {
            var resolver = A.Fake<ISecretResolver>();
            string port = secretPort ? "${secret:port}" : "1";
            using var host = Host(
                "true",
                true,
                target,
                overrides: new()
                {
                    ["DataStoreManagement:RuntimeConnectionString"] =
                        target == "postgresql"
                            ? $"Host=runtime-db.example;Port={port};Username=runtime;Password=test-only"
                            : $"Server=tcp:runtime-db.example,{port};User ID=runtime;Password=test-only",
                },
                configure: services => services.AddSingleton(resolver)
            );
            var error = StartupExceptionFor(host);
            error.Should().NotBeNull();
            error!
                .ToString()
                .Should()
                .Contain(EndpointMismatch)
                .And.NotContain("runtime-db.example")
                .And.NotContain("test-only")
                .And.NotContain("${secret");
            Fake.GetCalls(resolver).Should().BeEmpty();
        }

        [TestCase("postgresql"), TestCase("mssql")]
        public void It_applies_static_endpoint_validation_to_admin_secrets_without_resolving_them(
            string target
        )
        {
            var resolver = A.Fake<ISecretResolver>();
            IManagedDataStoreRuntimeConnectionProvider adapter =
                target == "postgresql"
                    ? new PostgresqlManagedRuntimeConnectionProvider()
                    : new MssqlManagedRuntimeConnectionProvider();
            string valid =
                target == "postgresql"
                    ? "Host=db.example;Password=${secret:admin}"
                    : "Server=tcp:db.example,1433;Password=${secret:admin}";
            var provisioning = new DmsDataStoreProvisioningOptions
            {
                Enabled = true,
                AdminConnectionString = valid,
            };
            using var services = new ServiceCollection()
                .AddSingleton(new DataStoreManagementOptions())
                .AddSingleton(Options.Create(provisioning))
                .AddSingleton(
                    Options.Create(new ManagedDataStoreRuntimeOptions { RuntimeConnectionString = valid })
                )
                .AddSingleton(ProtectedDatabaseNames.Create("cms", []))
                .AddSingleton(adapter)
                .AddSingleton(resolver)
                .BuildServiceProvider();
            ManagedDataStoreStartup.Validate(services);
            provisioning.AdminConnectionString =
                target == "postgresql"
                    ? "Host=db.example,other.example;Password=${secret:admin}"
                    : "Server=np:db.example;Password=${secret:admin}";
            var validate = () => ManagedDataStoreStartup.Validate(services);
            validate
                .Should()
                .Throw<OptionsValidationException>()
                .Which.Message.Should()
                .Contain("DmsDataStoreProvisioning:AdminConnectionString")
                .And.NotContain("${secret:admin}");
            Fake.GetCalls(resolver).Should().BeEmpty();
        }

        [TestCase(true, ""), TestCase(false, "")]
        [TestCase(true, ":directory"), TestCase(false, ":directory")]
        public void It_accepts_secret_bearing_abstract_sockets_in_static_startup_without_resolution(
            bool administrative,
            string suffix
        )
        {
            const string literal = "Host=db.example;Username=test";
            string socket = "Host=@${secret:socket}" + suffix + ";Port=5432;Username=test";
            var resolver = A.Fake<ISecretResolver>();
            IManagedDataStoreRuntimeConnectionProvider adapter =
                new PostgresqlManagedRuntimeConnectionProvider();
            using var services = new ServiceCollection()
                .AddSingleton(new DataStoreManagementOptions())
                .AddSingleton(
                    Options.Create(
                        new DmsDataStoreProvisioningOptions
                        {
                            Enabled = true,
                            AdminConnectionString = administrative ? socket : literal,
                        }
                    )
                )
                .AddSingleton(
                    Options.Create(
                        new ManagedDataStoreRuntimeOptions
                        {
                            RuntimeConnectionString = administrative ? literal : socket,
                        }
                    )
                )
                .AddSingleton(ProtectedDatabaseNames.Create("cms", []))
                .AddSingleton(adapter)
                .AddSingleton(resolver)
                .BuildServiceProvider();

            // Establish the operation-time contract with a supplied literal; never fetch the secret.
            adapter
                .TryParse(
                    socket.Replace("${secret:socket}", "CaseSensitiveSocket", StringComparison.Ordinal),
                    out var endpoint
                )
                .Should()
                .BeTrue();
            endpoint!.Host.Should().Be("@CaseSensitiveSocket" + suffix);
            endpoint.Transport.Should().Be("unix");
            endpoint.Port.Should().Be(5432);
            ManagedDataStoreStartup.Validate(services);
            Fake.GetCalls(resolver).Should().BeEmpty();
        }

        [TestCase("false", false), TestCase("false", true), TestCase("true", true), TestCase(null, true)]
        public void It_applies_the_startup_truth_table(string? management, bool provisioning)
        {
            using var host = Host(management, provisioning);
            StartupExceptionFor(host).Should().BeNull();
            var enabled = management != "false";
            if (management is null)
            {
                host.Services.GetRequiredService<IConfiguration>()["AppSettings:EnableDataStoreManagement"]
                    .Should()
                    .BeNull();
            }
            host.Services.GetRequiredService<JobClaimPolicy>()
                .ExcludesManagedDataStores.Should()
                .Be(!enabled);
            host.Services.GetRequiredService<IOptions<AppSettings>>()
                .Value.EnableDataStoreManagement.Should()
                .Be(enabled);
            var registry = host.Services.GetRequiredService<IJobHandlerRegistry>();
            registry.TryGet(ManagedJobTypes.Create, out var create).Should().BeTrue();
            registry.TryGet(ManagedJobTypes.Delete, out var delete).Should().BeTrue();
            create!.HandlerType.Should().Be(typeof(CreateManagedDataStoreHandler));
            delete!.HandlerType.Should().Be(typeof(DeleteManagedDataStoreHandler));
            create.PayloadVersions.Should().Contain(ManagedJobTypes.PayloadVersion);
            delete.PayloadVersions.Should().Contain(ManagedJobTypes.PayloadVersion);
            var policy = host.Services.GetRequiredService<JobClaimPolicy>();
            policy.Allows("DataStore.RefreshEducationOrganizations").Should().BeTrue();
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            configuration["AppSettings:EnableDataStoreManagement"] = enabled ? "false" : "true";
            ((IConfigurationRoot)configuration).Reload();
            host.Services.GetRequiredService<JobClaimPolicy>().Should().BeSameAs(policy);
            policy.ExcludesManagedDataStores.Should().Be(!enabled);
            if (enabled)
            {
                using var scope = host.Services.CreateScope();
                scope
                    .ServiceProvider.GetRequiredService<CreateManagedDataStoreHandler>()
                    .Should()
                    .NotBeNull();
                scope
                    .ServiceProvider.GetRequiredService<DeleteManagedDataStoreHandler>()
                    .Should()
                    .NotBeNull();
                scope
                    .ServiceProvider.GetRequiredService<ManagedDataStoreCreateAcceptance>()
                    .Should()
                    .NotBeNull();
            }
        }

        [TestCase("true"), TestCase(null)]
        public void It_refuses_enabled_management_without_the_provisioner(string? management)
        {
            using var host = Host(management, false);
            StartupExceptionFor(host)
                .Should()
                .NotBeNull()
                .And.Subject.ToString()
                .Should()
                .Contain("DmsDataStoreProvisioning:Enabled=true");
        }

        [TestCase("DmsDataStoreProvisioning:AdminConnectionString")]
        [TestCase("DmsDataStoreProvisioning:ArtifactDirectory")]
        [TestCase("DmsDataStoreProvisioning:TrustPolicyPath")]
        [TestCase("DataStoreManagement:RuntimeConnectionString")]
        public void It_refuses_missing_static_dependencies(string key)
        {
            using var host = Host("true", true, overrides: new() { [key] = "" });
            StartupExceptionFor(host).Should().NotBeNull().And.Subject.ToString().Should().Contain(key);
        }

        [TestCase("postgresql"), TestCase("mssql")]
        public void It_refuses_a_catalog_without_a_protected_database_name(string catalog)
        {
            using var host = Host(
                "true",
                true,
                catalog: catalog,
                overrides: new()
                {
                    ["DatabaseSettings:DatabaseConnection"] =
                        catalog == "postgresql"
                            ? "Host=localhost;Username=test-only"
                            : "Server=localhost;Integrated Security=true",
                }
            );
            StartupExceptionFor(host)
                .Should()
                .NotBeNull()
                .And.Subject.ToString()
                .Should()
                .Contain("DatabaseSettings:DatabaseConnection");
        }

        [TestCase("postgresql"), TestCase("mssql")]
        public void It_rejects_invalid_runtime_configuration_without_echoing_it(string target)
        {
            const string marker = "private-runtime-marker";
            using var host = Host(
                "true",
                true,
                target,
                overrides: new()
                {
                    ["DataStoreManagement:RuntimeConnectionString"] = $"unrecognized={marker}",
                }
            );
            var error = StartupExceptionFor(host);
            error.Should().NotBeNull();
            error!
                .ToString()
                .Should()
                .Contain("DataStoreManagement:RuntimeConnectionString")
                .And.NotContain(marker);
        }

        [TestCase("postgresql", "postgresql"), TestCase("postgresql", "mssql")]
        [TestCase("mssql", "postgresql"), TestCase("mssql", "mssql")]
        public void It_selects_target_and_catalog_providers_independently(string target, string catalog)
        {
            using var host = Host("true", true, target, catalog);
            StartupExceptionFor(host).Should().BeNull();
            using var scope = host.Services.CreateScope();
            scope
                .ServiceProvider.GetRequiredService<IDataStoreManageRepository>()
                .GetType()
                .FullName.Should()
                .Contain(catalog == "mssql" ? ".Mssql." : ".Postgresql.");
            host.Services.GetRequiredService<IManagedDataStoreRuntimeConnectionProvider>()
                .Provider.Should()
                .Be(target);
            var parser = host.Services.GetRequiredService<ManagedDataStoreEndpointParser>();
            parser(target == "mssql" ? "Server=tcp:DB.EXAMPLE,1433" : "Host=DB.EXAMPLE", out var endpoint)
                .Should()
                .BeTrue();
            endpoint!.Provider.Should().Be(target);
        }

        [TestCase("postgresql"), TestCase("mssql")]
        public void It_validates_keywords_even_when_a_value_is_a_secret_reference(string target)
        {
            var resolver = A.Fake<ISecretResolver>();
            using var host = Host(
                "true",
                true,
                target,
                overrides: new()
                {
                    ["DataStoreManagement:RuntimeConnectionString"] = "UnsupportedKeyword=${secret:runtime}",
                },
                configure: services => services.AddSingleton(resolver)
            );
            StartupExceptionFor(host)
                .Should()
                .NotBeNull()
                .And.Subject.ToString()
                .Should()
                .Contain("DataStoreManagement:RuntimeConnectionString")
                .And.NotContain("${secret:runtime}");
            Fake.GetCalls(resolver).Should().BeEmpty();
        }

        [TestCase("keycloak"), TestCase("self-contained")]
        public async Task It_starts_disabled_without_managed_dependencies_and_maps_all_routes(string identity)
        {
            using var host = Host(
                "false",
                false,
                identity: identity,
                overrides: new()
                {
                    ["DataStoreManagement:RuntimeConnectionString"] = "invalid unused configuration",
                }
            );
            using var client = host.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Role", "cms-client");
            client.DefaultRequestHeaders.Add("X-Test-Scope", "edfi_admin_api/full_access");
            foreach (
                var (method, path) in new[]
                {
                    (HttpMethod.Get, ""),
                    (HttpMethod.Get, "/bad"),
                    (HttpMethod.Post, ""),
                    (HttpMethod.Delete, "/bad"),
                }
            )
            {
                using var request = new HttpRequestMessage(method, "/v3/dataStores/manage" + path);
                if (method == HttpMethod.Post)
                {
                    request.Content = new StringContent("{", Encoding.UTF8, "application/json");
                }
                using var response = await client.SendAsync(request);
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await response.Content.ReadAsStringAsync()).Should().Contain("management is disabled");
            }
            host.Services.GetService<IDmsAdminConnectionSource>().Should().BeNull();
            host.Services.GetService<IManagedDataStoreRuntimeConnectionProvider>().Should().BeNull();
        }

        [TestCase("keycloak"), TestCase("self-contained")]
        public async Task It_keeps_NotReady_latched_and_records_Ready_only_on_a_fresh_host(string identity)
        {
            using (var host = Host("true", true, identity: identity, ready: false))
            {
                using var client = host.CreateClient();
                client.DefaultRequestHeaders.Add("X-Test-Role", "cms-client");
                client.DefaultRequestHeaders.Add("X-Test-Scope", "edfi_admin_api/full_access");
                host.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>()
                    .Current.Should()
                    .BeOfType<DmsProvisionerReadiness.NotReady>();
                var engine = host.Services.GetRequiredService<IDmsTargetEngine>();
                A.CallTo(() => engine.ValidateEnvironmentAsync(A<CancellationToken>._))
                    .Returns(new DmsEngineEnvironment.Ready());
                using var response = await client.PostAsync(
                    "/v3/dataStores/manage",
                    new StringContent(
                        "{\"name\":\"example\",\"databaseTemplate\":\"Minimal\"}",
                        Encoding.UTF8,
                        "application/json"
                    )
                );
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                host.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>()
                    .Current.Should()
                    .BeOfType<DmsProvisionerReadiness.NotReady>();
            }
            using var restarted = Host("true", true, identity: identity);
            StartupExceptionFor(restarted).Should().BeNull();
            restarted
                .Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>()
                .Current.Should()
                .BeOfType<DmsProvisionerReadiness.Ready>();
        }

        [TestCase("postgresql"), TestCase("mssql")]
        public void It_preserves_literal_password_fragments_and_defers_secret_availability(string target)
        {
            string prefix =
                target == "postgresql"
                    ? "Host=db.example;Username=runtime;Password="
                    : "Server=tcp:db.example,1433;User ID=runtime;Password=";
            string admin = MatchingAdmin(target);
            using var literal = Host(
                "true",
                true,
                target,
                overrides: new()
                {
                    [$"{Section}:AdminConnectionString"] = admin,
                    ["DataStoreManagement:RuntimeConnectionString"] = prefix + "${secret:}",
                }
            );
            StartupExceptionFor(literal).Should().BeNull();
            using var missing = Host(
                "true",
                true,
                target,
                overrides: new()
                {
                    [$"{Section}:AdminConnectionString"] = admin,
                    ["DataStoreManagement:RuntimeConnectionString"] = prefix + "${secret:runtime}",
                }
            );
            StartupExceptionFor(missing)
                .Should()
                .NotBeNull()
                .And.Subject.ToString()
                .Should()
                .Contain("ISecretResolver");
            var resolver = A.Fake<ISecretResolver>();
            using var configured = Host(
                "true",
                true,
                target,
                overrides: new()
                {
                    [$"{Section}:AdminConnectionString"] = admin,
                    ["DataStoreManagement:RuntimeConnectionString"] = prefix + "${secret:runtime}",
                },
                configure: services => services.AddSingleton(resolver)
            );
            StartupExceptionFor(configured).Should().BeNull();
            Fake.GetCalls(resolver).Should().BeEmpty("static validation must not resolve runtime secrets");
        }
    }
}
