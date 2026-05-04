// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Shared guard for separate-table profile collaborators that only support non-collection
/// extension scopes (<see cref="DbTableKind.RootExtension"/> or
/// <see cref="DbTableKind.CollectionExtensionScope"/>). Centralises the support rule and
/// exception wording previously duplicated across
/// <see cref="ProfileSeparateTableBindingClassifier"/> and
/// <see cref="ProfileSeparateTableKeyUnificationResolver"/>.
/// </summary>
internal static class ProfileSeparateTableSupportGuard
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="separateTablePlan"/>
    /// is not a supported separate-table kind. The thrown <c>paramName</c> is
    /// <c>nameof(separateTablePlan)</c>; the message identifies the calling collaborator
    /// via <paramref name="callerTypeName"/> so diagnostics keep their original tone.
    /// </summary>
    internal static void EnsureSupportedTableKind(TableWritePlan separateTablePlan, string callerTypeName)
    {
        ArgumentNullException.ThrowIfNull(separateTablePlan);

        var tableKind = separateTablePlan.TableModel.IdentityMetadata.TableKind;
        if (tableKind is DbTableKind.RootExtension or DbTableKind.CollectionExtensionScope)
        {
            return;
        }

        throw new ArgumentException(
            $"{callerTypeName} supports "
                + $"{nameof(DbTableKind.RootExtension)} and "
                + $"{nameof(DbTableKind.CollectionExtensionScope)} tables; got {tableKind} "
                + $"for table '{ProfileBindingClassificationCore.FormatTable(separateTablePlan)}'.",
            nameof(separateTablePlan)
        );
    }
}
