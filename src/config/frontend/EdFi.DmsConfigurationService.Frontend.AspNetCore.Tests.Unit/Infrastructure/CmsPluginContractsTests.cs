// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// The contracts this host declares, and the assembly names the loader's version-skew preflight is
/// given.
/// </summary>
[TestFixture]
public class Given_TheConfigurationServicePluginContractRegistry
{
    [Test]
    public void It_declares_the_secret_resolver_and_the_client_secret_hasher_as_replace()
    {
        CmsPluginContracts
            .Registry.Entries.Should()
            .BeEquivalentTo(
                [
                    new { Contract = typeof(ISecretResolver), Cardinality = Cardinality.Replace },
                    new { Contract = typeof(IClientSecretHasher), Cardinality = Cardinality.Replace },
                ],
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_derives_the_contract_assembly_names_from_those_entries()
    {
        CmsPluginContracts
            .Registry.ContractAssemblyNames.Should()
            .Equal("EdFi.Api.Plugins", "EdFi.DmsConfigurationService.Secrets");
    }

    [Test]
    public void It_does_not_name_the_package_the_secrets_contract_ships_under()
    {
        // An assembly reference carries a simple assembly name, so a package id in this set would
        // match nothing in a plugin's metadata and the preflight would silently check nothing. The
        // secrets contract packs as EdFi.Api.Secrets and is declared in the assembly
        // EdFi.DmsConfigurationService.Secrets; only the second belongs here.
        CmsPluginContracts.Registry.ContractAssemblyNames.Should().NotContain("EdFi.Api.Secrets");
    }
}
