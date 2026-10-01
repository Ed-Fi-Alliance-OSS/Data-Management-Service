// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// The Configuration Service's singleton-and-unkeyed rule for its plugin contracts, over real
/// descriptors attributed to a named plugin. Attribution itself is produced by the plugin hosting
/// assembly and is exercised end to end by the fixture-plugin boot tests.
/// </summary>
public class PluginContractShapeCheckTests
{
    private static IReadOnlyList<string> CheckOne(string pluginName, ServiceDescriptor descriptor) =>
        PluginContractShapeCheck.Check(CmsPluginContracts.Registry, [(pluginName, descriptor)]);

    [TestFixture]
    public class Given_a_plugin_registering_the_secret_resolver_as_scoped
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup() =>
            _findings = CheckOne(
                "Vault",
                new ServiceDescriptor(typeof(ISecretResolver), _ => null!, ServiceLifetime.Scoped)
            );

        [Test]
        public void It_reports_one_problem_naming_the_plugin_the_contract_and_the_lifetime()
        {
            _findings
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("plugin 'Vault'")
                .And.Contain("'EdFi.DmsConfigurationService.Secrets.ISecretResolver'")
                .And.Contain("as Scoped");
        }
    }

    [TestFixture]
    public class Given_a_plugin_registering_the_client_secret_hasher_as_transient
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup() =>
            _findings = CheckOne(
                "Hasher",
                new ServiceDescriptor(typeof(IClientSecretHasher), _ => null!, ServiceLifetime.Transient)
            );

        [Test]
        public void It_reports_one_problem_naming_the_plugin_the_contract_and_the_lifetime()
        {
            _findings
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("plugin 'Hasher'")
                .And.Contain("'EdFi.DmsConfigurationService.Secrets.IClientSecretHasher'")
                .And.Contain("as Transient");
        }
    }

    [TestFixture]
    public class Given_a_plugin_registering_the_secret_resolver_as_a_keyed_singleton
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup() =>
            _findings = CheckOne(
                "Vault",
                new ServiceDescriptor(
                    typeof(ISecretResolver),
                    "primary\r\nforged",
                    (_, _) => null!,
                    ServiceLifetime.Singleton
                )
            );

        [Test]
        public void It_reports_one_problem_naming_the_plugin_and_the_key_with_control_characters_removed()
        {
            _findings
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("plugin 'Vault'")
                .And.Contain("under the service key 'primaryforged'");
        }
    }

    [TestFixture]
    public class Given_a_plugin_registering_the_client_secret_hasher_as_a_keyed_scoped_service
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup() =>
            _findings = CheckOne(
                "Hasher",
                new ServiceDescriptor(typeof(IClientSecretHasher), 7, (_, _) => null!, ServiceLifetime.Scoped)
            );

        [Test]
        public void It_reports_the_key_and_the_lifetime_as_two_problems()
        {
            _findings.Should().HaveCount(2);
            _findings.Should().ContainSingle(finding => finding.Contains("under the service key 7."));
            _findings.Should().ContainSingle(finding => finding.Contains("as Scoped"));
        }
    }

    [TestFixture]
    public class Given_a_plugin_registering_a_contract_under_a_key_of_its_own_type
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup() =>
            _findings = CheckOne(
                "Vault",
                new ServiceDescriptor(
                    typeof(ISecretResolver),
                    new KeyWithUncontrolledText(),
                    (_, _) => null!,
                    ServiceLifetime.Singleton
                )
            );

        [Test]
        public void It_names_the_key_by_its_type()
        {
            _findings
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain($"of type '{typeof(KeyWithUncontrolledText).FullName}'");
        }

        [Test]
        public void It_does_not_render_the_key_itself()
        {
            _findings.Should().ContainSingle().Which.Should().NotContain(KeyWithUncontrolledText.Text);
        }

        private sealed class KeyWithUncontrolledText
        {
            public const string Text = "secret-looking-key-text";

            public override string ToString() => Text;
        }
    }

    [TestFixture]
    public class Given_a_plugin_registering_both_contracts_as_unkeyed_singletons_and_its_own_scoped_service
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup() =>
            _findings = PluginContractShapeCheck.Check(
                CmsPluginContracts.Registry,
                [
                    (
                        "Vault",
                        new ServiceDescriptor(typeof(ISecretResolver), _ => null!, ServiceLifetime.Singleton)
                    ),
                    (
                        "Vault",
                        new ServiceDescriptor(
                            typeof(IClientSecretHasher),
                            _ => null!,
                            ServiceLifetime.Singleton
                        )
                    ),
                    ("Vault", new ServiceDescriptor(typeof(IDisposable), _ => null!, ServiceLifetime.Scoped)),
                ]
            );

        [Test]
        public void It_reports_no_problem()
        {
            _findings.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_plugin_registering_the_client_secret_hasher_collection_type
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup() =>
            _findings = CheckOne(
                "Hasher",
                new ServiceDescriptor(
                    typeof(IEnumerable<IClientSecretHasher>),
                    _ => Array.Empty<IClientSecretHasher>(),
                    ServiceLifetime.Singleton
                )
            );

        [Test]
        public void It_reports_one_problem_naming_the_plugin_and_the_collection_type()
        {
            _findings
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("plugin 'Hasher'")
                .And.Contain("'IEnumerable<EdFi.DmsConfigurationService.Secrets.IClientSecretHasher>'");
        }
    }

    [TestFixture]
    public class Given_a_plugin_registering_a_collection_of_its_own_type
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup() =>
            _findings = CheckOne(
                "Hasher",
                new ServiceDescriptor(
                    typeof(IEnumerable<IDisposable>),
                    _ => Array.Empty<IDisposable>(),
                    ServiceLifetime.Singleton
                )
            );

        [Test]
        public void It_reports_no_problem()
        {
            _findings.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_plugin_registering_the_client_secret_hasher_collection_type_under_a_key
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup() =>
            _findings = CheckOne(
                "Hasher",
                new ServiceDescriptor(
                    typeof(IEnumerable<IClientSecretHasher>),
                    "primary",
                    (_, _) => Array.Empty<IClientSecretHasher>(),
                    ServiceLifetime.Singleton
                )
            );

        [Test]
        public void It_reports_no_problem_because_the_unkeyed_collection_is_untouched()
        {
            _findings.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_plugin_factory_for_the_client_secret_hasher_that_returns_null
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup()
        {
            ServiceDescriptor descriptor = ServiceDescriptor.Singleton<IClientSecretHasher>(_ => null!);
            IServiceCollection services = new ServiceCollection();
            services.Add(descriptor);
            using ServiceProvider provider = services.BuildServiceProvider();

            _findings = PluginContractShapeCheck.CheckResolvedInstances(
                CmsPluginContracts.Registry,
                [("Hasher\nforged", descriptor)],
                provider
            );
        }

        [Test]
        public void It_reports_one_problem_naming_the_contract_and_the_contributing_plugin()
        {
            _findings
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("'EdFi.DmsConfigurationService.Secrets.IClientSecretHasher' resolved to null")
                .And.Contain("plugin(s) 'Hasherforged'");
        }
    }

    [TestFixture]
    public class Given_a_null_hasher_factory_hidden_behind_a_collection_registration
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup()
        {
            // The collection yields a real instance, so only a single resolve sees the null that
            // host code would receive.
            ServiceDescriptor descriptor = ServiceDescriptor.Singleton<IClientSecretHasher>(_ => null!);
            IServiceCollection services = new ServiceCollection();
            services.Add(descriptor);
            services.AddSingleton<IEnumerable<IClientSecretHasher>>([A.Fake<IClientSecretHasher>()]);
            using ServiceProvider provider = services.BuildServiceProvider();

            _findings = PluginContractShapeCheck.CheckResolvedInstances(
                CmsPluginContracts.Registry,
                [("Hasher", descriptor)],
                provider
            );
        }

        [Test]
        public void It_reports_the_null_the_single_resolve_returns()
        {
            _findings
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("'EdFi.DmsConfigurationService.Secrets.IClientSecretHasher' resolved to null");
        }
    }

    [TestFixture]
    public class Given_no_registration_of_the_secret_resolver
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup()
        {
            using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();

            _findings = PluginContractShapeCheck.CheckResolvedInstances(
                CmsPluginContracts.Registry,
                [],
                provider
            );
        }

        [Test]
        public void It_reports_no_problem()
        {
            _findings.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_plugin_factory_for_the_client_secret_hasher_that_returns_an_instance
    {
        private IReadOnlyList<string> _findings = null!;

        [SetUp]
        public void Setup()
        {
            ServiceDescriptor descriptor = ServiceDescriptor.Singleton(_ => A.Fake<IClientSecretHasher>());
            IServiceCollection services = new ServiceCollection();
            services.Add(descriptor);
            using ServiceProvider provider = services.BuildServiceProvider();

            _findings = PluginContractShapeCheck.CheckResolvedInstances(
                CmsPluginContracts.Registry,
                [("Hasher", descriptor)],
                provider
            );
        }

        [Test]
        public void It_reports_no_problem()
        {
            _findings.Should().BeEmpty();
        }
    }
}
