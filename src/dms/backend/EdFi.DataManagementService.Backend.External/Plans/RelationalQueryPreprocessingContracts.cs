// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Request-scoped relational query preprocessing output used before predicate planning and page-SQL compilation.
/// </summary>
public sealed record RelationalQueryPreprocessingResult(
    RelationalQueryPreprocessingOutcome Outcome,
    IReadOnlyList<PreprocessedRelationalQueryElement> QueryElementsInOrder
)
{
    /// <summary>
    /// True when later page-selection SQL must join <c>dms.Document</c> to apply at least one parsed
    /// <see cref="RelationalQueryFieldTarget.DocumentUuid" /> filter.
    /// </summary>
    public bool RequiresDocumentUuidJoin =>
        QueryElementsInOrder.Any(static element =>
            element.Value is PreprocessedRelationalQueryValue.DocumentUuid
        );
}

/// <summary>
/// Classifies whether preprocessing should continue into predicate planning or short-circuit to an empty page.
/// </summary>
public abstract record RelationalQueryPreprocessingOutcome
{
    private RelationalQueryPreprocessingOutcome() { }

    /// <summary>
    /// Preprocessing succeeded and request-scoped planning may continue.
    /// </summary>
    public sealed record Continue : RelationalQueryPreprocessingOutcome;

    /// <summary>
    /// Preprocessing proved the query cannot match any rows, so later execution should return an empty page.
    /// </summary>
    /// <param name="Reason">Actionable diagnostic text describing why preprocessing proved the page is empty.</param>
    public sealed record EmptyPage(string Reason) : RelationalQueryPreprocessingOutcome;
}

/// <summary>
/// One query element plus its compiled resource metadata and any request-scoped preprocessed value.
/// </summary>
public sealed record PreprocessedRelationalQueryElement(
    QueryElement QueryElement,
    SupportedRelationalQueryField SupportedField,
    PreprocessedRelationalQueryValue Value
);

/// <summary>
/// Request-scoped preprocessed relational query value.
/// </summary>
public abstract record PreprocessedRelationalQueryValue
{
    private PreprocessedRelationalQueryValue() { }

    /// <summary>
    /// Query value is unchanged and can flow to later predicate/type planning.
    /// </summary>
    public sealed record Raw(string Value) : PreprocessedRelationalQueryValue;

    /// <summary>
    /// Query value was parsed as a <c>dms.Document.DocumentUuid</c> filter.
    /// </summary>
    public sealed record DocumentUuid(Guid Value) : PreprocessedRelationalQueryValue;
}
