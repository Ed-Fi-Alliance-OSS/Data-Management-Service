// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Structural equality comparer for <see cref="ScopeInstanceAddress"/> that handles
/// <see cref="ImmutableArray{T}"/> fields by comparing elements, including
/// nested <see cref="SemanticIdentityPart"/> values via serialized form.
/// </summary>
internal sealed class ScopeInstanceAddressComparer : IEqualityComparer<ScopeInstanceAddress>
{
    public static readonly ScopeInstanceAddressComparer Instance = new();

    public bool Equals(ScopeInstanceAddress? x, ScopeInstanceAddress? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return ScopeInstanceAddressEquals(x, y);
    }

    public int GetHashCode(ScopeInstanceAddress obj)
    {
        var hash = new HashCode();
        hash.Add(obj.JsonScope);
        foreach (var ancestor in obj.AncestorCollectionInstances)
        {
            hash.Add(ancestor.JsonScope);
            foreach (var part in ancestor.SemanticIdentityInOrder)
            {
                hash.Add(part.RelativePath);
                hash.Add(part.Value?.ToJsonString());
                hash.Add(part.IsPresent);
            }
        }
        return hash.ToHashCode();
    }

    internal static bool ScopeInstanceAddressEquals(ScopeInstanceAddress a, ScopeInstanceAddress b)
    {
        if (a.JsonScope != b.JsonScope)
        {
            return false;
        }

        if (a.AncestorCollectionInstances.Length != b.AncestorCollectionInstances.Length)
        {
            return false;
        }

        for (int i = 0; i < a.AncestorCollectionInstances.Length; i++)
        {
            var ai = a.AncestorCollectionInstances[i];
            var bi = b.AncestorCollectionInstances[i];

            if (ai.JsonScope != bi.JsonScope)
            {
                return false;
            }

            if (!SemanticIdentityEquals(ai.SemanticIdentityInOrder, bi.SemanticIdentityInOrder))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool SemanticIdentityEquals(
        ImmutableArray<SemanticIdentityPart> a,
        ImmutableArray<SemanticIdentityPart> b
    )
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].RelativePath != b[i].RelativePath)
            {
                return false;
            }

            if (a[i].IsPresent != b[i].IsPresent)
            {
                return false;
            }

            if (a[i].Value?.ToJsonString() != b[i].Value?.ToJsonString())
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Structural equality comparer for <see cref="CollectionRowAddress"/> that handles
/// <see cref="ImmutableArray{T}"/> fields by comparing elements.
/// </summary>
internal sealed class CollectionRowAddressComparer : IEqualityComparer<CollectionRowAddress>
{
    public static readonly CollectionRowAddressComparer Instance = new();

    public bool Equals(CollectionRowAddress? x, CollectionRowAddress? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        if (x.JsonScope != y.JsonScope)
        {
            return false;
        }

        if (!ScopeInstanceAddressComparer.ScopeInstanceAddressEquals(x.ParentAddress, y.ParentAddress))
        {
            return false;
        }

        return ScopeInstanceAddressComparer.SemanticIdentityEquals(
            x.SemanticIdentityInOrder,
            y.SemanticIdentityInOrder
        );
    }

    public int GetHashCode(CollectionRowAddress obj)
    {
        var hash = new HashCode();
        hash.Add(obj.JsonScope);
        hash.Add(obj.ParentAddress.JsonScope);
        foreach (var ancestor in obj.ParentAddress.AncestorCollectionInstances)
        {
            hash.Add(ancestor.JsonScope);
            foreach (var part in ancestor.SemanticIdentityInOrder)
            {
                hash.Add(part.RelativePath);
                hash.Add(part.Value?.ToJsonString());
                hash.Add(part.IsPresent);
            }
        }
        foreach (var part in obj.SemanticIdentityInOrder)
        {
            hash.Add(part.RelativePath);
            hash.Add(part.Value?.ToJsonString());
            hash.Add(part.IsPresent);
        }
        return hash.ToHashCode();
    }
}
