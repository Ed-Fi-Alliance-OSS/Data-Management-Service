// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

public class ApiSchemaValidatorTests
{
    private ApiSchemaValidator? _validator;

    [SetUp]
    public void Setup()
    {
        var logger = NullLogger<ApiSchemaSchemaProvider>.Instance;
        _validator = new ApiSchemaValidator(new ApiSchemaSchemaProvider(logger));
    }

    [TestFixture]
    public class Given_An_Empty_Schema : ApiSchemaValidatorTests
    {
        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(new JsonObject()).Value;
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailureMessages[0].Should().Contain("Required properties");
            response[0].FailureMessages[0].Should().Contain("projectNameMapping");
            response[0].FailureMessages[0].Should().Contain("projectSchemas");
        }
    }

    [TestFixture]
    public class Given_A_ProjectSchema_With_Missing_Required_Properties : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectNameMapping\":{}, \"projectSchemas\": { \"ed-fi\": {\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
            ) ?? new JsonObject();

        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode).Value;
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailureMessages[0].Should().Contain("Required properties");
            response[0].FailureMessages[0].Should().Contain("abstractResources");
            response[0].FailureMessages[0].Should().Contain("caseInsensitiveEndpointNameMapping");
        }
    }

    [TestFixture]
    public class Given_Invalid_Identity_Json_Path_On_AbstractResource : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectNameMapping\":{}, \"projectSchemas\": { \"ed-fi\":{\"caseInsensitiveEndpointNameMapping\":{}, \"abstractResources\":{\"educationOrg\":{ \"identityJsonPaths\": [\"educationOrganizationId\"]} },\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
            ) ?? new JsonObject();

        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode).Value;
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailurePath.Value.Should().Contain("educationOrg.identityJsonPaths");
            response[0].FailureMessages[0]
                .Should()
                .Contain("The string value is not a match for the indicated regular expression");
        }
    }

    [TestFixture]
    public class Given_A_ResourceSchema_With_Missing_Required_Properties : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectNameMapping\":{}, \"projectSchemas\": { \"ed-fi\":{\"caseInsensitiveEndpointNameMapping\":{}, "
                    + "\"abstractResources\":{ },\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\","
                    + "\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{\"Students\":{\"allowIdentityUpdates\":false, "
                    + "\"documentPathsMapping\":{}, \"identityJsonPaths\":[], \"isDescriptor\":false, \"jsonSchemaForInsert\":{}, \"resourceName\":\"Student\"}}} } }"
            ) ?? new JsonObject();

        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode).Value;
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailurePath.Value.Should().Contain("ed-fi.resourceSchemas.Students");
            response[0].FailureMessages[0].Should().Contain("Required properties");
            response[0].FailureMessages[0].Should().Contain("isSchoolYearEnumeration");
            response[0].FailureMessages[0].Should().Contain("equalityConstraints");
            response[0].FailureMessages[0].Should().Contain("isSubclass");
        }
    }

    [TestFixture]
    public class Given_A_ResourceSchema_With_Invalid_DocumentPathSmapping : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectNameMapping\":{}, \"projectSchemas\": { \"ed-fi\":{\"caseInsensitiveEndpointNameMapping\":{}, "
                    + "\"abstractResources\":{ },\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\","
                    + "\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{\"Students\":{\"allowIdentityUpdates\":false, "
                    + "\"documentPathsMapping\":{\"begindate\":{}}, \"identityJsonPaths\":[],\"isSchoolYearEnumeration\":false,\"isSubclass\":false,\"equalityConstraints\":[],"
                    + "\"isDescriptor\":false, \"jsonSchemaForInsert\":{}, \"resourceName\":\"Student\"}}} } }"
            ) ?? new JsonObject();

        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode).Value;
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailurePath.Value.Should()
                .Contain("ed-fi.resourceSchemas.Students.documentPathsMapping.begindate");
            response[0].FailureMessages[0].Should().Contain("Required properties");
            response[0].FailureMessages[0].Should().Contain("isReference");
        }
    }

    [TestFixture]
    public class Given_A_Valid_Api_Schema : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectNameMapping\":{}, \"projectSchemas\": { \"ed-fi\":{\"caseInsensitiveEndpointNameMapping\":{}, "
                    + "\"abstractResources\":{ },\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\","
                    + "\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{\"Students\":{\"allowIdentityUpdates\":false, "
                    + "\"documentPathsMapping\":{\"begindate\":{\"isReference\":false }}, \"identityJsonPaths\":[],\"isSchoolYearEnumeration\":false,\"isSubclass\":false,\"equalityConstraints\":[],"
                    + "\"isDescriptor\":false, \"jsonSchemaForInsert\":{}, \"resourceName\":\"Student\"}}} } }"
            ) ?? new JsonObject();

        [Test]
        public void It_has_no_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode).Value;
            response.Should().NotBeNull();
            response.Count.Should().Be(0);
        }
    }
}
