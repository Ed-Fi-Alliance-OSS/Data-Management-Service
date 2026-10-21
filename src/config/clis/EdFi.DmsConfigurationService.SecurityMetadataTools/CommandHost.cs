// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;

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
        // DMS-1617-06 wires the validated complete-document converter into this handler.
        convert.SetAction(_ =>
        {
            error.WriteLine("ODS XML conversion is not yet implemented.");
            return 1;
        });
        root.Subcommands.Add(convert);
        return root;
    }

    internal static Task<int> InvokeAsync(string[] args, TextWriter output, TextWriter error) =>
        CreateCommand(error)
            .Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
}
