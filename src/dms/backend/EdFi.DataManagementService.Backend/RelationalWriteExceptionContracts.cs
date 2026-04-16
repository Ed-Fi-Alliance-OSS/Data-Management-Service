// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalWriteExceptionClassifier
{
    bool TryClassify(
        DbException exception,
        [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
    );

    /// <summary>
    /// Reports whether the exception represents a transient, retry-eligible database failure
    /// (deadlock victim, serialization failure, lock-request timeout).
    /// </summary>
    bool IsTransientFailure(DbException exception);
}

public abstract record RelationalWriteExceptionClassification
{
    public abstract record ConstraintViolation : RelationalWriteExceptionClassification
    {
        protected ConstraintViolation(string constraintName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
            ConstraintName = constraintName;
        }

        public string ConstraintName { get; }
    }

    public sealed record UniqueConstraintViolation : ConstraintViolation
    {
        public UniqueConstraintViolation(string constraintName)
            : base(constraintName) { }
    }

    public sealed record ForeignKeyConstraintViolation : ConstraintViolation
    {
        public ForeignKeyConstraintViolation(string constraintName)
            : base(constraintName) { }
    }

    public sealed record UnrecognizedWriteFailure : RelationalWriteExceptionClassification
    {
        private UnrecognizedWriteFailure() { }

        public static UnrecognizedWriteFailure Instance { get; } = new();
    }
}

internal enum RelationalWriteReferenceKind
{
    Document,
    Descriptor,
}

internal sealed record RelationalWriteConstraintResolutionRequest
{
    public RelationalWriteConstraintResolutionRequest(
        ResourceWritePlan writePlan,
        ReferenceResolverRequest referenceResolutionRequest,
        RelationalWriteExceptionClassification.ConstraintViolation violation
    )
    {
        WritePlan = writePlan ?? throw new ArgumentNullException(nameof(writePlan));
        ReferenceResolutionRequest =
            referenceResolutionRequest ?? throw new ArgumentNullException(nameof(referenceResolutionRequest));
        Violation = violation ?? throw new ArgumentNullException(nameof(violation));

        if (ReferenceResolutionRequest.RequestResource != WritePlan.Model.Resource)
        {
            throw new ArgumentException(
                $"{nameof(referenceResolutionRequest)} must target resource "
                    + $"'{RelationalWriteSupport.FormatResource(WritePlan.Model.Resource)}'.",
                nameof(referenceResolutionRequest)
            );
        }
    }

    public ResourceWritePlan WritePlan { get; }

    public ReferenceResolverRequest ReferenceResolutionRequest { get; }

    public RelationalWriteExceptionClassification.ConstraintViolation Violation { get; }
}

internal interface IRelationalWriteConstraintResolver
{
    RelationalWriteConstraintResolution Resolve(RelationalWriteConstraintResolutionRequest request);
}

internal abstract record RelationalWriteConstraintResolution
{
    public abstract record ConstraintMatch : RelationalWriteConstraintResolution
    {
        protected ConstraintMatch(string constraintName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
            ConstraintName = constraintName;
        }

        public string ConstraintName { get; }
    }

    public sealed record RootNaturalKeyUnique : ConstraintMatch
    {
        public RootNaturalKeyUnique(string constraintName)
            : base(constraintName) { }
    }

    public sealed record RequestReference : ConstraintMatch
    {
        public RequestReference(
            string constraintName,
            RelationalWriteReferenceKind referenceKind,
            JsonPathExpression referencePath,
            QualifiedResourceName targetResource
        )
            : base(constraintName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(referencePath.Canonical);
            ReferenceKind = referenceKind;
            ReferencePath = referencePath;
            TargetResource = targetResource;
        }

        public RelationalWriteReferenceKind ReferenceKind { get; }

        public JsonPathExpression ReferencePath { get; }

        public QualifiedResourceName TargetResource { get; }
    }

    public sealed record Unresolved : ConstraintMatch
    {
        public Unresolved(string constraintName)
            : base(constraintName) { }
    }
}
