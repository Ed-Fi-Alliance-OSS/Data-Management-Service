// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

public record GetResult<T>
{
    public record GetSuccess(IReadOnlyList<T> Results) : GetResult<T>();

    public record GetByIdSuccess(T Result) : GetResult<T>();

    public record GetByIdFailureNotExists() : GetResult<T>();

    public record UnknownFailure(string FailureMessage) : GetResult<T>();
}
