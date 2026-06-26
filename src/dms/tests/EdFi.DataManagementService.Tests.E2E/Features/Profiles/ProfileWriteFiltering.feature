Feature: Profile Write Filtering
    As an API client with a profile assigned
    I want my POST/PUT requests to have out-of-profile fields silently stripped,
    while submitted collection items that fail a profile value filter are rejected
    So that only allowed data is persisted and value-filter violations surface as errors

    Rule: IncludeOnly WriteContentType silently strips fields not in the allowed list

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        @relational-backend
        @relational-ci-shard-3
        Scenario: 01 POST with IncludeOnly write profile silently strips excluded fields
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000601,
                      "nameOfInstitution": "Write Filter Test School IncludeOnly",
                      "shortNameOfInstitution": "WFTSIO",
                      "webSite": "https://should-be-stripped.example.com",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "schoolId, nameOfInstitution, shortNameOfInstitution"
             And the response body should not contain fields "webSite"

        @relational-backend
        @relational-ci-shard-3
        Scenario: 02 POST with IncludeOnly write profile preserves identity and allowed fields
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000602,
                      "nameOfInstitution": "Write Filter Preserve Test",
                      "shortNameOfInstitution": "WFPT",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, nameOfInstitution, shortNameOfInstitution"

    Rule: ExcludeOnly WriteContentType silently strips fields in the excluded list

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        @relational-backend
        @relational-ci-shard-3
        Scenario: 03 POST with ExcludeOnly write profile silently strips excluded fields
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000603,
                      "nameOfInstitution": "Write Filter Test School ExcludeOnly",
                      "shortNameOfInstitution": "WFTSEO",
                      "webSite": "https://should-be-stripped.example.com",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "schoolId, nameOfInstitution"
             And the response body should not contain fields "webSite, shortNameOfInstitution"

        @relational-backend
        @relational-ci-shard-3
        Scenario: 04 POST with ExcludeOnly write profile preserves non-excluded fields
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000604,
                      "nameOfInstitution": "Write Filter Preserve Non-Excluded",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "nameOfInstitution, educationOrganizationCategories, gradeLevels"

    Rule: Collection item filter on WriteContentType rejects non-matching submitted items

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-GradeLevelFilter" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade                   |

        @relational-backend
        @relational-ci-shard-3
        Scenario: 05 POST with collection item filter rejects submitted non-matching items
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-GradeLevelFilter" for resource "School" with body
                  """
                  {
                      "schoolId": 99000605,
                      "nameOfInstitution": "Write Collection Filter Test School",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          },
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          },
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 400
             And the response body should have error type "urn:ed-fi:api:bad-request:data-validation-failed"

        @relational-backend
        @relational-ci-shard-3
        Scenario: 06 POST with collection item filter rejects a submitted non-matching item
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-GradeLevelFilter" for resource "School" with body
                  """
                  {
                      "schoolId": 99000606,
                      "nameOfInstitution": "Write Collection Exclude Test School",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          },
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 400
             And the response body should have error type "urn:ed-fi:api:bad-request:data-validation-failed"

    Rule: A hidden non-identity reference or descriptor to a nonexistent target is accepted and omitted, not resolved

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        @relational-backend
        @relational-ci-shard-3
        Scenario: 07 POST with IncludeOnly write profile accepts and omits a hidden reference whose target does not exist
            # localEducationAgencyReference is an optional, non-identity School reference that is NOT in
            # this IncludeOnly write profile, so the shaper strips it. References are extracted
            # from the raw submitted body (DocumentInfo), so the backend must drop the hidden reference
            # from resolution rather than reject it as unresolved. The submitted localEducationAgencyId
            # 90000099 intentionally does not exist; without the profile-shaped reference filter
            # (ProfileWriteReferenceFilter) this POST would fail with an unresolved-reference 409 instead
            # of succeeding and omitting the reference, matching legacy ODS behavior.
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000607,
                      "nameOfInstitution": "Hidden Reference Nonexistent Target School",
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 90000099
                      },
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should not contain fields "localEducationAgencyReference"

        @relational-backend
        @relational-ci-shard-3
        Scenario: 08 POST with IncludeOnly write profile accepts and omits a hidden descriptor whose value does not exist
            # schoolTypeDescriptor is an optional, non-identity School descriptor that is NOT in this
            # IncludeOnly write profile, so the shaper strips it. Descriptor references are extracted
            # from the raw submitted body and filtered separately from document references
            # (RelationalDocumentStoreRepository.ResolveProfileShapedDescriptors), so the backend must drop
            # the hidden descriptor from resolution rather than reject it as unresolved. The submitted
            # SchoolTypeDescriptor#Nonexistent value intentionally does not exist; without the profile-shaped
            # descriptor filter this POST would fail with an unresolved-descriptor 409 instead of succeeding
            # and omitting the descriptor, matching legacy ODS behavior.
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000608,
                      "nameOfInstitution": "Hidden Descriptor Nonexistent Value School",
                      "schoolTypeDescriptor": "uri://ed-fi.org/SchoolTypeDescriptor#Nonexistent",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should not contain fields "schoolTypeDescriptor"
