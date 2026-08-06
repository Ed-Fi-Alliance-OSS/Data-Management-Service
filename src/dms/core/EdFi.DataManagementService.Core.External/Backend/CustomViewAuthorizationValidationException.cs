// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Indicates that a GET-many custom authorization view could not be validated.
/// </summary>
public sealed class CustomViewAuthorizationValidationException(Exception innerException)
    : Exception("Custom authorization view validation failed.", innerException);
