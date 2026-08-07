// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl.PublicContract.CompileCheck;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_CdcProviderSetup_Public_Contract_Compile_Check
{
    [Test]
    public void It_should_compile_a_production_assembly_against_the_public_contract()
    {
        // The separate production compile-check assembly is the assertion protected by this test.
        typeof(CdcProviderSetupPublicContractCompileCheck).Should().NotBeNull();
    }
}
