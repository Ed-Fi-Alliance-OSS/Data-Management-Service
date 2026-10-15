// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_Resolved_Document_Reference_Map
{
    private const long ProgramDocumentId = 5_000_000_101;

    private static readonly QualifiedResourceName _program = new("Ed-Fi", "Program");
    private static readonly QualifiedResourceName _school = new("Ed-Fi", "School");

    private IResolvedDocumentReferenceMap _map = null!;

    [SetUp]
    public void Setup()
    {
        _map = new ResolvedDocumentReferenceMapFactory().Create([
            Resolved(_program, ProgramIdentity(), ProgramDocumentId, "$.programReference"),
        ]);
    }

    [Test]
    public void It_resolves_an_equal_identity_held_in_a_different_array()
    {
        var lookupIdentity = ProgramIdentity();

        _map.TryGetDocumentId(_program, lookupIdentity, out var documentId).Should().BeTrue();
        documentId.Should().Be(ProgramDocumentId);
    }

    [Test]
    public void It_needs_the_structural_comparer_because_default_key_equality_compares_arrays()
    {
        var first = new ReferenceLookupKey(_program, ProgramIdentity());
        var second = new ReferenceLookupKey(_program, ProgramIdentity());

        EqualityComparer<ReferenceLookupKey>.Default.Equals(first, second).Should().BeFalse();
        ReferenceLookupKeyComparer.Instance.Equals(first, second).Should().BeTrue();
        ReferenceLookupKeyComparer
            .Instance.GetHashCode(first)
            .Should()
            .Be(ReferenceLookupKeyComparer.Instance.GetHashCode(second));
    }

    [Test]
    public void It_misses_a_different_value()
    {
        _map.TryGetDocumentId(_program, ProgramIdentity(programName: "Career prep"), out _)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_misses_a_different_path()
    {
        DocumentIdentity identity = new([
            Element("$.educationOrganizationReference.educationOrganizationId", "255901"),
            Element("$.name", "Career Prep"),
            Element("$.programTypeDescriptor", "uri://ed-fi.org/ProgramTypeDescriptor#CTE"),
        ]);

        _map.TryGetDocumentId(_program, identity, out _).Should().BeFalse();
    }

    [Test]
    public void It_misses_a_different_element_order()
    {
        var elements = ProgramIdentity().DocumentIdentityElements;
        DocumentIdentity reordered = new([elements[1], elements[0], elements[2]]);

        _map.TryGetDocumentId(_program, reordered, out _).Should().BeFalse();
    }

    [Test]
    public void It_misses_a_different_target()
    {
        _map.TryGetDocumentId(_school, ProgramIdentity(), out _).Should().BeFalse();
    }

    [Test]
    public void It_accepts_the_same_identity_resolved_from_several_paths()
    {
        var map = new ResolvedDocumentReferenceMapFactory().Create([
            Resolved(_program, ProgramIdentity(), ProgramDocumentId, "$.programReference"),
            Resolved(_program, ProgramIdentity(), ProgramDocumentId, "$.programs[0].programReference"),
        ]);

        map.TryGetDocumentId(_program, ProgramIdentity(), out var documentId).Should().BeTrue();
        documentId.Should().Be(ProgramDocumentId);
    }

    [Test]
    public void It_rejects_one_identity_resolved_to_two_documents()
    {
        var act = () =>
            new ResolvedDocumentReferenceMapFactory().Create([
                Resolved(_program, ProgramIdentity(), ProgramDocumentId, "$.programReference"),
                Resolved(
                    _program,
                    ProgramIdentity(),
                    ProgramDocumentId + 1,
                    "$.programs[0].programReference"
                ),
            ]);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Two references to 'Ed-Fi.Program' with the same identity resolved to different documents*"
            );
    }

    [Test]
    public void It_exposes_no_default_equality_identity_dictionary_on_a_public_backend_member()
    {
        var offending = typeof(ReferenceLookupKey)
            .Assembly.GetExportedTypes()
            .SelectMany(type => PublicSignatureTypes(type).Select(signatureType => (type, signatureType)))
            .Where(pair => ExpandTypes(pair.signatureType).Any(IsForbidden))
            .Select(pair => $"{pair.type.FullName}: {pair.signatureType}")
            .Distinct()
            .ToList();

        offending.Should().BeEmpty();
    }

    [Test]
    public void It_detects_a_dictionary_keyed_by_document_identity()
    {
        // Proves the scan above would see the forbidden shapes it looks for.
        ExpandTypes(typeof(Task<IReadOnlyDictionary<DocumentIdentity, long>>))
            .Any(IsForbidden)
            .Should()
            .BeTrue();
        ExpandTypes(typeof(Dictionary<ReferenceLookupKey, long>)).Any(IsForbidden).Should().BeTrue();
        ExpandTypes(typeof(IReadOnlyDictionary<string, long>)).Any(IsForbidden).Should().BeFalse();
    }

    private static IEnumerable<Type> PublicSignatureTypes(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(flags))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(flags))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
        {
            if (method is MethodInfo methodInfo)
            {
                yield return methodInfo.ReturnType;
            }

            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var eventInfo in type.GetEvents(flags))
        {
            yield return eventInfo.EventHandlerType!;
        }
    }

    private static IEnumerable<Type> ExpandTypes(Type type)
    {
        yield return type;

        var inner = type.HasElementType ? [type.GetElementType()!] : type.GetGenericArguments();

        foreach (var nested in inner.SelectMany(ExpandTypes))
        {
            yield return nested;
        }
    }

    private static bool IsForbidden(Type type)
    {
        if (type == typeof(ReferenceLookupKey))
        {
            return true;
        }

        if (type.IsGenericParameter || !type.IsConstructedGenericType)
        {
            return false;
        }

        IEnumerable<Type> candidates = type.IsInterface
            ? [type, .. type.GetInterfaces()]
            : type.GetInterfaces();

        return candidates.Any(candidate =>
            candidate.IsConstructedGenericType
            && (
                candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
            )
            && candidate.GetGenericArguments()[0] is var keyType
            && (keyType == typeof(DocumentIdentity) || keyType == typeof(ReferenceLookupKey))
        );
    }

    private static ResolvedDocumentReference Resolved(
        QualifiedResourceName target,
        DocumentIdentity identity,
        long documentId,
        string path
    ) =>
        new(
            new DocumentReference(
                new BaseResourceInfo(
                    new ProjectName(target.ProjectName),
                    new ResourceName(target.ResourceName),
                    false
                ),
                identity,
                new ReferentialId(Guid.NewGuid()),
                new JsonPath(path)
            ),
            documentId,
            208
        );

    private static DocumentIdentity ProgramIdentity(string programName = "Career Prep") =>
        new([
            Element("$.educationOrganizationReference.educationOrganizationId", "255901"),
            Element("$.programName", programName),
            Element("$.programTypeDescriptor", "uri://ed-fi.org/ProgramTypeDescriptor#CTE"),
        ]);

    private static DocumentIdentityElement Element(string path, string value) =>
        new(new JsonPath(path), value);
}
