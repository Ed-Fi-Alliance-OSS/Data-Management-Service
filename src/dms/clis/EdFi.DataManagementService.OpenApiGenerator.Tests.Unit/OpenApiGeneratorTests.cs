// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.OpenApiGenerator.Tests.Unit;

[TestFixture]
public class OpenApiGeneratorTests
{
    [TestFixture]
    public class Given_A_Non_Valid_Paths : OpenApiGeneratorTests
    {
        private ILogger<Services.OpenApiGenerator> _fakeLogger = null!;
        private Services.OpenApiGenerator _generator = null!;

        [SetUp]
        public void SetUp()
        {
            // Create a fake logger
            _fakeLogger = A.Fake<ILogger<Services.OpenApiGenerator>>();
            _generator = new Services.OpenApiGenerator(_fakeLogger);
        }

        [Test]
        public void Should_throw_ArgumentException_when_paths_are_invalid()
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
        private ILogger<Services.OpenApiGenerator> _fakeLogger = null!;
        private Services.OpenApiGenerator _generator = null!;

        [SetUp]
        public void SetUp()
        {
            // Create a fake logger
            _fakeLogger = A.Fake<ILogger<Services.OpenApiGenerator>>();
            _generator = new Services.OpenApiGenerator(_fakeLogger);
        }

        [Test]
        public void Should_throw_InvalidOperationException_when_node_not_found()
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
}
