// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Core.Exceptions;

public readonly struct DetailedExceptionWrapper : IDetailedException
{
    private readonly IDetailedException _wrapped;

    public DetailedExceptionWrapper(IDetailedException wrapped)
    {
        _wrapped = wrapped;
    }

    public string? Detail
    {
        get => _wrapped.Detail;
    }

    public string? Type
    {
        get => _wrapped.Type;
    }

    public string? Title
    {
        get => _wrapped.Title;
    }

    public int Status
    {
        get => _wrapped.Status;
    }

    public string? CorrelationId
    {
        get => _wrapped.CorrelationId;
        set => _wrapped.CorrelationId = value;
    }

    public Dictionary<string, string[]>? ValidationErrors
    {
        get => _wrapped.ValidationErrors;
        set => _wrapped.ValidationErrors = value;
    }

    public string[]? Errors
    {
        get => _wrapped.Errors;
        set => _wrapped.Errors = value;
    }
}
