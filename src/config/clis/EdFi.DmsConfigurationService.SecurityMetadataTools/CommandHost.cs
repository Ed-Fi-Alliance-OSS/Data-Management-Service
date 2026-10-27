// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal static class CommandHost
{
    internal static RootCommand CreateCommand(TextWriter error) =>
        CreateCommand(TextWriter.Null, error, CreateHttpClient);

    internal static RootCommand CreateCommand(
        TextWriter output,
        TextWriter error,
        Func<HttpClient> createHttpClient
    )
    {
        var root = new RootCommand(
            "Ed-Fi API Security Metadata Tools. Prepare with merge, inspect with resolve, deploy through CMS, "
                + "then compare with verify. Use the corresponding CMS release/build and Data Standard; "
                + "--version identifies this tool build. File outputs require an existing parent directory; "
                + "--force permits atomic replacement only after successful processing and validation."
        );
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
        root.Subcommands.Add(MergeCommand.Create(error));
        root.Subcommands.Add(ResolveCommand.Create(error));
        root.Subcommands.Add(VerifyCommand.Create(output, error, createHttpClient));
        return root;
    }

    private static int Convert(string inputPath, string outputPath, bool force, TextWriter error) =>
        CommandDiagnostics.Execute(
            error,
            "Conversion",
            () =>
            {
                // Complete conversion, CMS validation, and serialization before touching the destination.
                string json;
                using (FileStream input = File.OpenRead(inputPath))
                {
                    json = OdsXmlToClaimsConverter
                        .Convert(input)
                        .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                }

                ProtectedFileOutput.Write(outputPath, json, force);
            }
        );

    internal static Task<int> InvokeAsync(string[] args, TextWriter output, TextWriter error) =>
        InvokeAsync(args, output, error, CreateHttpClient);

    internal static Task<int> InvokeAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<HttpClient> createHttpClient
    )
    {
        var result = CreateCommand(output, error, createHttpClient).Parse(args);
        // Keep command results separate from argument-error help.
        bool argumentFailure =
            result.CommandResult.Command.Name is "verify" or "resolve" && result.Errors.Count > 0;
        return result.InvokeAsync(
            new InvocationConfiguration { Output = argumentFailure ? error : output, Error = error }
        );
    }

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });
}
