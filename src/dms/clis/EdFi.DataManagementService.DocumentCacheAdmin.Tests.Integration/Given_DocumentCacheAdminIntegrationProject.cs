// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
public sealed class Given_DocumentCacheAdminIntegrationProject
{
    private string _toolCommandName = null!;

    [SetUp]
    public void Setup()
    {
        _toolCommandName = DocumentCacheAdminCliConstants.ToolCommandName;
    }

    [Test]
    public void It_targets_the_packaged_document_cache_admin_command()
    {
        _toolCommandName.Should().Be("dms-document-cache");
    }
}
