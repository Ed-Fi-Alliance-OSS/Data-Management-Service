// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
[Parallelizable]
public class ProfileCreatabilityValidatorTests
{
    private static readonly IProfileCreatabilityValidator Validator = new ProfileCreatabilityValidator();

    private static ContentTypeDefinition CreateContentType(
        MemberSelection memberSelection,
        IReadOnlyList<PropertyRule>? properties = null,
        IReadOnlyList<CollectionRule>? collections = null,
        IReadOnlyList<ObjectRule>? objects = null
    )
    {
        return new ContentTypeDefinition(
            memberSelection,
            properties ?? [],
            objects ?? [],
            collections ?? [],
            []
        );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_IncludeOnly_When_RequiredFieldNotIncluded : ProfileCreatabilityValidatorTests
    {
        private IReadOnlyList<string> _result = null!;

        [SetUp]
        public void Setup()
        {
            var requiredFields = new List<string> { "nameOfInstitution", "schoolId" };
            var identityPropertyNames = new HashSet<string> { "schoolId" };

            // IncludeOnly profile that only includes shortNameOfInstitution (not nameOfInstitution)
            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("shortNameOfInstitution")]
            );

            _result = Validator.GetExcludedRequiredFields(requiredFields, contentType, identityPropertyNames);
        }

        [Test]
        public void It_returns_the_excluded_required_field()
        {
            _result.Should().Contain("nameOfInstitution");
        }

        [Test]
        public void It_does_not_return_identity_fields()
        {
            _result.Should().NotContain("schoolId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_IncludeOnly_When_RequiredFieldIncluded : ProfileCreatabilityValidatorTests
    {
        private IReadOnlyList<string> _result = null!;

        [SetUp]
        public void Setup()
        {
            var requiredFields = new List<string> { "nameOfInstitution", "schoolId" };
            var identityPropertyNames = new HashSet<string> { "schoolId" };

            // IncludeOnly profile that includes the required nameOfInstitution
            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties:
                [
                    new PropertyRule("nameOfInstitution"),
                    new PropertyRule("shortNameOfInstitution"),
                ]
            );

            _result = Validator.GetExcludedRequiredFields(requiredFields, contentType, identityPropertyNames);
        }

        [Test]
        public void It_returns_empty_list()
        {
            _result.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ExcludeOnly_When_RequiredFieldExcluded : ProfileCreatabilityValidatorTests
    {
        private IReadOnlyList<string> _result = null!;

        [SetUp]
        public void Setup()
        {
            var requiredFields = new List<string> { "nameOfInstitution", "schoolId" };
            var identityPropertyNames = new HashSet<string> { "schoolId" };

            // ExcludeOnly profile that explicitly excludes the required nameOfInstitution
            var contentType = CreateContentType(
                MemberSelection.ExcludeOnly,
                properties: [new PropertyRule("nameOfInstitution")]
            );

            _result = Validator.GetExcludedRequiredFields(requiredFields, contentType, identityPropertyNames);
        }

        [Test]
        public void It_returns_the_excluded_required_field()
        {
            _result.Should().Contain("nameOfInstitution");
        }

        [Test]
        public void It_does_not_return_identity_fields()
        {
            _result.Should().NotContain("schoolId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ExcludeOnly_When_RequiredFieldNotExcluded : ProfileCreatabilityValidatorTests
    {
        private IReadOnlyList<string> _result = null!;

        [SetUp]
        public void Setup()
        {
            var requiredFields = new List<string> { "nameOfInstitution", "schoolId" };
            var identityPropertyNames = new HashSet<string> { "schoolId" };

            // ExcludeOnly profile that excludes webSite (not nameOfInstitution)
            var contentType = CreateContentType(
                MemberSelection.ExcludeOnly,
                properties: [new PropertyRule("webSite")]
            );

            _result = Validator.GetExcludedRequiredFields(requiredFields, contentType, identityPropertyNames);
        }

        [Test]
        public void It_returns_empty_list()
        {
            _result.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_IncludeAll : ProfileCreatabilityValidatorTests
    {
        private IReadOnlyList<string> _result = null!;

        [SetUp]
        public void Setup()
        {
            var requiredFields = new List<string>
            {
                "nameOfInstitution",
                "schoolId",
                "educationOrganizationCategories",
            };
            var identityPropertyNames = new HashSet<string> { "schoolId" };

            // IncludeAll profile doesn't exclude any fields
            var contentType = CreateContentType(MemberSelection.IncludeAll);

            _result = Validator.GetExcludedRequiredFields(requiredFields, contentType, identityPropertyNames);
        }

        [Test]
        public void It_returns_empty_list()
        {
            _result.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_RequiredCollection_With_CollectionRule : ProfileCreatabilityValidatorTests
    {
        private IReadOnlyList<string> _result = null!;

        [SetUp]
        public void Setup()
        {
            var requiredFields = new List<string>
            {
                "nameOfInstitution",
                "schoolId",
                "educationOrganizationCategories",
            };
            var identityPropertyNames = new HashSet<string> { "schoolId" };

            // IncludeOnly profile that includes nameOfInstitution and has a collection rule for educationOrganizationCategories
            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("nameOfInstitution")],
                collections:
                [
                    new CollectionRule(
                        Name: "educationOrganizationCategories",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ]
            );

            _result = Validator.GetExcludedRequiredFields(requiredFields, contentType, identityPropertyNames);
        }

        [Test]
        public void It_returns_empty_list()
        {
            // educationOrganizationCategories is handled by the collection rule, not excluded
            _result.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_RequiredObject_With_ObjectRule : ProfileCreatabilityValidatorTests
    {
        private IReadOnlyList<string> _result = null!;

        [SetUp]
        public void Setup()
        {
            var requiredFields = new List<string> { "studentUniqueId", "birthData" };
            var identityPropertyNames = new HashSet<string> { "studentUniqueId" };

            // IncludeOnly profile with an object rule for birthData
            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                objects:
                [
                    new ObjectRule(
                        Name: "birthData",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("birthCity")],
                        NestedObjects: null,
                        Collections: null,
                        Extensions: null
                    ),
                ]
            );

            _result = Validator.GetExcludedRequiredFields(requiredFields, contentType, identityPropertyNames);
        }

        [Test]
        public void It_returns_empty_list()
        {
            // birthData is handled by the object rule, not excluded
            _result.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_IdentityField_IsRequired : ProfileCreatabilityValidatorTests
    {
        private IReadOnlyList<string> _result = null!;

        [SetUp]
        public void Setup()
        {
            var requiredFields = new List<string> { "schoolId", "nameOfInstitution" };
            var identityPropertyNames = new HashSet<string> { "schoolId" };

            // IncludeOnly profile that doesn't include schoolId (but schoolId is an identity field)
            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("nameOfInstitution")]
            );

            _result = Validator.GetExcludedRequiredFields(requiredFields, contentType, identityPropertyNames);
        }

        [Test]
        public void It_returns_empty_list()
        {
            // schoolId is an identity field, so it's always preserved even if not in the IncludeOnly list
            _result.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Multiple_Required_Fields_Excluded : ProfileCreatabilityValidatorTests
    {
        private IReadOnlyList<string> _result = null!;

        [SetUp]
        public void Setup()
        {
            var requiredFields = new List<string>
            {
                "nameOfInstitution",
                "schoolId",
                "educationOrganizationCategories",
                "gradeLevels",
            };
            var identityPropertyNames = new HashSet<string> { "schoolId" };

            // IncludeOnly profile that only includes gradeLevels via collection rule
            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                collections:
                [
                    new CollectionRule(
                        Name: "gradeLevels",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ]
            );

            _result = Validator.GetExcludedRequiredFields(requiredFields, contentType, identityPropertyNames);
        }

        [Test]
        public void It_returns_all_excluded_required_fields()
        {
            _result.Should().Contain("nameOfInstitution");
            _result.Should().Contain("educationOrganizationCategories");
        }

        [Test]
        public void It_does_not_return_identity_fields()
        {
            _result.Should().NotContain("schoolId");
        }

        [Test]
        public void It_does_not_return_fields_with_collection_rules()
        {
            _result.Should().NotContain("gradeLevels");
        }
    }
}
