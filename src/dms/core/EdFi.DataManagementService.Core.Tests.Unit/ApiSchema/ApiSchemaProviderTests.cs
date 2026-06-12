// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

[TestFixture]
public class Given_bundled_ApiSchema_package_content
{
    private ApiSchemaDocumentNodes _nodes = null!;

    [SetUp]
    public void Setup()
    {
        var provider = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            Options.Create(new AppSettings { AllowIdentityUpdateOverrides = "" }),
            new ApiSchemaValidator(NullLogger<ApiSchemaValidator>.Instance)
        );

        _nodes = provider.GetApiSchemaNodes();
    }

    [Test]
    public void It_loads_the_core_schema_from_the_application_output()
    {
        _nodes
            .CoreApiSchemaRootNode.SelectRequiredNodeFromPathAs<string>(
                "$.projectSchema.projectEndpointName",
                NullLogger.Instance
            )
            .Should()
            .Be("ed-fi");
    }
}
