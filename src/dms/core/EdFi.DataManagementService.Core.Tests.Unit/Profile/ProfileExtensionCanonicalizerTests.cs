// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class ProfileExtensionCanonicalizerTests
{
    public class Given_ProfileExtensionCanonicalizer
    {
        private IEffectiveApiSchemaProvider _effectiveApiSchemaProvider = null!;

        /// <summary>
        /// Builds a schema for <paramref name="resourceName"/> whose insert schema exposes
        /// a root-level <c>_ext.{extensionKey}</c> with the given member names.
        /// </summary>
        private static ApiSchemaDocuments CreateSchemaWithRootExtension(
            string resourceName,
            string extensionKey,
            params string[] extensionPropertyNames
        )
        {
            var extProperties = new JsonObject();
            foreach (var name in extensionPropertyNames)
            {
                extProperties[name] = new JsonObject { ["type"] = "string" };
            }

            var jsonSchemaForInsert = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            [extensionKey] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = extProperties,
                            },
                        },
                    },
                },
            };

            return BuildDocuments(resourceName, jsonSchemaForInsert);
        }

        /// <summary>
        /// Builds a schema for <paramref name="resourceName"/> whose insert schema exposes
        /// a collection whose items carry a nested <c>_ext.{extensionKey}</c>.
        /// </summary>
        private static ApiSchemaDocuments CreateSchemaWithCollectionExtension(
            string resourceName,
            string collectionName,
            string extensionKey
        )
        {
            var jsonSchemaForInsert = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [collectionName] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["_ext"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        [extensionKey] = new JsonObject { ["type"] = "object" },
                                    },
                                },
                            },
                        },
                    },
                },
            };

            return BuildDocuments(resourceName, jsonSchemaForInsert);
        }

        private static ApiSchemaDocuments BuildDocuments(string resourceName, JsonObject jsonSchemaForInsert)
        {
            var apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource(resourceName)
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            var resourceNode = apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new(resourceName));

            if (resourceNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
            }

            return apiSchemaDocuments;
        }

        private static ContentTypeDefinition ContentTypeWithExtension(
            MemberSelection parentSelection,
            ExtensionRule extension
        ) => new(parentSelection, [], [], [], [extension]);

        private static ExtensionRule Extension(string name) =>
            new(name, MemberSelection.IncludeAll, null, null, null, null);

        [SetUp]
        public void Setup()
        {
            _effectiveApiSchemaProvider = A.Fake<IEffectiveApiSchemaProvider>();
        }

        [Test]
        public void Canonicalize_rewrites_mixed_case_extension_name_to_schema_key()
        {
            // Arrange — schema key is sample, profile authored it as Sample.
            A.CallTo(() => _effectiveApiSchemaProvider.Documents)
                .Returns(CreateSchemaWithRootExtension("Staff", "sample", "firstPetOwnedDate"));

            var writeContentType = ContentTypeWithExtension(MemberSelection.ExcludeOnly, Extension("Sample"));
            var definition = new ProfileDefinition(
                "TestProfile",
                [new ResourceProfile("Staff", null, null, writeContentType)]
            );

            // Act
            ProfileDefinition result = ProfileExtensionCanonicalizer.Canonicalize(
                definition,
                _effectiveApiSchemaProvider
            );

            // Assert
            ExtensionRule canonical = result.Resources[0].WriteContentType!.Extensions.Single();
            canonical.Name.Should().Be("sample");
        }

        [Test]
        public void Canonicalize_drops_unknown_extension_rule()
        {
            // Arrange — schema only exposes sample, profile excludes an unknown extension.
            A.CallTo(() => _effectiveApiSchemaProvider.Documents)
                .Returns(CreateSchemaWithRootExtension("Staff", "sample", "firstPetOwnedDate"));

            var writeContentType = ContentTypeWithExtension(
                MemberSelection.ExcludeOnly,
                Extension("Nonexistent")
            );
            var definition = new ProfileDefinition(
                "TestProfile",
                [new ResourceProfile("Staff", null, null, writeContentType)]
            );

            // Act
            ProfileDefinition result = ProfileExtensionCanonicalizer.Canonicalize(
                definition,
                _effectiveApiSchemaProvider
            );

            // Assert — the unmatched rule is removed so no unresolved runtime scope is emitted.
            result.Resources[0].WriteContentType!.Extensions.Should().BeEmpty();
        }

        [Test]
        public void Canonicalize_rewrites_extension_nested_in_collection()
        {
            // Arrange — extension lives on collection items as staffPets[*]._ext.sample.
            A.CallTo(() => _effectiveApiSchemaProvider.Documents)
                .Returns(CreateSchemaWithCollectionExtension("Staff", "staffPets", "sample"));

            var collection = new CollectionRule(
                Name: "staffPets",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                NestedCollections: null,
                Extensions: [Extension("Sample")],
                ItemFilter: null
            );
            var writeContentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections: [collection],
                Extensions: []
            );
            var definition = new ProfileDefinition(
                "TestProfile",
                [new ResourceProfile("Staff", null, null, writeContentType)]
            );

            // Act
            ProfileDefinition result = ProfileExtensionCanonicalizer.Canonicalize(
                definition,
                _effectiveApiSchemaProvider
            );

            // Assert
            ExtensionRule canonical = result
                .Resources[0]
                .WriteContentType!.Collections.Single()
                .Extensions!.Single();
            canonical.Name.Should().Be("sample");
        }

        [Test]
        public void Canonicalize_rewrites_extension_nested_in_object()
        {
            // Arrange — extension lives on an object as addresses._ext.sample.
            var jsonSchemaForInsert = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["addresses"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["_ext"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["sample"] = new JsonObject { ["type"] = "object" },
                                },
                            },
                        },
                    },
                },
            };
            A.CallTo(() => _effectiveApiSchemaProvider.Documents)
                .Returns(BuildDocuments("Staff", jsonSchemaForInsert));

            var objectRule = new ObjectRule(
                Name: "addresses",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                Collections: null,
                Extensions: [Extension("Sample")]
            );
            var writeContentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                Properties: [],
                Objects: [objectRule],
                Collections: [],
                Extensions: []
            );
            var definition = new ProfileDefinition(
                "TestProfile",
                [new ResourceProfile("Staff", null, null, writeContentType)]
            );

            // Act
            ProfileDefinition result = ProfileExtensionCanonicalizer.Canonicalize(
                definition,
                _effectiveApiSchemaProvider
            );

            // Assert
            ExtensionRule canonical = result
                .Resources[0]
                .WriteContentType!.Objects.Single()
                .Extensions!.Single();
            canonical.Name.Should().Be("sample");
        }

        [Test]
        public void Canonicalize_rewrites_extension_inside_resolved_extension_object()
        {
            // Arrange — _ext.sample.obj._ext.other: an extension nested inside an object that
            // lives within a resolved extension. Exercises recursion from a resolved extension
            // into its object children.
            var jsonSchemaForInsert = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["obj"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["_ext"] = new JsonObject
                                            {
                                                ["type"] = "object",
                                                ["properties"] = new JsonObject
                                                {
                                                    ["other"] = new JsonObject { ["type"] = "object" },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            };
            A.CallTo(() => _effectiveApiSchemaProvider.Documents)
                .Returns(BuildDocuments("Staff", jsonSchemaForInsert));

            var innerObject = new ObjectRule(
                Name: "obj",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                Collections: null,
                Extensions: [Extension("Other")]
            );
            var sampleExtension = new ExtensionRule(
                Name: "sample",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                Objects: [innerObject],
                Collections: null
            );
            var writeContentType = ContentTypeWithExtension(MemberSelection.IncludeAll, sampleExtension);
            var definition = new ProfileDefinition(
                "TestProfile",
                [new ResourceProfile("Staff", null, null, writeContentType)]
            );

            // Act
            ProfileDefinition result = ProfileExtensionCanonicalizer.Canonicalize(
                definition,
                _effectiveApiSchemaProvider
            );

            // Assert — the outer sample stays and the inner Other is rewritten to other.
            ExtensionRule outer = result.Resources[0].WriteContentType!.Extensions.Single();
            outer.Name.Should().Be("sample");
            ExtensionRule inner = outer.Objects!.Single().Extensions!.Single();
            inner.Name.Should().Be("other");
        }

        [Test]
        public void Canonicalize_drops_root_extension_rule_when_extension_only_exists_nested()
        {
            // Arrange — the sample extension exists ONLY on staffPets[*]._ext, not at the root.
            // A root-level extension rule must not survive, or it would emit an unresolved
            // root $._ext.sample scope at runtime (the runtime 500).
            A.CallTo(() => _effectiveApiSchemaProvider.Documents)
                .Returns(CreateSchemaWithCollectionExtension("Staff", "staffPets", "sample"));

            var writeContentType = ContentTypeWithExtension(MemberSelection.ExcludeOnly, Extension("Sample"));
            var definition = new ProfileDefinition(
                "TestProfile",
                [new ResourceProfile("Staff", null, null, writeContentType)]
            );

            // Act
            ProfileDefinition result = ProfileExtensionCanonicalizer.Canonicalize(
                definition,
                _effectiveApiSchemaProvider
            );

            // Assert — the root rule is dropped because the root location has no _ext.sample.
            result.Resources[0].WriteContentType!.Extensions.Should().BeEmpty();
        }

        [Test]
        public void Canonicalize_rewrites_both_read_and_write_content_types()
        {
            // Arrange
            A.CallTo(() => _effectiveApiSchemaProvider.Documents)
                .Returns(CreateSchemaWithRootExtension("Staff", "sample", "firstPetOwnedDate"));

            var readContentType = ContentTypeWithExtension(MemberSelection.ExcludeOnly, Extension("Sample"));
            var writeContentType = ContentTypeWithExtension(MemberSelection.ExcludeOnly, Extension("SAMPLE"));
            var definition = new ProfileDefinition(
                "TestProfile",
                [new ResourceProfile("Staff", null, readContentType, writeContentType)]
            );

            // Act
            ProfileDefinition result = ProfileExtensionCanonicalizer.Canonicalize(
                definition,
                _effectiveApiSchemaProvider
            );

            // Assert
            result.Resources[0].ReadContentType!.Extensions.Single().Name.Should().Be("sample");
            result.Resources[0].WriteContentType!.Extensions.Single().Name.Should().Be("sample");
        }

        [Test]
        public void Canonicalize_uses_schema_key_casing_not_lowercase()
        {
            // Arrange — schema key is camelCase sampleStaff. The canonical key must come from
            // the schema/project endpoint name, not blind lowercasing, so an authored
            // samplestaff must become sampleStaff (not stay lowercase).
            A.CallTo(() => _effectiveApiSchemaProvider.Documents)
                .Returns(CreateSchemaWithRootExtension("Staff", "sampleStaff", "field"));

            var writeContentType = ContentTypeWithExtension(
                MemberSelection.ExcludeOnly,
                Extension("samplestaff")
            );
            var definition = new ProfileDefinition(
                "TestProfile",
                [new ResourceProfile("Staff", null, null, writeContentType)]
            );

            // Act
            ProfileDefinition result = ProfileExtensionCanonicalizer.Canonicalize(
                definition,
                _effectiveApiSchemaProvider
            );

            // Assert — the schema's exact casing is preserved.
            result.Resources[0].WriteContentType!.Extensions.Single().Name.Should().Be("sampleStaff");
        }

        [Test]
        public void Canonicalize_returns_same_instance_when_already_canonical()
        {
            // Arrange — extension already uses the schema key, so nothing should be rewritten.
            A.CallTo(() => _effectiveApiSchemaProvider.Documents)
                .Returns(CreateSchemaWithRootExtension("Staff", "sample", "firstPetOwnedDate"));

            var writeContentType = ContentTypeWithExtension(MemberSelection.ExcludeOnly, Extension("sample"));
            var definition = new ProfileDefinition(
                "TestProfile",
                [new ResourceProfile("Staff", null, null, writeContentType)]
            );

            // Act
            ProfileDefinition result = ProfileExtensionCanonicalizer.Canonicalize(
                definition,
                _effectiveApiSchemaProvider
            );

            // Assert
            result.Should().BeSameAs(definition);
        }
    }
}
