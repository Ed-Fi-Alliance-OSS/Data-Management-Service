using DmsOpenApiGenerator.Services;

namespace DmsOpenApiGenerator.Tests;

[TestFixture]
public class OpenApiGeneratorTests
{
    private const string CoreSchemaPath = "test-core-schema.json";
    private const string OutputPath = "output-openapi.json";

    [SetUp]
    public void SetUp()
    {
        File.WriteAllText(CoreSchemaPath, "{ \"test\": \"data\" }");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(CoreSchemaPath))
        {
            File.Delete(CoreSchemaPath);
        }
        if (File.Exists(OutputPath))
        {
            File.Delete(OutputPath);
        }
    }

    [Test]
    public void Generate_ShouldCreateOpenApiSpecFile()
    {
        // Arrange
        var generator = new OpenApiGenerator();

        // Act
        generator.Generate(CoreSchemaPath, null, OutputPath);

        // Assert
        Assert.IsTrue(File.Exists(OutputPath));
        var content = File.ReadAllText(OutputPath);
        Assert.IsNotEmpty(content);
        Assert.That(content, Does.Contain("openapi"));
    }

    [Test]
    public void Generate_ShouldHandleMissingExtensionFile()
    {
        // Arrange
        var generator = new OpenApiGenerator();

        // Act
        generator.Generate(CoreSchemaPath, "nonexistent-extension.json", OutputPath);

        // Assert
        Assert.IsTrue(File.Exists(OutputPath));
        var content = File.ReadAllText(OutputPath);
        Assert.IsNotEmpty(content);
        Assert.That(content, Does.Contain("openapi"));
    }
}
