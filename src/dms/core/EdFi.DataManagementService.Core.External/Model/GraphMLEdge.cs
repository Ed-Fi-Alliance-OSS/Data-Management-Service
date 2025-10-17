// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using QuickGraph;

namespace EdFi.DataManagementService.Core.External.Model;

public sealed class GraphMLEdge : IEquatable<GraphMLEdge>, IEdge<GraphMLNode>
{
    public GraphMLEdge() { }

    public GraphMLEdge(GraphMLNode source, GraphMLNode target)
    {
        Source = source;
        Target = target;
    }

    public required GraphMLNode Source { get; init; }

    public required GraphMLNode Target { get; init; }
    public required bool IsReferenceRequired { get; set; }

    #region Equality members
    public bool Equals(GraphMLEdge? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Source.Id == other.Source.Id && Target.Id == other.Target.Id;
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

        return Equals((GraphMLEdge)obj);
    }

    public override int GetHashCode() => HashCode.Combine(Source.Id, Target.Id);

    #endregion
}
