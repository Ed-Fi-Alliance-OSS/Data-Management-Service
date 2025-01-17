using DmsOpenApiGenerator.Services;

namespace DmsOpenApiGenerator.Tests;

[TestFixture]
public class ApiSchemaFileLoaderTests
{
    private const string TestCoreSchemaPath = "test-core-schema.json";

    [SetUp]
    public void SetUp()
    {
        File.WriteAllText(TestCoreSchemaPath, "{ \"test\": \"data\" }");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(TestCoreSchemaPath))
        {
            File.Delete(TestCoreSchemaPath);
        }
    }

    [Test]
    public void LoadCoreSchema_ShouldLoadSchema_WhenFileExists()
    {
        // Arrange
        var loader = new ApiSchemaFileLoader();

        // Act
        var schema = loader.LoadCoreSchema(TestCoreSchemaPath);

        // Assert
        Assert.NotNull(schema);
        Assert.AreEqual("data", schema["test"]?.ToString());
    }

    [Test]
    public void LoadCoreSchema_ShouldThrowFileNotFoundException_WhenFileDoesNotExist()
    {
        // Arrange
        var loader = new ApiSchemaFileLoader();

        // Act & Assert
        var ex = Assert.Throws<FileNotFoundException>(() => loader.LoadCoreSchema("nonexistent-file.json"));
        Assert.That(ex.Message, Does.Contain("Core schema file not found"));
    }
}
