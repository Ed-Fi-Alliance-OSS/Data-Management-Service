// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Claims.Models;

/// <summary>
/// Represents an error that occurred during claims upload
/// </summary>
/// <param name="ErrorType">The type of error</param>
/// <param name="Message">The error message</param>
/// <param name="Path">The JSON path where the error occurred, if applicable</param>
public record ClaimsUploadError(string ErrorType, string Message, string? Path = null);
