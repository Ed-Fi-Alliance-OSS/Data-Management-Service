// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_RuntimeMappingSetCompiler
{
    private const string MissingSemanticIdentityFixturePath =
        "Fixtures/runtime-plan-compilation/focused-stable-key/negative/missing-semantic-identity/fixture.manifest.json";
    private const string PositiveStableKeyFixturePath =
        "Fixtures/runtime-plan-compilation/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";

    private const string MinimalProjectSchemaJson = """
        {
          "projectName": "Ed-Fi",
          "projectVersion": "5.0.0",
          "projectEndpointName": "ed-fi",
          "isExtensionProject": false,
          "description": "Test schema",
          "resourceNameMapping": {},
          "caseInsensitiveEndpointNameMapping": {},
          "educationOrganizationHierarchy": {},
          "educationOrganizationTypes": [],
          "resourceSchemas": {
            "students": {
              "resourceName": "Student",
              "isDescriptor": false,
              "isSchoolYearEnumeration": false,
              "isResourceExtension": false,
              "allowIdentityUpdates": false,
              "isSubclass": false,
              "identityJsonPaths": [
                "$.studentUniqueId"
              ],
              "booleanJsonPaths": [],
              "numericJsonPaths": [],
              "dateJsonPaths": [],
              "dateTimeJsonPaths": [],
              "equalityConstraints": [],
              "arrayUniquenessConstraints": [],
              "documentPathsMapping": {
                "StudentUniqueId": {
                  "isReference": false,
                  "isPartOfIdentity": true,
                  "isRequired": true,
                  "path": "$.studentUniqueId"
                },
                "FirstName": {
                  "isReference": false,
                  "isPartOfIdentity": false,
                  "isRequired": true,
                  "path": "$.firstName"
                }
              },
              "queryFieldMapping": {},
              "securableElements": {
                "Namespace": [],
                "EducationOrganization": [],
                "Student": [],
                "Contact": [],
                "Staff": []
              },
              "authorizationPathways": [],
              "decimalPropertyValidationInfos": [],
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "studentUniqueId": {
                    "type": "string",
                    "maxLength": 32
                  },
                  "firstName": {
                    "type": "string",
                    "maxLength": 75
                  }
                },
                "required": [
                  "studentUniqueId",
                  "firstName"
                ]
              }
            }
          },
          "abstractResources": {}
        }
        """;

    private static readonly string _testHash = new('a', 64);

    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private static EffectiveSchemaSet CreateEffectiveSchemaSet(string? hash = null)
    {
        var effectiveHash = hash ?? _testHash;
        var resourceKeyEntry = new ResourceKeyEntry(1, _studentResource, "5.0.0", false);

        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: effectiveHash,
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false, new string('b', 64)),
            ],
            ResourceKeysInIdOrder: [resourceKeyEntry]
        );

        var projectSchema = (JsonObject)JsonNode.Parse(MinimalProjectSchemaJson)!;

        return new EffectiveSchemaSet(
            effectiveSchema,
            [new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, projectSchema)]
        );
    }

    private static RuntimeMappingSetCompiler CreateCompiler(
        SqlDialect dialect,
        ISqlDialectRules? dialectRules = null,
        Func<EffectiveSchemaSet>? accessor = null
    )
    {
        var schemaSet = CreateEffectiveSchemaSet();
        return new RuntimeMappingSetCompiler(
            accessor ?? (() => schemaSet),
            new MappingSetCompiler(),
            dialect,
            dialectRules ?? (dialect == SqlDialect.Pgsql ? new PgsqlDialectRules() : new MssqlDialectRules())
        );
    }

    [TestFixture]
    public class Given_Pgsql_Dialect : Given_RuntimeMappingSetCompiler
    {
        [Test]
        public void It_returns_pgsql_dialect()
        {
            var compiler = CreateCompiler(SqlDialect.Pgsql);
            compiler.Dialect.Should().Be(SqlDialect.Pgsql);
        }

        [Test]
        public void It_returns_current_key_matching_effective_schema()
        {
            var compiler = CreateCompiler(SqlDialect.Pgsql);
            var key = compiler.GetCurrentKey();

            key.EffectiveSchemaHash.Should().Be(_testHash);
            key.Dialect.Should().Be(SqlDialect.Pgsql);
            key.RelationalMappingVersion.Should().Be("v1");
        }

        [Test]
        public async Task It_compiles_successfully()
        {
            var compiler = CreateCompiler(SqlDialect.Pgsql);
            var key = compiler.GetCurrentKey();

            var mappingSet = await compiler.CompileAsync(key, CancellationToken.None);

            mappingSet.Should().NotBeNull();
            mappingSet.Key.Should().Be(key);
            mappingSet.Key.Dialect.Should().Be(SqlDialect.Pgsql);
        }
    }

    [TestFixture]
    public class Given_Mssql_Dialect : Given_RuntimeMappingSetCompiler
    {
        [Test]
        public void It_returns_mssql_dialect()
        {
            var compiler = CreateCompiler(SqlDialect.Mssql);
            compiler.Dialect.Should().Be(SqlDialect.Mssql);
        }

        [Test]
        public async Task It_compiles_successfully()
        {
            var compiler = CreateCompiler(SqlDialect.Mssql);
            var key = compiler.GetCurrentKey();

            var mappingSet = await compiler.CompileAsync(key, CancellationToken.None);

            mappingSet.Should().NotBeNull();
            mappingSet.Key.Should().Be(key);
            mappingSet.Key.Dialect.Should().Be(SqlDialect.Mssql);
        }
    }

    [TestFixture]
    public class Given_Key_Mismatch : Given_RuntimeMappingSetCompiler
    {
        [Test]
        public async Task It_throws_InvalidOperationException()
        {
            var compiler = CreateCompiler(SqlDialect.Pgsql);

            var wrongKey = new MappingSetKey(new string('f', 64), SqlDialect.Pgsql, "v1");

            var act = () => compiler.CompileAsync(wrongKey, CancellationToken.None);

            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*Cannot compile*current schema resolved to*");
        }
    }

    [TestFixture]
    public class Given_Accessor_Throws : Given_RuntimeMappingSetCompiler
    {
        [Test]
        public void It_wraps_exception_with_initialization_guidance()
        {
            Func<EffectiveSchemaSet> failingAccessor = () =>
                throw new InvalidOperationException("Schema not initialized");

            var compiler = CreateCompiler(SqlDialect.Pgsql, accessor: failingAccessor);

            var act = () => compiler.GetCurrentKey();

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*runtime mapping initialization failed*")
                .WithInnerException<InvalidOperationException>();
        }
    }

    [TestFixture]
    public class Given_Abstract_Resource_Plan_Uses_Renamed_Columns : Given_RuntimeMappingSetCompiler
    {
        // A minimal schema with an EducationOrganization abstract resource and a School subclass
        // that references it.  The write plan column bindings must carry the renamed abstract identity
        // columns (e.g. EducationOrganizationId), not old concatenated names.
        private const string AbstractResourceProjectSchemaJson = """
            {
              "projectName": "Ed-Fi",
              "projectVersion": "5.0.0",
              "projectEndpointName": "ed-fi",
              "isExtensionProject": false,
              "description": "Test schema with abstract resource",
              "resourceNameMapping": {},
              "caseInsensitiveEndpointNameMapping": {},
              "educationOrganizationHierarchy": {},
              "educationOrganizationTypes": [],
              "abstractResources": {
                "EducationOrganization": {
                  "identityJsonPaths": [
                    "$.educationOrganizationId"
                  ]
                }
              },
              "resourceSchemas": {
                "schools": {
                  "resourceName": "School",
                  "isDescriptor": false,
                  "isSchoolYearEnumeration": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": true,
                  "isSubclass": true,
                  "superclassProjectName": "Ed-Fi",
                  "superclassResourceName": "EducationOrganization",
                  "identityJsonPaths": [
                    "$.educationOrganizationId"
                  ],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "equalityConstraints": [],
                  "arrayUniquenessConstraints": [],
                  "documentPathsMapping": {
                    "EducationOrganizationId": {
                      "isReference": false,
                      "isPartOfIdentity": true,
                      "isRequired": true,
                      "path": "$.educationOrganizationId"
                    }
                  },
                  "queryFieldMapping": {},
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {
                      "educationOrganizationId": {
                        "type": "integer"
                      }
                    },
                    "required": [
                      "educationOrganizationId"
                    ]
                  }
                }
              }
            }
            """;

        private static readonly QualifiedResourceName AbstractSchoolResource = new("Ed-Fi", "School");
        private static readonly QualifiedResourceName AbstractEdOrgResource = new(
            "Ed-Fi",
            "EducationOrganization"
        );

        private static EffectiveSchemaSet CreateAbstractResourceSchemaSet()
        {
            var schoolKeyEntry = new ResourceKeyEntry(1, AbstractSchoolResource, "5.0.0", false);
            var abstractKeyEntry = new ResourceKeyEntry(2, AbstractEdOrgResource, "5.0.0", true);

            var effectiveSchema = new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "1.0.0",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: new string('c', 64),
                ResourceKeyCount: 2,
                ResourceKeySeedHash: new byte[32],
                SchemaComponentsInEndpointOrder:
                [
                    new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false, new string('d', 64)),
                ],
                ResourceKeysInIdOrder: [schoolKeyEntry, abstractKeyEntry]
            );

            var projectSchema = (JsonObject)JsonNode.Parse(AbstractResourceProjectSchemaJson)!;

            return new EffectiveSchemaSet(
                effectiveSchema,
                [new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, projectSchema)]
            );
        }

        [Test]
        public async Task It_should_compile_write_plan_with_renamed_abstract_identity_columns()
        {
            var schemaSet = CreateAbstractResourceSchemaSet();
            var compiler = new RuntimeMappingSetCompiler(
                () => schemaSet,
                new MappingSetCompiler(),
                SqlDialect.Pgsql,
                new PgsqlDialectRules()
            );
            var key = compiler.GetCurrentKey();
            var mappingSet = await compiler.CompileAsync(key, CancellationToken.None);

            mappingSet.WritePlansByResource.Should().ContainKey(AbstractSchoolResource);
            var writePlan = mappingSet.WritePlansByResource[AbstractSchoolResource];
            var rootTablePlan = writePlan.TablePlansInDependencyOrder.Single(p =>
                p.TableModel.Table.Name == "School"
            );

            // Column bindings for the School root table must include the renamed abstract identity
            // column (EducationOrganizationId), not an old concatenated form.
            var columnNames = rootTablePlan.ColumnBindings.Select(b => b.Column.ColumnName.Value).ToList();

            columnNames.Should().Contain("EducationOrganizationId");
            // Must NOT carry old-style concatenated names
            columnNames.Should().NotContain("EducationOrganizationReferenceEducationOrganizationId");
        }
    }

    /// <summary>
    /// Tests that compile a write plan for a subclass of a reference-backed abstract resource
    /// (GeneralStudentProgramAssociation-style) and assert that the compiled column bindings
    /// carry the <em>renamed</em> abstract identity column names produced by the DMS-1223 rename
    /// (e.g. <c>Student_StudentUniqueId</c>, <c>Program_ProgramTypeDescriptor_DescriptorId</c>),
    /// not the old concatenated forms (<c>StudentReferenceStudentUniqueId</c>,
    /// <c>ProgramReferenceProgramTypeDescriptor</c>).
    /// </summary>
    [TestFixture]
    public class Given_Abstract_Resource_Plan_Uses_Renamed_Reference_Backed_Columns
        : Given_RuntimeMappingSetCompiler
    {
        // Schema with GeneralStudentProgramAssociation as the abstract resource whose identity
        // paths flow through EducationOrganization, Program (with a descriptor field), and Student
        // references — the exact same shape as the trigger-inventory fixture in
        // DeriveTriggerInventoryPassTests.cs.
        private const string AbstractCompositeReferenceProjectSchemaJson = """
            {
              "projectName": "Ed-Fi",
              "projectVersion": "5.0.0",
              "projectEndpointName": "ed-fi",
              "isExtensionProject": false,
              "description": "Test schema with reference-backed abstract resource",
              "resourceNameMapping": {},
              "caseInsensitiveEndpointNameMapping": {},
              "educationOrganizationHierarchy": {},
              "educationOrganizationTypes": [],
              "abstractResources": {
                "GeneralStudentProgramAssociation": {
                  "identityJsonPaths": [
                    "$.beginDate",
                    "$.educationOrganizationReference.educationOrganizationId",
                    "$.programReference.educationOrganizationId",
                    "$.programReference.programTypeDescriptor",
                    "$.studentReference.studentUniqueId"
                  ]
                },
                "EducationOrganization": {
                  "identityJsonPaths": [
                    "$.educationOrganizationId"
                  ]
                }
              },
              "resourceSchemas": {
                "studentArtProgramAssociations": {
                  "resourceName": "StudentArtProgramAssociation",
                  "isDescriptor": false,
                  "isSchoolYearEnumeration": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "isSubclass": true,
                  "subclassType": "association",
                  "superclassProjectName": "Ed-Fi",
                  "superclassResourceName": "GeneralStudentProgramAssociation",
                  "identityJsonPaths": [
                    "$.beginDate",
                    "$.educationOrganizationReference.educationOrganizationId",
                    "$.programReference.educationOrganizationId",
                    "$.programReference.programTypeDescriptor",
                    "$.studentReference.studentUniqueId"
                  ],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [ "$.beginDate" ],
                  "dateTimeJsonPaths": [],
                  "equalityConstraints": [],
                  "arrayUniquenessConstraints": [],
                  "documentPathsMapping": {
                    "BeginDate": {
                      "isReference": false,
                      "isPartOfIdentity": true,
                      "isRequired": true,
                      "path": "$.beginDate"
                    },
                    "EducationOrganization": {
                      "isReference": true,
                      "isDescriptor": false,
                      "isPartOfIdentity": true,
                      "isRequired": true,
                      "projectName": "Ed-Fi",
                      "resourceName": "EducationOrganization",
                      "referenceJsonPaths": [
                        {
                          "identityJsonPath": "$.educationOrganizationId",
                          "referenceJsonPath": "$.educationOrganizationReference.educationOrganizationId"
                        }
                      ]
                    },
                    "Program": {
                      "isReference": true,
                      "isDescriptor": false,
                      "isPartOfIdentity": true,
                      "isRequired": true,
                      "projectName": "Ed-Fi",
                      "resourceName": "Program",
                      "referenceJsonPaths": [
                        {
                          "identityJsonPath": "$.educationOrganizationId",
                          "referenceJsonPath": "$.programReference.educationOrganizationId"
                        },
                        {
                          "identityJsonPath": "$.programTypeDescriptor",
                          "referenceJsonPath": "$.programReference.programTypeDescriptor"
                        }
                      ]
                    },
                    "Student": {
                      "isReference": true,
                      "isDescriptor": false,
                      "isPartOfIdentity": true,
                      "isRequired": true,
                      "projectName": "Ed-Fi",
                      "resourceName": "Student",
                      "referenceJsonPaths": [
                        {
                          "identityJsonPath": "$.studentUniqueId",
                          "referenceJsonPath": "$.studentReference.studentUniqueId"
                        }
                      ]
                    }
                  },
                  "queryFieldMapping": {},
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {
                      "beginDate": { "type": "string", "format": "date" },
                      "educationOrganizationReference": {
                        "type": "object",
                        "properties": {
                          "educationOrganizationId": { "type": "integer" }
                        }
                      },
                      "programReference": {
                        "type": "object",
                        "properties": {
                          "educationOrganizationId": { "type": "integer" },
                          "programTypeDescriptor": { "type": "string", "maxLength": 306 }
                        }
                      },
                      "studentReference": {
                        "type": "object",
                        "properties": {
                          "studentUniqueId": { "type": "string", "maxLength": 32 }
                        }
                      }
                    },
                    "required": [
                      "beginDate",
                      "educationOrganizationReference",
                      "programReference",
                      "studentReference"
                    ]
                  }
                },
                "schools": {
                  "resourceName": "School",
                  "isDescriptor": false,
                  "isSchoolYearEnumeration": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "isSubclass": true,
                  "subclassType": "domainObjectSubclass",
                  "superclassProjectName": "Ed-Fi",
                  "superclassResourceName": "EducationOrganization",
                  "identityJsonPaths": [ "$.educationOrganizationId" ],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "equalityConstraints": [],
                  "arrayUniquenessConstraints": [],
                  "documentPathsMapping": {
                    "EducationOrganizationId": {
                      "isReference": false,
                      "isPartOfIdentity": true,
                      "isRequired": true,
                      "path": "$.educationOrganizationId"
                    }
                  },
                  "queryFieldMapping": {},
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {
                      "educationOrganizationId": { "type": "integer", "format": "int64" }
                    },
                    "required": [ "educationOrganizationId" ]
                  }
                },
                "programs": {
                  "resourceName": "Program",
                  "isDescriptor": false,
                  "isSchoolYearEnumeration": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "isSubclass": false,
                  "identityJsonPaths": [ "$.educationOrganizationId", "$.programTypeDescriptor" ],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "equalityConstraints": [],
                  "arrayUniquenessConstraints": [],
                  "documentPathsMapping": {
                    "EducationOrganizationId": {
                      "isReference": false,
                      "isPartOfIdentity": true,
                      "isRequired": true,
                      "path": "$.educationOrganizationId"
                    },
                    "ProgramTypeDescriptor": {
                      "isReference": true,
                      "isDescriptor": true,
                      "isPartOfIdentity": true,
                      "isRequired": true,
                      "projectName": "Ed-Fi",
                      "resourceName": "ProgramTypeDescriptor",
                      "path": "$.programTypeDescriptor"
                    }
                  },
                  "queryFieldMapping": {},
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {
                      "educationOrganizationId": { "type": "integer", "format": "int64" },
                      "programTypeDescriptor": { "type": "string", "maxLength": 306 }
                    },
                    "required": [ "educationOrganizationId", "programTypeDescriptor" ]
                  }
                },
                "students": {
                  "resourceName": "Student",
                  "isDescriptor": false,
                  "isSchoolYearEnumeration": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "isSubclass": false,
                  "identityJsonPaths": [ "$.studentUniqueId" ],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "equalityConstraints": [],
                  "arrayUniquenessConstraints": [],
                  "documentPathsMapping": {
                    "StudentUniqueId": {
                      "isReference": false,
                      "isPartOfIdentity": true,
                      "isRequired": true,
                      "path": "$.studentUniqueId"
                    }
                  },
                  "queryFieldMapping": {},
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {
                      "studentUniqueId": { "type": "string", "maxLength": 32 }
                    },
                    "required": [ "studentUniqueId" ]
                  }
                },
                "programTypeDescriptors": {
                  "resourceName": "ProgramTypeDescriptor",
                  "isDescriptor": true,
                  "isSchoolYearEnumeration": false,
                  "isResourceExtension": false,
                  "allowIdentityUpdates": false,
                  "isSubclass": false,
                  "identityJsonPaths": [],
                  "booleanJsonPaths": [],
                  "numericJsonPaths": [],
                  "dateJsonPaths": [],
                  "dateTimeJsonPaths": [],
                  "equalityConstraints": [],
                  "arrayUniquenessConstraints": [],
                  "documentPathsMapping": {},
                  "queryFieldMapping": {},
                  "securableElements": {
                    "Namespace": [],
                    "EducationOrganization": [],
                    "Student": [],
                    "Contact": [],
                    "Staff": []
                  },
                  "authorizationPathways": [],
                  "decimalPropertyValidationInfos": [],
                  "jsonSchemaForInsert": {
                    "type": "object",
                    "properties": {
                      "codeValue": { "type": "string", "maxLength": 20 },
                      "namespace": { "type": "string", "maxLength": 255 }
                    },
                    "required": [ "codeValue", "namespace" ]
                  }
                }
              }
            }
            """;

        private static readonly QualifiedResourceName _studentArtProgramAssociationResource = new(
            "Ed-Fi",
            "StudentArtProgramAssociation"
        );

        private static EffectiveSchemaSet CreateCompositeAbstractResourceSchemaSet()
        {
            var artProgramKeyEntry = new ResourceKeyEntry(
                1,
                _studentArtProgramAssociationResource,
                "5.0.0",
                false
            );
            var generalProgramKeyEntry = new ResourceKeyEntry(
                2,
                new QualifiedResourceName("Ed-Fi", "GeneralStudentProgramAssociation"),
                "5.0.0",
                true
            );
            var edOrgKeyEntry = new ResourceKeyEntry(
                3,
                new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
                "5.0.0",
                true
            );
            var schoolKeyEntry = new ResourceKeyEntry(
                4,
                new QualifiedResourceName("Ed-Fi", "School"),
                "5.0.0",
                false
            );
            var programKeyEntry = new ResourceKeyEntry(
                5,
                new QualifiedResourceName("Ed-Fi", "Program"),
                "5.0.0",
                false
            );
            var studentKeyEntry = new ResourceKeyEntry(
                6,
                new QualifiedResourceName("Ed-Fi", "Student"),
                "5.0.0",
                false
            );
            var descriptorKeyEntry = new ResourceKeyEntry(
                7,
                new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
                "5.0.0",
                false
            );

            var effectiveSchema = new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "1.0.0",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: new string('e', 64),
                ResourceKeyCount: 7,
                ResourceKeySeedHash: new byte[32],
                SchemaComponentsInEndpointOrder:
                [
                    new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false, new string('f', 64)),
                ],
                ResourceKeysInIdOrder:
                [
                    artProgramKeyEntry,
                    generalProgramKeyEntry,
                    edOrgKeyEntry,
                    schoolKeyEntry,
                    programKeyEntry,
                    studentKeyEntry,
                    descriptorKeyEntry,
                ]
            );

            var projectSchema = (JsonObject)JsonNode.Parse(AbstractCompositeReferenceProjectSchemaJson)!;

            return new EffectiveSchemaSet(
                effectiveSchema,
                [new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, projectSchema)]
            );
        }

        /// <summary>
        /// The compiled write-plan column bindings for <c>StudentArtProgramAssociation</c> must include
        /// the <em>renamed</em> reference-backed abstract identity columns.
        /// This assertion is a genuine guard: if the pipeline regressed to the old concatenated names
        /// (e.g. <c>StudentReferenceStudentUniqueId</c>), the <c>Contain</c> checks would fail because
        /// those column names no longer exist in the relational model.
        /// </summary>
        [Test]
        public async Task It_should_compile_write_plan_with_renamed_reference_backed_abstract_identity_columns()
        {
            var schemaSet = CreateCompositeAbstractResourceSchemaSet();
            var compiler = new RuntimeMappingSetCompiler(
                () => schemaSet,
                new MappingSetCompiler(),
                SqlDialect.Pgsql,
                new PgsqlDialectRules()
            );
            var key = compiler.GetCurrentKey();
            var mappingSet = await compiler.CompileAsync(key, CancellationToken.None);

            mappingSet.WritePlansByResource.Should().ContainKey(_studentArtProgramAssociationResource);
            var writePlan = mappingSet.WritePlansByResource[_studentArtProgramAssociationResource];
            var rootTablePlan = writePlan.TablePlansInDependencyOrder.Single(p =>
                p.TableModel.Table.Name == "StudentArtProgramAssociation"
            );

            var columnNames = rootTablePlan.ColumnBindings.Select(b => b.Column.ColumnName.Value).ToList();

            // Reference-backed abstract identity columns must use the renamed convention
            // (Resource_Field) introduced by DMS-1223.
            columnNames.Should().Contain("Student_StudentUniqueId");
            columnNames.Should().Contain("Program_ProgramTypeDescriptor_DescriptorId");
            columnNames.Should().Contain("Program_EducationOrganizationId");
            columnNames.Should().Contain("EducationOrganization_EducationOrganizationId");

            // Must NOT carry the old concatenated names that existed before the rename.
            columnNames.Should().NotContain("StudentReferenceStudentUniqueId");
            columnNames.Should().NotContain("ProgramReferenceProgramTypeDescriptor");
        }
    }

    [TestFixture(SqlDialect.Pgsql)]
    [TestFixture(SqlDialect.Mssql)]
    public class Given_A_Runtime_Collection_Fixture_Missing_Semantic_Identity(SqlDialect dialect)
        : Given_RuntimeMappingSetCompiler
    {
        private Func<Task<MappingSet>> _compile = null!;

        [SetUp]
        public void Setup()
        {
            var schemaSet = RuntimePlanFixtureModelSetBuilder.CreateEffectiveSchemaSet(
                MissingSemanticIdentityFixturePath
            );
            var compiler = CreateCompiler(dialect, accessor: () => schemaSet);
            var key = compiler.GetCurrentKey();

            _compile = () => compiler.CompileAsync(key, CancellationToken.None);
        }

        [Test]
        public async Task It_should_fail_with_the_missing_semantic_identity_diagnostic()
        {
            var exception = (await _compile.Should().ThrowAsync<InvalidOperationException>()).Which;

            exception.Message.Should().Contain("Persisted multi-item scope");
            exception.Message.Should().Contain("$.addresses[*]");
            exception.Message.Should().Contain("Ed-Fi:School");
            exception.Message.Should().Contain("arrayUniquenessConstraints");
        }
    }

    [TestFixture(SqlDialect.Pgsql)]
    [TestFixture(SqlDialect.Mssql)]
    public class Given_A_Runtime_Collection_Fixture_With_Stable_Keys(SqlDialect dialect)
        : Given_RuntimeMappingSetCompiler
    {
        private ResourceWritePlan _writePlan = null!;

        [SetUp]
        public async Task Setup()
        {
            var schemaSet = RuntimePlanFixtureModelSetBuilder.CreateEffectiveSchemaSet(
                PositiveStableKeyFixturePath
            );
            var compiler = CreateCompiler(dialect, accessor: () => schemaSet);
            var key = compiler.GetCurrentKey();
            var mappingSet = await compiler.CompileAsync(key, CancellationToken.None);

            _writePlan = mappingSet.WritePlansByResource[_schoolResource];
        }

        [Test]
        public void It_should_compile_collection_merge_plans_for_true_collection_tables()
        {
            foreach (
                var tableName in new[]
                {
                    "SchoolAddress",
                    "SchoolAddressPeriod",
                    "SchoolExtensionIntervention",
                    "SchoolExtensionInterventionVisit",
                    "SchoolExtensionAddressSponsorReference",
                }
            )
            {
                AssertUsesCollectionMergeContract(tableName);
            }
        }

        [Test]
        public void It_should_keep_non_collection_scopes_on_the_existing_one_to_one_contract()
        {
            var rootPlan = GetTablePlan("School");
            rootPlan.DeleteByParentSql.Should().BeNull();
            rootPlan.CollectionMergePlan.Should().BeNull();

            var rootExtensionPlan = GetTablePlan("SchoolExtension");
            rootExtensionPlan.UpdateSql.Should().NotBeNullOrWhiteSpace();
            rootExtensionPlan.DeleteByParentSql.Should().NotBeNullOrWhiteSpace();
            rootExtensionPlan.CollectionMergePlan.Should().BeNull();

            var collectionExtensionScopePlan = GetTablePlan("SchoolExtensionAddress");
            collectionExtensionScopePlan.UpdateSql.Should().NotBeNullOrWhiteSpace();
            collectionExtensionScopePlan.DeleteByParentSql.Should().NotBeNullOrWhiteSpace();
            collectionExtensionScopePlan.CollectionMergePlan.Should().BeNull();
        }

        private void AssertUsesCollectionMergeContract(string tableName)
        {
            var tablePlan = GetTablePlan(tableName);

            tablePlan.UpdateSql.Should().BeNull();
            tablePlan.DeleteByParentSql.Should().BeNull();
            tablePlan.CollectionMergePlan.Should().NotBeNull();
            tablePlan.CollectionKeyPreallocationPlan.Should().NotBeNull();
        }

        private TableWritePlan GetTablePlan(string tableName)
        {
            return _writePlan.TablePlansInDependencyOrder.Single(tablePlan =>
                string.Equals(tablePlan.TableModel.Table.Name, tableName, StringComparison.Ordinal)
            );
        }
    }
}
