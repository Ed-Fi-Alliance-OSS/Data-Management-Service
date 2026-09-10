// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Reflection.Emit;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// A type in an assembly of the test's own naming.
/// </summary>
/// <remarks>
/// The host-owned rule reads one thing, the declaring assembly's simple name, and the cases worth
/// asserting are the near misses: a name that stops one character short of the prefix, one that
/// continues past it, one that differs only in case, and one that carries the prefix somewhere other
/// than the start. No collection of real fixture assemblies would cover those without existing
/// solely to be misnamed, and an emitted assembly carries a real name that
/// <see cref="Assembly.GetName" /> reports like any other. A real host-prefixed fixture assembly
/// exercises the rule end to end through the wrapper, where the descriptors are real too.
/// </remarks>
internal static class SyntheticAssembly
{
    internal static Type TypeInAssemblyNamed(string assemblyName)
    {
        // Built by property rather than parsed. AssemblyName(string) is the display-name parser, and
        // HostAssemblies.cs records what that costs; a simple name is a name here for the same
        // reason.
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName { Name = assemblyName },
            AssemblyBuilderAccess.RunAndCollect
        );

        TypeBuilder type = assembly
            .DefineDynamicModule(assemblyName)
            .DefineType(
                "ISyntheticService",
                TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract
            );

        return type.CreateType()
            ?? throw new InvalidOperationException($"the synthetic type in '{assemblyName}' was not created");
    }
}

[TestFixture]
public class Given_a_service_type_declared_in_an_assembly_carrying_a_host_prefix
{
    [TestCase("EdFi.DataManagementService.CustomValidation")]
    [TestCase("EdFi.DataManagementService.Core.External")]
    [TestCase("EdFi.DataManagementService.Backend.External")]
    [TestCase("EdFi.DmsConfigurationService.Backend")]
    [TestCase("EdFi.DmsConfigurationService.Frontend.AspNetCore")]
    public void It_is_host_owned(string assemblyName)
    {
        HostOwnedServiceTypes
            .IsHostOwned(SyntheticAssembly.TypeInAssemblyNamed(assemblyName))
            .Should()
            .BeTrue();
    }
}

[TestFixture]
public class Given_a_service_type_declared_in_an_assembly_that_only_resembles_a_host_prefix
{
    /// <summary>
    /// Each of these is a way the rule could be got wrong: a name equal to the prefix without its
    /// separator, one that continues past the separator's position, one differing only in case, and
    /// one carrying the prefix somewhere other than the start.
    /// </summary>
    [TestCase("EdFi.DataManagementService")]
    [TestCase("EdFi.DataManagementServiceX.Acme")]
    [TestCase("edfi.datamanagementservice.acme")]
    [TestCase("EDFI.DATAMANAGEMENTSERVICE.ACME")]
    [TestCase("Acme.EdFi.DataManagementService.Thing")]
    [TestCase("EdFi.DmsConfigurationServiceX.Acme")]
    [TestCase("EdFi.Api.Plugins")]
    [TestCase("Acme.Good")]
    public void It_is_not_host_owned(string assemblyName)
    {
        HostOwnedServiceTypes
            .IsHostOwned(SyntheticAssembly.TypeInAssemblyNamed(assemblyName))
            .Should()
            .BeFalse();
    }
}

[TestFixture]
public class Given_real_service_types_the_rule_must_admit
{
    [Test]
    public void It_does_not_treat_the_plugin_contract_as_host_owned()
    {
        HostOwnedServiceTypes.IsHostOwned(typeof(EdFiApiPlugin)).Should().BeFalse();
    }

    [Test]
    public void It_does_not_treat_the_loader_as_host_owned()
    {
        HostOwnedServiceTypes.IsHostOwned(typeof(PluginContractRegistry)).Should().BeFalse();
    }

    [TestCase(typeof(int))]
    [TestCase(typeof(ILoggerProvider))]
    [TestCase(typeof(IServiceProvider))]
    public void It_does_not_treat_framework_types_as_host_owned(Type serviceType)
    {
        HostOwnedServiceTypes.IsHostOwned(serviceType).Should().BeFalse();
    }

    /// <summary>
    /// The rule reads the declaring assembly of the service type, not of its type arguments. A
    /// framework collection closed over a host-owned type is a framework service type, which is what
    /// keeps this predicate from reaching registrations the design deliberately permits.
    /// </summary>
    [Test]
    public void It_reads_the_declaring_assembly_rather_than_the_type_arguments()
    {
        Type hostOwned = SyntheticAssembly.TypeInAssemblyNamed("EdFi.DataManagementService.Synthetic");

        HostOwnedServiceTypes.IsHostOwned(hostOwned).Should().BeTrue();
        HostOwnedServiceTypes
            .IsHostOwned(typeof(IEnumerable<>).MakeGenericType(hostOwned))
            .Should()
            .BeFalse();
    }
}

[TestFixture]
public class Given_the_logging_pipeline_service_types
{
    [TestCase(typeof(ILoggerProvider))]
    [TestCase(typeof(ILoggerFactory))]
    [TestCase(typeof(ILogger))]
    [TestCase(typeof(ILogger<>))]
    public void It_recognises_every_name_in_the_set(Type serviceType)
    {
        HostOwnedServiceTypes.IsLoggingPipeline(serviceType).Should().BeTrue();
    }

    [Test]
    public void It_recognises_a_closed_generic_logger()
    {
        HostOwnedServiceTypes.IsLoggingPipeline(typeof(ILogger<PluginContractRegistry>)).Should().BeTrue();
    }

    /// <summary>
    /// The set is four service type names, not an assembly or a namespace rule. IExternalScopeProvider
    /// is declared in the same assembly and the same namespace as all four and is not in it, which is
    /// what that distinction means in practice.
    /// </summary>
    [TestCase(typeof(IExternalScopeProvider))]
    [TestCase(typeof(LogLevel))]
    [TestCase(typeof(int))]
    [TestCase(typeof(EdFiApiPlugin))]
    public void It_recognises_nothing_else(Type serviceType)
    {
        HostOwnedServiceTypes.IsLoggingPipeline(serviceType).Should().BeFalse();
    }
}

/// <summary>
/// The narrower set the host reserves against a plugin's own registration, which is the removal set
/// minus the one member that composes rather than displaces.
/// </summary>
[TestFixture]
public class Given_the_logging_service_types_the_host_reserves
{
    [TestCase(typeof(ILoggerFactory))]
    [TestCase(typeof(ILogger))]
    [TestCase(typeof(ILogger<>))]
    public void It_reserves_every_singly_resolved_logging_service(Type serviceType)
    {
        HostOwnedServiceTypes.IsReservedLoggingService(serviceType).Should().BeTrue();
    }

    /// <summary>
    /// The narrower shape of the same displacement: a closed logger beats the host's open generic for
    /// that one category.
    /// </summary>
    [Test]
    public void It_reserves_a_closed_generic_logger()
    {
        HostOwnedServiceTypes
            .IsReservedLoggingService(typeof(ILogger<PluginContractRegistry>))
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// The one member of the removal set that is not reserved, and the difference is the whole point:
    /// providers are enumerated, so a plugin's sink composes with the host's rather than replacing it.
    /// </summary>
    [Test]
    public void It_does_not_reserve_the_provider_that_the_removal_rule_protects()
    {
        HostOwnedServiceTypes.IsLoggingPipeline(typeof(ILoggerProvider)).Should().BeTrue();
        HostOwnedServiceTypes.IsReservedLoggingService(typeof(ILoggerProvider)).Should().BeFalse();
    }

    [TestCase(typeof(IExternalScopeProvider))]
    [TestCase(typeof(LogLevel))]
    [TestCase(typeof(int))]
    [TestCase(typeof(EdFiApiPlugin))]
    public void It_reserves_nothing_else(Type serviceType)
    {
        HostOwnedServiceTypes.IsReservedLoggingService(serviceType).Should().BeFalse();
    }
}
