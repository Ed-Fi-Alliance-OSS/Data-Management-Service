// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateProjectStructure")]
public class Given_CdcConnectorTemplateIntegrationProject
{
    [Test]
    public void It_has_a_runnable_integration_test_project()
    {
        typeof(ICdcConnectorTemplateService)
            .Assembly.GetName()
            .Name.Should()
            .Be("EdFi.DataManagementService.Backend.Cdc");
    }
}
