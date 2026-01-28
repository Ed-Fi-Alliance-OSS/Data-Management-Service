// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

[TestFixture]
public class RelationalMetadataTests
{
    [TestFixture]
    public class Given_ApiSchema_With_Relational_Block
    {
        private ResourceSchema _resourceSchema = null!;

        [SetUp]
        public void Setup()
        {
            var apiSchemaJson = """
                {
                  "resourceName": "Student",
                  "isSchoolYearEnumeration": false,
                  "isDescriptor": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {},
                    "required": []
                  },
                  "identityJsonPaths": [],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "documentPathsMapping": {},
                  "equalityConstraints": [],
                  "queryFieldMapping": {},
                  "isSubclass": false,
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "relational": {
                    "rootTableNameOverride": "Student",
                    "nameOverrides": {
                      "$.addresses[*]": "Address",
                      "$.addresses[*].periods[*]": "AddressPeriod",
                      "$.studentReference": "Student"
                    }
                  }
                }
                """;

            _resourceSchema = new ResourceSchema(JsonNode.Parse(apiSchemaJson)!);
        }

        [Test]
        public void It_Should_Deserialize_Relational_Block()
        {
            _resourceSchema.Relational.Should().NotBeNull();
        }

        [Test]
        public void It_Should_Parse_RootTableNameOverride()
        {
            _resourceSchema.Relational!.RootTableNameOverride.Should().Be("Student");
        }

        [Test]
        public void It_Should_Parse_NameOverrides_Dictionary()
        {
            var nameOverrides = _resourceSchema.Relational!.NameOverrides;

            nameOverrides.Should().HaveCount(3);
            nameOverrides.Should().ContainKey("$.addresses[*]");
            nameOverrides["$.addresses[*]"].Should().Be("Address");
        }

        [Test]
        public void It_Should_Parse_All_NameOverrides()
        {
            var nameOverrides = _resourceSchema.Relational!.NameOverrides;

            nameOverrides["$.addresses[*]"].Should().Be("Address");
            nameOverrides["$.addresses[*].periods[*]"].Should().Be("AddressPeriod");
            nameOverrides["$.studentReference"].Should().Be("Student");
        }
    }

    [TestFixture]
    public class Given_ApiSchema_Without_Relational_Block
    {
        private ResourceSchema _resourceSchema = null!;

        [SetUp]
        public void Setup()
        {
            var apiSchemaJson = """
                {
                  "resourceName": "School",
                  "isSchoolYearEnumeration": false,
                  "isDescriptor": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {},
                    "required": []
                  },
                  "identityJsonPaths": [],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "documentPathsMapping": {},
                  "equalityConstraints": [],
                  "queryFieldMapping": {},
                  "isSubclass": false,
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": []
                }
                """;

            _resourceSchema = new ResourceSchema(JsonNode.Parse(apiSchemaJson)!);
        }

        [Test]
        public void It_Should_Return_Null()
        {
            _resourceSchema.Relational.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_ApiSchema_With_Only_RootTableNameOverride
    {
        private ResourceSchema _resourceSchema = null!;

        [SetUp]
        public void Setup()
        {
            var apiSchemaJson = """
                {
                  "resourceName": "Student",
                  "isSchoolYearEnumeration": false,
                  "isDescriptor": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {},
                    "required": []
                  },
                  "identityJsonPaths": [],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "documentPathsMapping": {},
                  "equalityConstraints": [],
                  "queryFieldMapping": {},
                  "isSubclass": false,
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "relational": {
                    "rootTableNameOverride": "Student"
                  }
                }
                """;

            _resourceSchema = new ResourceSchema(JsonNode.Parse(apiSchemaJson)!);
        }

        [Test]
        public void It_Should_Parse_RootTableNameOverride()
        {
            _resourceSchema.Relational!.RootTableNameOverride.Should().Be("Student");
        }

        [Test]
        public void It_Should_Return_Empty_NameOverrides()
        {
            _resourceSchema.Relational!.NameOverrides.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_ApiSchema_With_Empty_NameOverrides
    {
        private ResourceSchema _resourceSchema = null!;

        [SetUp]
        public void Setup()
        {
            var apiSchemaJson = """
                {
                  "resourceName": "Student",
                  "isSchoolYearEnumeration": false,
                  "isDescriptor": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {},
                    "required": []
                  },
                  "identityJsonPaths": [],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "documentPathsMapping": {},
                  "equalityConstraints": [],
                  "queryFieldMapping": {},
                  "isSubclass": false,
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "relational": {
                    "rootTableNameOverride": "Student",
                    "nameOverrides": {}
                  }
                }
                """;

            _resourceSchema = new ResourceSchema(JsonNode.Parse(apiSchemaJson)!);
        }

        [Test]
        public void It_Should_Return_Empty_NameOverrides()
        {
            _resourceSchema.Relational!.NameOverrides.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_ApiSchema_With_Only_NameOverrides_No_RootTableNameOverride
    {
        private ResourceSchema _resourceSchema = null!;

        [SetUp]
        public void Setup()
        {
            var apiSchemaJson = """
                {
                  "resourceName": "Student",
                  "isSchoolYearEnumeration": false,
                  "isDescriptor": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {},
                    "required": []
                  },
                  "identityJsonPaths": [],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "documentPathsMapping": {},
                  "equalityConstraints": [],
                  "queryFieldMapping": {},
                  "isSubclass": false,
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "relational": {
                    "nameOverrides": {
                      "$.addresses[*]": "Address"
                    }
                  }
                }
                """;

            _resourceSchema = new ResourceSchema(JsonNode.Parse(apiSchemaJson)!);
        }

        [Test]
        public void It_Should_Return_Null_For_RootTableNameOverride()
        {
            _resourceSchema.Relational!.RootTableNameOverride.Should().BeNull();
        }

        [Test]
        public void It_Should_Parse_NameOverrides()
        {
            _resourceSchema.Relational!.NameOverrides.Should().ContainKey("$.addresses[*]");
            _resourceSchema.Relational!.NameOverrides["$.addresses[*]"].Should().Be("Address");
        }
    }

    [TestFixture]
    public class Given_ApiSchema_With_Null_NameOverride_Value
    {
        [Test]
        public void It_Should_Throw_InvalidOperationException()
        {
            var apiSchemaJson = """
                {
                  "resourceName": "Student",
                  "isSchoolYearEnumeration": false,
                  "isDescriptor": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {},
                    "required": []
                  },
                  "identityJsonPaths": [],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "documentPathsMapping": {},
                  "equalityConstraints": [],
                  "queryFieldMapping": {},
                  "isSubclass": false,
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "relational": {
                    "nameOverrides": {
                      "$.addresses[*]": null
                    }
                  }
                }
                """;

            var resourceSchema = new ResourceSchema(JsonNode.Parse(apiSchemaJson)!);

            var action = () => resourceSchema.Relational!.NameOverrides;

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("Name override for '$.addresses[*]' has null value");
        }
    }
}
