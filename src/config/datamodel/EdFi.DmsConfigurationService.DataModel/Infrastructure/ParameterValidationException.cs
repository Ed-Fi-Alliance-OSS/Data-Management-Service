// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.DataModel.Infrastructure;

/// <summary>
/// Thrown by a paging/query-parameter validator guard (as opposed to a body-command validator) so the
/// CMS exception handler can classify the failure as urn:ed-fi:api:bad-request:parameter instead of the
/// body/data-validation taxonomy, without inspecting exception messages. Derives from
/// <see cref="ValidationException"/> so callers that only expect the generic FluentValidation type still
/// catch it; the exception handler matches this more specific subtype first.
/// </summary>
public sealed class ParameterValidationException(IEnumerable<ValidationFailure> errors)
    : ValidationException(errors);
