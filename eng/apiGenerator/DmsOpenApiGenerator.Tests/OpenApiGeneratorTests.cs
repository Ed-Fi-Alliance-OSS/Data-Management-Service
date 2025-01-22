using System.Reflection.Emit;
using System.Text.Json.Nodes;
using DmsOpenApiGenerator.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmsOpenApiGenerator.Tests;

[TestFixture]
public class OpenApiGeneratorTests
{
    internal static JsonNode CoreSchemaRootNode()
    {
        JsonObject schemas = new()
        {
            ["EdFi_AcademicWeek"] = new JsonObject
            {
                ["description"] = "AcademicWeek description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_AccountabilityRating"] = new JsonObject
            {
                ["description"] = "AccountabilityRating description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
        };

        JsonObject paths = new()
        {
            ["/ed-fi/academicWeeks"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "academicWeek get description" },
                ["post"] = new JsonObject { ["description"] = "academicWeek post description" },
            },
            ["/ed-fi/academicWeeks/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "academicWeek id get description" },
                ["delete"] = new JsonObject { ["description"] = "academicWeek delete description" },
            },
        };

        return new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithCoreOpenApiSpecification(schemas, paths)
            .WithEndProject()
            .AsRootJsonNode();
    }

    internal static JsonNode FirstExtensionSchemaRootNode()
    {
        JsonObject exts = new()
        {
            ["EdFi_AcademicWeek"] = new JsonObject
            {
                ["description"] = "ext AcademicWeek description",
                ["type"] = "string",
            },
        };

        JsonObject newPaths = new()
        {
            ["/tpdm/credentials"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "credential get" },
                ["post"] = new JsonObject { ["description"] = "credential post" },
            },
            ["/tpdm/credentials/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "credential id get" },
                ["delete"] = new JsonObject { ["description"] = "credential delete" },
            },
        };

        JsonObject newSchemas = new()
        {
            ["TPDM_Credential"] = new JsonObject
            {
                ["description"] = "TPDM credential description",
                ["type"] = "string",
            },
        };

        return new ApiSchemaBuilder()
            .WithStartProject("tpdm", "5.0.0")
            .WithOpenApiExtensionFragments(exts, newPaths, newSchemas)
            .WithEndProject()
            .AsRootJsonNode();
    }

    internal static JsonNode SecondExtensionSchemaRootNode()
    {
        JsonObject exts = new()
        {
            ["EdFi_AccountabilityRating"] = new JsonObject
            {
                ["description"] = "ext AccountabilityRating description",
                ["type"] = "string",
            },
        };

        JsonObject newPaths = new()
        {
            ["/tpdm/candidates"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "candidate get" },
                ["post"] = new JsonObject { ["description"] = "candidate post" },
            },
            ["/tpdm/candidates/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "candidate id get" },
                ["delete"] = new JsonObject { ["description"] = "candidate delete" },
            },
        };

        JsonObject newSchemas = new()
        {
            ["TPDM_Candidate"] = new JsonObject
            {
                ["description"] = "TPDM candidate description",
                ["type"] = "string",
            },
        };

        return new ApiSchemaBuilder()
            .WithStartProject("tpdm", "5.0.0")
            .WithOpenApiExtensionFragments(exts, newPaths, newSchemas)
            .WithEndProject()
            .AsRootJsonNode();
    }

    [TestFixture]
    public class Given_A_Non_Valid_Paths : OpenApiGeneratorTests
    {
        private ILogger<OpenApiGenerator> _fakeLogger = null!;
        private OpenApiGenerator _generator = null!;

        [SetUp]
        public void SetUp()
        {
            // Create a fake logger
            _fakeLogger = A.Fake<ILogger<OpenApiGenerator>>();
            _generator = new OpenApiGenerator(_fakeLogger);
        }

        [Test]
        public void Generate_ShouldThrowException_WhenPathsAreInvalid()
        {
            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => _generator.Generate("", "", ""));

            Assert.That(
                ex?.Message,
                Is.EqualTo("Core schema, extension schema, and output paths are required.")
            );
        }
    }

    [TestFixture]
    public class Given_Invalid_Values_For_Core_And_Extension : OpenApiGeneratorTests
    {
        private ILogger<OpenApiGenerator> _fakeLogger = null!;
        private OpenApiGenerator _generator = null!;

        [SetUp]
        public void SetUp()
        {
            // Create a fake logger
            _fakeLogger = A.Fake<ILogger<OpenApiGenerator>>();
            _generator = new OpenApiGenerator(_fakeLogger);
        }

        [Test]
        public void Generate_ShouldThrowInvalidOperationException_WhenNodeNotFound()
        {
            // Arrange
            string coreSchemaPath = "core-schema.json";
            string extensionSchemaPath = "extension-schema.json";
            string outputPath = "output.json";

            File.WriteAllText(coreSchemaPath, "{ \"openapi\": \"3.0.0\" }");
            File.WriteAllText(extensionSchemaPath, "{ \"info\": { \"title\": \"Test API\" } }");

            // Act
            var ex = Assert.Throws<InvalidOperationException>(
                () => _generator.Generate(coreSchemaPath, extensionSchemaPath, outputPath)
            );

            // Assert
            Assert.That(
                ex?.Message,
                Is.EqualTo("Node at path '$.projectSchemas['ed-fi'].coreOpenApiSpecification' not found")
            );

            // Cleanup
            File.Delete(coreSchemaPath);
            File.Delete(extensionSchemaPath);
            File.Delete(outputPath);
        }
    }

    [TestFixture]
    public class Given_A_Simple_Core_Schema_Document : OpenApiGeneratorTests
    {
        private JsonNode openApiDocumentResult = new JsonObject();

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaRootNode();
            OpenApiGenerator openApiDocument = new(NullLogger.Instance);
            openApiDocumentResult = openApiDocument.CombineSchemas(coreSchemaRootNode, []);
        }

        [Test]
        public void It_should_be_the_simple_result()
        {
            string expectedResult = """
                {
                  "components": {
                    "schemas": {
                      "EdFi_AcademicWeek": {
                        "description": "AcademicWeek description",
                        "properties": {},
                        "type": "string"
                      },
                      "EdFi_AccountabilityRating": {
                        "description": "AccountabilityRating description",
                        "properties": {},
                        "type": "string"
                      }
                    }
                  },
                  "paths": {
                    "/ed-fi/academicWeeks": {
                      "get": {
                        "description": "academicWeek get description"
                      },
                      "post": {
                        "description": "academicWeek post description"
                      }
                    },
                    "/ed-fi/academicWeeks/{id}": {
                      "get": {
                        "description": "academicWeek id get description"
                      },
                      "delete": {
                        "description": "academicWeek delete description"
                      }
                    }
                  }
                }
                """;

            string result = openApiDocumentResult.ToJsonString(new() { WriteIndented = true });

            expectedResult = expectedResult.Replace("\r\n", "\n");
            result = result.Replace("\r\n", "\n");

            result.Should().Be(expectedResult);
        }
    }

    [TestFixture]
    public class Given_A_Core_Schema_And_Multiple_Extension_Schemas : OpenApiGeneratorTests
    {
        private JsonNode openApiDocumentResult = new JsonObject();

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaRootNode();
            JsonNode[] extensionSchemaRootNodes =
            [
                FirstExtensionSchemaRootNode(),
                SecondExtensionSchemaRootNode(),
            ];
            OpenApiGenerator openApiDocument = new(NullLogger.Instance);
            openApiDocumentResult = openApiDocument.CombineSchemas(
                coreSchemaRootNode,
                extensionSchemaRootNodes
            );
        }

        [Test]
        public void It_should_be_the_simple_result()
        {
            string expectedResult = """
                {
                  "components": {
                    "schemas": {
                      "EdFi_AcademicWeek": {
                        "description": "AcademicWeek description",
                        "properties": {
                          "_ext": {
                            "description": "ext AcademicWeek description",
                            "type": "string"
                          }
                        },
                        "type": "string"
                      },
                      "EdFi_AccountabilityRating": {
                        "description": "AccountabilityRating description",
                        "properties": {
                          "_ext": {
                            "description": "ext AccountabilityRating description",
                            "type": "string"
                          }
                        },
                        "type": "string"
                      },
                      "TPDM_Credential": {
                        "description": "TPDM credential description",
                        "type": "string"
                      },
                      "TPDM_Candidate": {
                        "description": "TPDM candidate description",
                        "type": "string"
                      }
                    }
                  },
                  "paths": {
                    "/ed-fi/academicWeeks": {
                      "get": {
                        "description": "academicWeek get description"
                      },
                      "post": {
                        "description": "academicWeek post description"
                      }
                    },
                    "/ed-fi/academicWeeks/{id}": {
                      "get": {
                        "description": "academicWeek id get description"
                      },
                      "delete": {
                        "description": "academicWeek delete description"
                      }
                    },
                    "/tpdm/credentials": {
                      "get": {
                        "description": "credential get"
                      },
                      "post": {
                        "description": "credential post"
                      }
                    },
                    "/tpdm/credentials/{id}": {
                      "get": {
                        "description": "credential id get"
                      },
                      "delete": {
                        "description": "credential delete"
                      }
                    },
                    "/tpdm/candidates": {
                      "get": {
                        "description": "candidate get"
                      },
                      "post": {
                        "description": "candidate post"
                      }
                    },
                    "/tpdm/candidates/{id}": {
                      "get": {
                        "description": "candidate id get"
                      },
                      "delete": {
                        "description": "candidate delete"
                      }
                    }
                  }
                }
                """;

            string result = openApiDocumentResult.ToJsonString(new() { WriteIndented = true });

            expectedResult = expectedResult.Replace("\r\n", "\n");
            result = result.Replace("\r\n", "\n");

            result.Should().Be(expectedResult);
        }
    }
}
