// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A request-scoped write validation failure keyed to a concrete JSON path.
/// </summary>
/// <param name="Path">The concrete JSON path for the invalid request value or scope.</param>
/// <param name="Message">The validation message for the request-scoped failure.</param>
public sealed record WriteValidationFailure(JsonPath Path, string Message);
