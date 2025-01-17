using DmsOpenApiGenerator.Services;
using Spectre.Console;

namespace DmsOpenApiGenerator;

abstract class Program
{
    static void Main()
    {
        try
        {
            var coreSchemaPath = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter the [green]path to the core ApiSchema JSON file[/]:")
                    .Validate(path => File.Exists(path) ? ValidationResult.Success() : ValidationResult.Error("[red]File not found[/]")));

            var extensionSchemaPath = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter the [green]path to the extension ApiSchema JSON file[/] (optional):")
                    .AllowEmpty());

            var outputPath = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter the [green]output path[/] for the generated OpenAPI file:"));

            var generator = new OpenApiGenerator();
            generator.Generate(coreSchemaPath, extensionSchemaPath, outputPath);

            AnsiConsole.MarkupLine($"[bold green]OpenAPI specification successfully generated at:[/] {outputPath}");
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }
    }
}
