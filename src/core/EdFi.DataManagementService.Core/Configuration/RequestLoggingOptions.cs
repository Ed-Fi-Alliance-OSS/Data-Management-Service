// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

public class RequestLoggingOptions
{
    /// <summary>
    /// Used to determine whether to generate the body entry
    /// </summary>
    public required string LogLevel { get; set; }

    /// <summary>
    /// Know whether to mask the requested Body
    /// </summary>
    public bool MaskRequestBody { get; set; }
}
