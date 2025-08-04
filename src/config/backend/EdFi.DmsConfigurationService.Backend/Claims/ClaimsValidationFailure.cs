// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// Represents a validation failure in the claims JSON structure
/// </summary>
public record ClaimsValidationFailure(JsonPath FailurePath, List<string> FailureMessages);
