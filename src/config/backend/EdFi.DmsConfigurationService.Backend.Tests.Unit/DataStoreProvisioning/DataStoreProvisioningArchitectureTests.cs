// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// AC 13: nothing under <c>DataStoreProvisioning</c> depends on the job subsystem, the Frontend, or DMS.
/// The project graph already forbids the last two; this pins the in-assembly boundary with the job code.
/// </summary>
public class DataStoreProvisioningArchitectureTests
{
    private const string Namespace = "EdFi.DmsConfigurationService.Backend.DataStoreProvisioning";

    private static readonly string[] _forbiddenNamespacePrefixes =
    [
        "EdFi.DmsConfigurationService.Backend.Jobs",
        "EdFi.DmsConfigurationService.Frontend",
        "EdFi.DataManagementService",
        "Microsoft.AspNetCore",
    ];

    private const BindingFlags Declared =
        BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Instance
        | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    [TestFixture]
    public class Given_every_type_in_the_provisioning_namespace
    {
        private Type[] _types = [];
        private string[] _offendingReferences = [];

        [SetUp]
        public void Setup()
        {
            _types =
            [
                .. typeof(IDmsDataStoreProvisioner)
                    .Assembly.GetTypes()
                    .Where(type => type.Namespace?.StartsWith(Namespace, StringComparison.Ordinal) == true),
            ];

            _offendingReferences =
            [
                .. _types
                    .SelectMany(type => ReferencedTypes(type).Select(referenced => (type, referenced)))
                    .Where(pair => IsForbidden(pair.referenced))
                    .Select(pair => $"{pair.type.FullName} -> {pair.referenced.FullName}")
                    .Distinct(),
            ];
        }

        [Test]
        public void It_finds_the_contracts() => _types.Should().Contain(typeof(IDmsTargetEngine));

        [Test]
        public void It_references_no_job_frontend_or_dms_type() => _offendingReferences.Should().BeEmpty();

        private static bool IsForbidden(Type type) =>
            type.Namespace is not null
            && Array.Exists(
                _forbiddenNamespacePrefixes,
                prefix => type.Namespace.StartsWith(prefix, StringComparison.Ordinal)
            );

        private static IEnumerable<Type> ReferencedTypes(Type type)
        {
            IEnumerable<Type> surface =
            [
                .. type.BaseType is { } baseType ? [baseType] : Array.Empty<Type>(),
                .. type.GetInterfaces(),
                .. type.GetFields(Declared).Select(field => field.FieldType),
                .. type.GetProperties(Declared).Select(property => property.PropertyType),
                .. type.GetConstructors(Declared)
                    .SelectMany(ctor => ctor.GetParameters())
                    .Select(p => p.ParameterType),
                .. type.GetMethods(Declared).Select(method => method.ReturnType),
                .. type.GetMethods(Declared)
                    .SelectMany(method => method.GetParameters())
                    .Select(p => p.ParameterType),
            ];

            return surface.SelectMany(Flatten);
        }

        private static IEnumerable<Type> Flatten(Type type)
        {
            if (type.HasElementType && type.GetElementType() is { } elementType)
            {
                return Flatten(elementType);
            }

            return type.IsGenericType
                ? [type.GetGenericTypeDefinition(), .. type.GetGenericArguments().SelectMany(Flatten)]
                : [type];
        }
    }
}
