// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Set by <c>ExceptionContentBoundaryMiddleware</c> when it replaces an exception on a route marked with
/// <see cref="ExceptionTypeOnlyLoggingMetadata"/>: the type names of the exception it withheld, which the
/// failed-request log event reports in place of the replacement's own type.
/// </summary>
/// <param name="OriginalExceptionTypes">The <see cref="ExceptionTypeNames.Chain"/> of the original exception.</param>
internal sealed record WithheldExceptionFeature(string OriginalExceptionTypes);
