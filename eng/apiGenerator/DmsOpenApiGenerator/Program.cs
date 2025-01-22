using DmsOpenApiGenerator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var serviceCollection = new ServiceCollection();
ConfigureServices(serviceCollection);
var serviceProvider = serviceCollection.BuildServiceProvider();

var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var generator = serviceProvider.GetRequiredService<OpenApiGenerator>();

try
{
    // Prompt for the paths interactively
    Console.Write("Enter the path to the core schema file: ");
    string coreSchemaPath = Console.ReadLine()?.Trim() ?? string.Empty;

    Console.Write("Enter the path to the extension schema file: ");
    string extensionSchemaPath = Console.ReadLine()?.Trim() ?? string.Empty;

    Console.Write("Enter the path to save the output OpenAPI spec file: ");
    string outputPath = Console.ReadLine()?.Trim() ?? string.Empty;

    // Validate file paths
    if (!File.Exists(coreSchemaPath))
    {
        logger.LogError("Core schema file not found: {CoreSchemaPath}", coreSchemaPath);
        return 1;
    }

    if (!File.Exists(extensionSchemaPath))
    {
        logger.LogError("Extension schema file not found: {ExtensionSchemaPath}", extensionSchemaPath);
        return 1;
    }

    generator.Generate(coreSchemaPath, extensionSchemaPath, outputPath);
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "An error occurred while generating the OpenAPI spec.");
    return 1;
}

void ConfigureServices(IServiceCollection services)
{
    services.AddLogging(config =>
    {
        config.AddConsole();
        config.SetMinimumLevel(LogLevel.Debug);
    });

    services.AddSingleton<OpenApiGenerator>();
}
