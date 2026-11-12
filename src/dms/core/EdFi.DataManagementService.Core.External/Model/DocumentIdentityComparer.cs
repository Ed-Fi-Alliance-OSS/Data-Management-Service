// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// The structural natural-key comparer: two identities are equal when they carry the same elements in the same
/// order, each with an ordinally equal identity path and value. It never folds case, so whether two spellings of a
/// descriptor URI name the same row is left to the database.
/// </summary>
public sealed class DocumentIdentityComparer : IEqualityComparer<DocumentIdentity>
{
    public static DocumentIdentityComparer Instance { get; } = new();

    private DocumentIdentityComparer() { }

    public bool Equals(DocumentIdentity? x, DocumentIdentity? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        var left = x.DocumentIdentityElements;
        var right = y.DocumentIdentityElements;

        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (
                !string.Equals(
                    left[index].IdentityJsonPath.Value,
                    right[index].IdentityJsonPath.Value,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    left[index].IdentityValue,
                    right[index].IdentityValue,
                    StringComparison.Ordinal
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(DocumentIdentity obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        HashCode hash = new();

        foreach (var element in obj.DocumentIdentityElements)
        {
            hash.Add(element.IdentityJsonPath.Value, StringComparer.Ordinal);
            hash.Add(element.IdentityValue, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
