using DmsOpenApiGenerator.Services;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace DmsOpenApiGenerator.Tests;

[TestFixture]
public class OpenApiGeneratorTests
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

        // Verify the error message was logged
        A.CallTo(
                () =>
                    _fakeLogger.Log(
                        LogLevel.Error,
                        A<EventId>.Ignored,
                        A<object>.Ignored,
                        A<Exception>.That.IsInstanceOf(typeof(ArgumentException)),
                        A<Func<object, Exception?, string>>.Ignored
                    )
            )
            .MustHaveHappenedOnceExactly();

        Assert.That(ex?.Message, Is.EqualTo("Core schema, extension schema, and output paths are required."));
    }

    [Test]
    public void Generate_ShouldWriteCombinedSchemaToOutputPath_WhenPathsAreValid()
    {
        // Arrange
        string coreSchemaPath = "core-schema.json";
        string extensionSchemaPath = "extension-schema.json";
        string outputPath = "output.json";

        File.WriteAllText(coreSchemaPath, "{ \"openapi\": \"3.0.0\" }");
        File.WriteAllText(extensionSchemaPath, "{ \"info\": { \"title\": \"Test API\" } }");

        // Act
        _generator.Generate(coreSchemaPath, extensionSchemaPath, outputPath);

        // Assert
        Assert.IsTrue(File.Exists(outputPath));
        var content = File.ReadAllText(outputPath);
        var json = JsonNode.Parse(content);
        Assert.IsNotNull(json);
        Assert.AreEqual("3.0.0", json?["openapi"]?.ToString());
        Assert.AreEqual("Test API", json?["info"]?["title"]?.ToString());

        // Verify the log messages
        A.CallTo(
                () =>
                    _fakeLogger.Log(
                        LogLevel.Information,
                        A<EventId>.Ignored,
                        A<object>.That.Matches(v =>
                            v.ToString()!.Contains("Starting OpenAPI generation") == true
                        ),
                        A<Exception>.Ignored,
                        A<Func<object, Exception?, string>>.Ignored
                    )
            )
            .MustHaveHappenedOnceExactly();

        A.CallTo(
                () =>
                    _fakeLogger.Log(
                        LogLevel.Information,
                        A<EventId>.Ignored,
                        A<object>.That.Matches(v =>
                            v.ToString()!.Contains("OpenAPI generation completed successfully") == true
                        ),
                        A<Exception>.Ignored,
                        A<Func<object, Exception?, string>>.Ignored
                    )
            )
            .MustHaveHappenedOnceExactly();

        // Cleanup
        File.Delete(coreSchemaPath);
        File.Delete(extensionSchemaPath);
        File.Delete(outputPath);
    }
}
