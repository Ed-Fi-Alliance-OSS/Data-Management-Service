// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// One real Configuration Service boot against the staged fixture plugin, with what it wrote to the
/// diagnostic channel and how host creation ended.
/// </summary>
/// <remarks>
/// <para>
/// Every setting a case depends on is written through UseSetting, which lands in the host's
/// configuration ahead of Program's first line, where the loader reads the plugin root and the
/// allowlist. The fixture's behavior key is always written, empty when a case wants the default, so no
/// case can pick one up from the process environment or from another case.
/// </para>
/// <para>
/// Console.Error is process-wide, so it is redirected only for the length of the boot and restored in a
/// finally block. Every fixture using this is NonParallelizable for the same reason.
/// </para>
/// </remarks>
internal sealed class CmsPluginBoot : IDisposable
{
    internal const string PluginName = "Acme.CmsContributor";
    internal const string SecretLookingKey = "AcmeVault:ClientSecret";
    internal const string SecretLookingValue = "acme-vault-7f3c9e1b-plugin-secret";
    private const string BehaviorKey = "Fixture:Acme.CmsContributor:Behavior";

    /// <summary>Where PluginCmsFixtures.targets publishes the fixture plugin.</summary>
    internal static string StageRoot { get; } = Path.Combine(AppContext.BaseDirectory, "PluginFixtures");

    private CmsPluginBoot(
        WebApplicationFactory<Program> factory,
        Exception? startupException,
        string diagnostics,
        bool clientCreated,
        IReadOnlyList<ServiceDescriptor> auditInputDescriptors
    )
    {
        Factory = factory;
        StartupException = startupException;
        Diagnostics = diagnostics;
        ClientCreated = clientCreated;
        AuditInputDescriptors = auditInputDescriptors;
    }

    internal WebApplicationFactory<Program> Factory { get; }

    /// <summary>What host creation threw, or null when the host started.</summary>
    internal Exception? StartupException { get; }

    /// <summary>Everything written to Console.Error while the host was being created.</summary>
    internal string Diagnostics { get; }

    /// <summary>Whether a client existed at any point, which is the precondition for any request.</summary>
    internal bool ClientCreated { get; }

    /// <summary>
    /// The audit input registrations on the collection as Program left it, in registration order.
    /// Empty when host creation failed before the collection was complete.
    /// </summary>
    internal IReadOnlyList<ServiceDescriptor> AuditInputDescriptors { get; }

    internal static CmsPluginBoot Run(string pluginRoot, string allowed, string behavior = "")
    {
        List<ServiceDescriptor> auditInputDescriptors = [];

        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("Plugins:Directory", pluginRoot);
                builder.UseSetting("Plugins:Allowed", allowed);
                builder.UseSetting(BehaviorKey, behavior);
                // Test services are applied after Program's own registrations, so this sees the
                // collection as AddServices left it.
                builder.ConfigureTestServices(services =>
                    auditInputDescriptors.AddRange(
                        services.Where(descriptor => descriptor.ServiceType == typeof(PluginAuditInput))
                    )
                );
            }
        );

        TextWriter originalError = Console.Error;
        using StringWriter error = new();
        Console.SetError(error);

        Exception? startupException = null;
        bool clientCreated = false;

        try
        {
            // WebApplicationFactory defers the entry point until the server is first needed, so this is
            // where host creation happens, and a failure here means no client ever existed.
            using HttpClient client = factory.CreateClient();
            clientCreated = true;
        }
        catch (Exception exception)
        {
            startupException = exception;
        }
        finally
        {
            Console.SetError(originalError);
        }

        return new CmsPluginBoot(
            factory,
            startupException,
            error.ToString(),
            clientCreated,
            auditInputDescriptors
        );
    }

    /// <summary>
    /// The first exception of type <typeparamref name="T"/> in the chain. The test host wraps what the
    /// entry point throws before RunAsync, so the chain is walked rather than the outermost type
    /// asserted.
    /// </summary>
    internal T? Find<T>()
        where T : Exception => Find<T>(StartupException);

    private static T? Find<T>(Exception? exception)
        where T : Exception =>
        exception switch
        {
            null => null,
            T match => match,
            AggregateException aggregate => aggregate
                .InnerExceptions.Select(Find<T>)
                .FirstOrDefault(found => found is not null),
            _ => Find<T>(exception.InnerException),
        };

    /// <summary>
    /// Every message in the startup exception chain, for asserting that nothing secret travelled in one.
    /// </summary>
    internal string ExceptionText()
    {
        List<string> messages = [];
        for (Exception? current = StartupException; current is not null; current = current.InnerException)
        {
            messages.Add(current.ToString());
        }
        return string.Join('\n', messages);
    }

    public void Dispose() => Factory.Dispose();
}

[TestFixture]
[NonParallelizable]
public class Given_a_boot_whose_allowlist_misspells_the_staged_plugin
{
    private const string Misspelled = "Acme.CmsContributr";
    private CmsPluginBoot _boot = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _boot = CmsPluginBoot.Run(CmsPluginBoot.StageRoot, Misspelled);

    [OneTimeTearDown]
    public void OneTimeTearDown() => _boot.Dispose();

    [Test]
    public void It_fails_host_creation_before_any_client_exists()
    {
        _boot.StartupException.Should().NotBeNull();
        _boot.ClientCreated.Should().BeFalse();
    }

    [Test]
    public void It_fails_with_the_missing_plugin_directory_fatal_naming_the_entry()
    {
        PluginLoadException failure = _boot.Find<PluginLoadException>()!;

        failure.Should().NotBeNull();
        failure.Reason.Should().Be(PluginLoadFailure.PluginDirectoryMissing);
        failure.PluginName.Should().Be(Misspelled);
    }

    [Test]
    public void It_writes_the_fatal_to_the_diagnostic_channel()
    {
        _boot
            .Diagnostics.Should()
            .Contain($"plugin '{Misspelled}' failed: plugin '{Misspelled}' is allowlisted but '")
            .And.Contain(Path.Combine(CmsPluginBoot.StageRoot, Misspelled))
            .And.Contain("' does not exist.");
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_boot_that_allowlists_a_plugin_on_a_missing_root
{
    private readonly string _missingRoot = Path.Combine(
        Path.GetTempPath(),
        "cms-plugin-root-" + Guid.NewGuid().ToString("N")
    );
    private CmsPluginBoot _boot = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _boot = CmsPluginBoot.Run(_missingRoot, CmsPluginBoot.PluginName);

    [OneTimeTearDown]
    public void OneTimeTearDown() => _boot.Dispose();

    [Test]
    public void It_fails_host_creation_before_any_client_exists()
    {
        _boot.StartupException.Should().NotBeNull();
        _boot.ClientCreated.Should().BeFalse();
    }

    [Test]
    public void It_fails_with_the_missing_root_fatal()
    {
        _boot.Find<PluginLoadException>()!.Reason.Should().Be(PluginLoadFailure.PluginRootMissing);
    }

    [Test]
    public void It_writes_the_fatal_to_the_diagnostic_channel()
    {
        _boot
            .Diagnostics.Should()
            .Contain(
                $"plugins: the plugin root '{_missingRoot}' does not exist, and Plugins:Allowed asks for 1 plugin(s)."
            );
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_boot_whose_plugin_configuration_hook_throws
{
    private CmsPluginBoot _boot = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() =>
        _boot = CmsPluginBoot.Run(
            CmsPluginBoot.StageRoot,
            CmsPluginBoot.PluginName,
            "throwFromConfiguration"
        );

    [OneTimeTearDown]
    public void OneTimeTearDown() => _boot.Dispose();

    [Test]
    public void It_fails_host_creation_before_any_client_exists()
    {
        _boot.StartupException.Should().NotBeNull();
        _boot.ClientCreated.Should().BeFalse();
    }

    [Test]
    public void It_fails_naming_the_plugin_and_the_configuration_phase()
    {
        PluginCompositionException failure = _boot.Find<PluginCompositionException>()!;

        failure.Should().NotBeNull();
        failure.Reason.Should().Be(PluginCompositionFailure.ContributeConfigurationThrew);
        failure.PluginName.Should().Be(CmsPluginBoot.PluginName);
    }

    [Test]
    public void It_writes_the_plugin_the_phase_and_the_failure_to_the_diagnostic_channel()
    {
        _boot
            .Diagnostics.Should()
            .Contain(
                $"plugin configuration refused: plugin '{CmsPluginBoot.PluginName}' threw from "
                    + "ContributeConfiguration, the configuration contribution phase: "
                    + "System.InvalidOperationException: the fixture configuration hook failed on purpose"
            );
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_boot_whose_plugin_service_hook_throws
{
    private CmsPluginBoot _boot = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() =>
        _boot = CmsPluginBoot.Run(CmsPluginBoot.StageRoot, CmsPluginBoot.PluginName, "throwFromServices");

    [OneTimeTearDown]
    public void OneTimeTearDown() => _boot.Dispose();

    [Test]
    public void It_fails_host_creation_before_any_client_exists()
    {
        _boot.StartupException.Should().NotBeNull();
        _boot.ClientCreated.Should().BeFalse();
    }

    [Test]
    public void It_fails_naming_the_plugin_and_the_service_phase()
    {
        PluginCompositionException failure = _boot.Find<PluginCompositionException>()!;

        failure.Should().NotBeNull();
        failure.Reason.Should().Be(PluginCompositionFailure.ContributeServicesThrew);
        failure.PluginName.Should().Be(CmsPluginBoot.PluginName);
    }

    [Test]
    public void It_writes_the_plugin_the_phase_and_the_failure_to_the_diagnostic_channel()
    {
        _boot
            .Diagnostics.Should()
            .Contain(
                $"plugin composition refused: plugin '{CmsPluginBoot.PluginName}' threw from "
                    + "ContributeServices, the service composition phase: "
                    + "System.InvalidOperationException: the fixture service hook failed on purpose"
            );
    }

    [Test]
    public void It_does_not_write_the_value_its_configuration_phase_supplied()
    {
        // The configuration phase succeeded here before the service phase failed, so the secret-looking
        // value was in the host's configuration when the failure was reported. Asserted as booleans so
        // a failure does not print the captured text.
        string captured = _boot.Diagnostics + _boot.ExceptionText();

        captured
            .Contains(CmsPluginBoot.SecretLookingKey, StringComparison.Ordinal)
            .Should()
            .BeFalse("the key must not be written");
        captured
            .Contains(CmsPluginBoot.SecretLookingValue, StringComparison.Ordinal)
            .Should()
            .BeFalse("the value must not be written");
    }
}

/// <summary>
/// A plugin that registers an audit input of its own, beside a valid declared contract, before the
/// host registers its own.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_a_boot_whose_plugin_registers_its_own_audit_input
{
    private CmsPluginBoot _boot = null!;
    private PluginAuditInput _resolved = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _boot = CmsPluginBoot.Run(CmsPluginBoot.StageRoot, CmsPluginBoot.PluginName, "decoyAudit");
        _resolved = _boot.Factory.Services.GetRequiredService<PluginAuditInput>();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _boot.Dispose();

    [Test]
    public void It_starts_the_host()
    {
        _boot.StartupException.Should().BeNull();
        _boot.ClientCreated.Should().BeTrue();
    }

    [Test]
    public void It_carries_the_plugins_registration_and_the_hosts_in_that_order()
    {
        _boot.AuditInputDescriptors.Should().HaveCount(2);
        ((PluginAuditInput)_boot.AuditInputDescriptors[0].ImplementationInstance!)
            .Registry.Entries.Should()
            .BeEmpty("the first registration is the plugin's decoy");
        _boot.AuditInputDescriptors[1].ImplementationInstance.Should().BeSameAs(_resolved);
    }

    [Test]
    public void It_resolves_the_host_produced_audit_input()
    {
        _resolved.Registry.Should().BeSameAs(CmsPluginContracts.Registry);
    }

    [Test]
    public void It_resolves_an_audit_input_that_attributes_the_plugins_contribution()
    {
        PluginContributionRecord record = _resolved.Records.Should().ContainSingle().Subject;

        record.PluginName.Should().Be(CmsPluginBoot.PluginName);
        record
            .Additions.Select(descriptor => descriptor.ServiceType)
            .Should()
            .Contain(typeof(PluginAuditInput));
        record
            .Additions.Select(descriptor => descriptor.ServiceType)
            .Should()
            .Contain(typeof(ISecretResolver));
    }

    [Test]
    public void It_still_resolves_the_plugins_secret_resolver()
    {
        _boot
            .Factory.Services.GetRequiredService<ISecretResolver>()
            .GetType()
            .FullName.Should()
            .Be("Acme.CmsContributor.CmsContributorSecretResolver");
    }
}
