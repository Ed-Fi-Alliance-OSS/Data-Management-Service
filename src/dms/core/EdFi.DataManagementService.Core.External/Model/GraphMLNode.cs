// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

public sealed class GraphMLNode : IEquatable<GraphMLNode>, IComparable<GraphMLNode>, IComparable
{
    public GraphMLNode() { }

    public required string Id { get; init; }

    public override string ToString() => Id;

    #region Equality members

    public bool Equals(GraphMLNode? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Id == other.Id;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((GraphMLNode)obj);
    }

    public override int GetHashCode() => (Id != null ? Id.GetHashCode() : 0);

    #endregion

    #region Comparable members
    public int CompareTo(GraphMLNode? other)
    {
        if (ReferenceEquals(this, other))
        {
            return 0;
        }

        if (other is null)
        {
            return 1;
        }

        return string.Compare(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        if (ReferenceEquals(this, obj))
        {
            return 0;
        }

        return obj is GraphMLNode other
            ? CompareTo(other)
            : throw new ArgumentException($"Object must be of type {nameof(GraphMLNode)}");
    }

    #region Operator Overloads

    public static bool operator ==(GraphMLNode? left, GraphMLNode? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(GraphMLNode? left, GraphMLNode? right)
    {
        return !Equals(left, right);
    }

    public static bool operator <(GraphMLNode? left, GraphMLNode? right)
    {
        if (left is null)
        {
            return right is not null;
        }

        return left.CompareTo(right) < 0;
    }

    public static bool operator <=(GraphMLNode? left, GraphMLNode? right)
    {
        if (left is null)
        {
            return true;
        }

        return left.CompareTo(right) <= 0;
    }

    public static bool operator >(GraphMLNode? left, GraphMLNode? right)
    {
        if (left is null)
        {
            return false;
        }

        return left.CompareTo(right) > 0;
    }

    public static bool operator >=(GraphMLNode? left, GraphMLNode? right)
    {
        if (left is null)
        {
            return right is null;
        }

        return left.CompareTo(right) >= 0;
    }

    #endregion
    #endregion
}
