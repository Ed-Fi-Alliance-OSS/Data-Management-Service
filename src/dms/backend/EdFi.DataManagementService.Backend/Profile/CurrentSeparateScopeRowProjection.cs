// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// A projection of one current 1:1 separate-scope row used by
/// <see cref="ProfileCollectionWalker"/> when calling
/// <c>SynthesizeSeparateScopeInstance</c>. Applies to both
/// <see cref="DbTableKind.RootExtension"/> and <see cref="DbTableKind.CollectionExtensionScope"/>
/// rows. No ordinal — separate scopes are 1:1 with their parent.
/// </summary>
/// <param name="ProjectedRow">Binding-indexed merged-row representation.</param>
/// <param name="ColumnNameProjection">Current row keyed by column name; consumed by the resolver.</param>
internal sealed record CurrentSeparateScopeRowProjection(
    RelationalWriteMergedTableRow ProjectedRow,
    IReadOnlyDictionary<DbColumnName, object?> ColumnNameProjection
);
