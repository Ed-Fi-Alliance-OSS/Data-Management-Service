// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Consumer semantics for a payload that already satisfies the structural contract (spec D-13), such as
/// identifier ranges or cross-field rules.
/// </summary>
public interface IJobPayloadValidator<in TPayload>
    where TPayload : class
{
    /// <summary>
    /// The failures found, as fixed reason codes, never input text; empty when the payload is valid.
    /// </summary>
    IReadOnlyList<string> Validate(TPayload payload);
}
