// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal static class CommandHost
{
    internal static RootCommand CreateCommand(TextWriter error)
    {
        var root = new RootCommand("Ed-Fi API Security Metadata Tools");
        var convert = new Command(
            "convert-ods-xml",
            "Convert an ODS security metadata XML export to a Configuration Service claims document."
        );
        convert.Options.Add(
            new Option<string>("--input")
            {
                Description = "Path to the ODS security metadata XML export.",
                Required = true,
            }
        );
        convert.Options.Add(
            new Option<string>("--output")
            {
                Description = "Path for the converted JSON claims document.",
                Required = true,
            }
        );
        convert.Options.Add(
            new Option<bool>("--force")
            {
                Description = "Replace an existing output file after successful conversion and validation.",
            }
        );
        convert.SetAction(result =>
            Convert(
                result.GetRequiredValue<string>("--input"),
                result.GetRequiredValue<string>("--output"),
                result.GetValue<bool>("--force"),
                error
            )
        );
        root.Subcommands.Add(convert);
        return root;
    }

    private static int Convert(string inputPath, string outputPath, bool force, TextWriter error)
    {
        try
        {
            // Complete conversion, CMS validation, and serialization before touching the destination.
            string json;
            using (FileStream input = File.OpenRead(inputPath))
            {
                json = OdsXmlToClaimsConverter
                    .Convert(input)
                    .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            string destination = Path.GetFullPath(outputPath);
            if (File.Exists(destination) && !force)
            {
                throw new IOException("Output file already exists. Use --force to replace it.");
            }

            // Stage beside the destination so a failed write cannot truncate an existing file.
            // Move also enforces no-overwrite if a destination appears after the existence check.
            string temporaryPath = Path.Combine(
                Path.GetDirectoryName(destination)!,
                $".security-metadata-tools-{Guid.NewGuid():N}.tmp"
            );
            var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write);
            try
            {
                using (stream)
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                }

                File.Move(temporaryPath, destination, overwrite: force);
            }
            finally
            {
                File.Delete(temporaryPath);
            }

            return 0;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or InvalidOperationException
            )
        {
            // Escape paths and source-derived diagnostics before writing a single stderr line.
            error.WriteLine($"Conversion failed: {JsonSerializer.Serialize(exception.Message)}");
            return 1;
        }
    }

    internal static Task<int> InvokeAsync(string[] args, TextWriter output, TextWriter error) =>
        CreateCommand(error)
            .Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
}
