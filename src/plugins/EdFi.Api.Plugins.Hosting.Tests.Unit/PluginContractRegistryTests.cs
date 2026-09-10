// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Contract types for the registry cases. They are declared here rather than in a fixture assembly
/// because what these cases read is the declaring assembly's name, and this assembly has one.
/// </summary>
/// <remarks>
/// The assertion that the derived name is an assembly name rather than the package id its project
/// packs under cannot be made here, because no contract this assembly can see is packed under a
/// different id. It is made against DMS's own registry in the frontend's host tests, where
/// ICustomResourceValidator is declared in the assembly EdFi.DataManagementService.CustomValidation
/// and packed as EdFi.Api.CustomValidation.
/// </remarks>
public interface IRegistryFixtureContract { }

public interface IRegistryFixtureSecondContract { }

public interface IRegistryFixtureOpenContract<T>
{
    // The parameter is used so that this reads as a contract rather than as a marker; an unused type
    // parameter is a build error under this repository's analyzer set.
    void Validate(T candidate);
}

[TestFixture]
public class Given_a_registry_with_no_entries
{
    private PluginContractRegistry _registry = null!;

    [SetUp]
    public void Setup() => _registry = new PluginContractRegistry([]);

    [Test]
    public void It_still_carries_the_assembly_that_declares_the_plugin_base_class()
    {
        // The literal rather than typeof(EdFiApiPlugin).Assembly.GetName().Name, so that renaming
        // the contract assembly fails this test instead of silently redefining what the skew
        // preflight checks.
        _registry.ContractAssemblyNames.Should().Equal("EdFi.Api.Plugins");
    }

    [Test]
    public void It_carries_no_entries()
    {
        _registry.Entries.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_a_registry_with_one_contract_entry
{
    private PluginContractRegistry _registry = null!;

    [SetUp]
    public void Setup() =>
        _registry = new PluginContractRegistry([
            new PluginContractEntry(typeof(IRegistryFixtureContract), Cardinality.FanIn),
        ]);

    [Test]
    public void It_adds_the_declaring_assembly_of_that_contract_and_nothing_else()
    {
        _registry
            .ContractAssemblyNames.Should()
            .Equal("EdFi.Api.Plugins", "EdFi.Api.Plugins.Hosting.Tests.Unit");
    }

    [Test]
    public void It_keeps_the_entry_with_its_cardinality()
    {
        _registry.Entries.Should().HaveCount(1);
        _registry.Entries[0].Contract.Should().Be(typeof(IRegistryFixtureContract));
        _registry.Entries[0].Cardinality.Should().Be(Cardinality.FanIn);
    }
}

[TestFixture]
public class Given_a_registry_with_two_contracts_declared_in_one_assembly
{
    private PluginContractRegistry _registry = null!;

    [SetUp]
    public void Setup() =>
        _registry = new PluginContractRegistry([
            new PluginContractEntry(typeof(IRegistryFixtureContract), Cardinality.FanIn),
            new PluginContractEntry(typeof(IRegistryFixtureSecondContract), Cardinality.Replace),
        ]);

    [Test]
    public void It_names_that_assembly_once()
    {
        _registry
            .ContractAssemblyNames.Should()
            .Equal("EdFi.Api.Plugins", "EdFi.Api.Plugins.Hosting.Tests.Unit");
    }

    [Test]
    public void It_keeps_both_entries_in_the_order_they_were_given()
    {
        _registry
            .Entries.Select(entry => entry.Contract)
            .Should()
            .Equal(typeof(IRegistryFixtureContract), typeof(IRegistryFixtureSecondContract));
    }
}

[TestFixture]
public class Given_a_registry_constructed_from_a_list_the_caller_still_holds
{
    private PluginContractRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        // Built here rather than in a field initializer: NUnit reuses one fixture instance across the
        // tests below, so a list built once would carry the entry the previous test appended.
        List<PluginContractEntry> source = [new(typeof(IRegistryFixtureContract), Cardinality.FanIn)];

        _registry = new PluginContractRegistry(source);

        // The hazard this pins: the assembly names are derived once, so an entry added afterwards
        // would leave the entries and the derived names describing different sets.
        source.Add(new PluginContractEntry(typeof(IRegistryFixtureSecondContract), Cardinality.Replace));
    }

    [Test]
    public void It_is_unaffected_by_a_later_change_to_that_list()
    {
        _registry.Entries.Should().HaveCount(1);
        _registry
            .ContractAssemblyNames.Should()
            .Equal("EdFi.Api.Plugins", "EdFi.Api.Plugins.Hosting.Tests.Unit");
    }

    [Test]
    public void It_exposes_collections_that_refuse_mutation()
    {
        ((IList<PluginContractEntry>)_registry.Entries).IsReadOnly.Should().BeTrue();
        ((IList<string>)_registry.ContractAssemblyNames).IsReadOnly.Should().BeTrue();
    }
}

[TestFixture]
public class Given_registry_entries_the_host_wrote_wrongly
{
    [Test]
    public void It_refuses_a_null_entry_list()
    {
        Action construction = static () => new PluginContractRegistry(null!);

        construction.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void It_refuses_a_null_entry()
    {
        Action construction = static () => new PluginContractRegistry([null!]);

        construction.Should().Throw<ArgumentException>().WithMessage("*entry*");
    }

    [Test]
    public void It_refuses_an_entry_naming_no_contract_type()
    {
        Action construction = static () =>
            new PluginContractRegistry([new PluginContractEntry(null!, Cardinality.FanIn)]);

        construction.Should().Throw<ArgumentException>().WithMessage("*contract*");
    }

    [Test]
    public void It_refuses_the_same_contract_twice()
    {
        Action construction = static () =>
            new PluginContractRegistry([
                new PluginContractEntry(typeof(IRegistryFixtureContract), Cardinality.FanIn),
                new PluginContractEntry(typeof(IRegistryFixtureContract), Cardinality.Replace),
            ]);

        construction.Should().Throw<ArgumentException>().WithMessage($"*{nameof(IRegistryFixtureContract)}*");
    }

    /// <summary>
    /// An open contract is refused where the host writes it rather than skipped where the guard reads
    /// it. The activation probe resolves every declared-contract registration, and there is no type
    /// argument the host could invent to close one, so a registry that held an open contract would
    /// turn an activation requirement into a silent exemption.
    /// </summary>
    [Test]
    public void It_refuses_a_contract_carrying_generic_parameters()
    {
        Action construction = static () =>
            new PluginContractRegistry([
                new PluginContractEntry(typeof(IRegistryFixtureOpenContract<>), Cardinality.FanIn),
            ]);

        construction
            .Should()
            .Throw<ArgumentException>()
            .WithMessage($"*{nameof(IRegistryFixtureOpenContract<object>)}*");
    }

    [Test]
    public void It_accepts_a_closed_generic_contract()
    {
        Action construction = static () =>
            new PluginContractRegistry([
                new PluginContractEntry(typeof(IRegistryFixtureOpenContract<string>), Cardinality.FanIn),
            ]);

        construction.Should().NotThrow();
    }
}
