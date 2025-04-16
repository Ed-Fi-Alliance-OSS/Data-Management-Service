// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using CommandLine;

namespace EdFi.DataManagementService.Frontend.BulkLoader
{
    public class CommandLineOverrides
    {
        [Option('b', "bulkLoadSourcePath", Required = true, HelpText = "bulkLoadSourcePath")]
        public required string BulkLoadSourcePath { get; set; }
    }
}
