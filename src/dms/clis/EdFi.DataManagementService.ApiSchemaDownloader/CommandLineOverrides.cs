// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using CommandLine;

namespace EdFi.DataManagementService.ApiSchemaDownloader
{
    public class CommandLineOverrides
    {
        [Option('p', "packageId", Required = true, HelpText = "packageId")]
        public required string PackageId { get; set; }

        [Option('v', "packageVersion", Required = false, HelpText = "packageVersion")]
        public string? PackageVersion { get; set; }

        [Option('f', "feedUrl", Required = false, HelpText = "feed Url")]
        public string FeedUrl { get; set; } = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json";

        [Option(
            'd',
            "apiSchemaFolder",
            Required = true,
            HelpText = "Path to folder containing the api schema files "
        )]
        public required string ApiSchemaFolder { get; set; }
    }
}

