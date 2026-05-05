// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Request-scoped descriptor-endpoint query preprocessing output used before descriptor page/count SQL planning.
/// </summary>
public sealed record DescriptorQueryPreprocessingResult(
    RelationalQueryPreprocessingOutcome Outcome,
    IReadOnlyList<PreprocessedDescriptorQueryElement> QueryElementsInOrder
);

/// <summary>
/// One descriptor query element plus its compiled resource metadata and any request-scoped preprocessed value.
/// </summary>
public sealed record PreprocessedDescriptorQueryElement(
    QueryElement QueryElement,
    SupportedDescriptorQueryField SupportedField,
    PreprocessedDescriptorQueryValue Value
);

/// <summary>
/// Request-scoped preprocessed descriptor query value.
/// </summary>
public abstract record PreprocessedDescriptorQueryValue
{
    private PreprocessedDescriptorQueryValue() { }

    /// <summary>
    /// Query value is unchanged and can flow to later descriptor predicate planning.
    /// </summary>
    public sealed record Raw(string Value) : PreprocessedDescriptorQueryValue;

    /// <summary>
    /// Query value was parsed as a <c>dms.Document.DocumentUuid</c> filter.
    /// </summary>
    public sealed record DocumentUuid(Guid Value) : PreprocessedDescriptorQueryValue;

    /// <summary>
    /// Query value was parsed as an exact-match <see cref="DateOnly" /> descriptor column filter.
    /// </summary>
    public sealed record DateOnlyValue(DateOnly Value) : PreprocessedDescriptorQueryValue;
}
