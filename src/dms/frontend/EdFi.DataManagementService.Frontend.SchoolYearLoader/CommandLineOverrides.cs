// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using CommandLine;

namespace EdFi.DataManagementService.Frontend.SchoolYearLoader
{
    public class CommandLineOverrides
    {
        [Option('s', "startYear", Required = true, HelpText = "Start year of the school term.")]
        public int StartYear { get; set; }

        [Option('e', "endYear", Required = true, HelpText = "End year of the school term.")]
        public int EndYear { get; set; }
    }
}
