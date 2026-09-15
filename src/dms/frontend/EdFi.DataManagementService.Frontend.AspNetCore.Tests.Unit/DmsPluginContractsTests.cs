// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.CustomValidation;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The contracts this host declares, and the assembly names the loader's version-skew preflight is
/// given.
/// </summary>
[TestFixture]
public class Given_TheDataManagementServicePluginContractRegistry
{
    [Test]
    public void It_declares_the_custom_resource_validator_as_fan_in()
    {
        DmsPluginContracts
            .Registry.Entries.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new { Contract = typeof(ICustomResourceValidator), Cardinality = Cardinality.FanIn }
            );
    }

    [Test]
    public void It_derives_the_contract_assembly_names_from_those_entries()
    {
        DmsPluginContracts
            .Registry.ContractAssemblyNames.Should()
            .Equal("EdFi.Api.Plugins", "EdFi.DataManagementService.CustomValidation");
    }

    [Test]
    public void It_does_not_name_the_package_the_validator_contract_ships_under()
    {
        // An assembly reference carries a simple assembly name, so a package id in this set would
        // match nothing in a plugin's metadata and the preflight would silently check nothing. The
        // validator contract packs as EdFi.Api.CustomValidation and is declared in the assembly
        // EdFi.DataManagementService.CustomValidation; only the second belongs here.
        DmsPluginContracts
            .Registry.ContractAssemblyNames.Should()
            .NotContain("EdFi.Api.CustomValidation");
    }
}
