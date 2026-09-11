// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.ChangeQueries;

/// <summary>
/// Adapts planned ReadChanges custom-view checks to the input <see cref="CustomViewAuthorizationValidator"/>
/// takes, the way <see cref="PageDocumentIdCustomViewAdapter"/> adapts the live page checks. The validator
/// binds the view and its <c>DocumentId</c> column against the subject's root table in a row-free probe; it
/// never walks a basis path, so none is supplied. The validated view is the full, possibly suffixed, name.
/// </summary>
internal static class ReadChangesCustomViewValidationAdapter
{
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    public static IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> Adapt(
        DbTableName subjectRootTable,
        IReadOnlyList<ReadChangesCustomViewCheckSpec> checks
    )
    {
        ArgumentNullException.ThrowIfNull(checks);

        return
        [
            .. checks.Select(check => new PageDocumentIdAuthorizationCustomViewCheck(
                check.ConfiguredStrategy.StrategyName,
                check.ConfiguredStrategy.RawConfiguredIndex,
                check.View,
                _documentIdColumn,
                PathToBasisResource: [],
                subjectRootTable,
                _documentIdColumn
            )),
        ];
    }
}
