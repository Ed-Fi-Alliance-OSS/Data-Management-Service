// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Represents any type of failure that can occur during API schema operations
/// </summary>
public record ApiSchemaFailure(
    string FailureType, // "Validation", "FileNotFound", "ParseError", "Configuration", "AccessDenied", etc.
    string Message, // Human-readable error message
    JsonPath? FailurePath = null, // Optional: For validation failures, indicates where in the schema
    Exception? InnerException = null // Optional: Original exception for debugging
);
