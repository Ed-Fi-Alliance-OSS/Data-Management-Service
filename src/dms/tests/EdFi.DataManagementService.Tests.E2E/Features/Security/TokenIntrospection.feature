Feature: JWT Token Introspection
    Other than the Discovery endpoint, all HTTP requests against DMS endpoints
    require a JSON Web Token (JWT) for authorizating the request. The DMS will
    perform self-inspection to confirm the validity of the token, rather than
    querying the OAuth provider.

  Rule: Discovery API does not require an authorization token
            Given there is no Authorization header

    @API-213
    Scenario: Accept root endpoint requests that do not contain a token
      When a GET request is made to "/"
      Then it should respond with 200

    @API-214
    Scenario: Accept dependencies endpoint requests that do not contain a token
      When a GET request is made to "/metadata/dependencies"
      Then it should respond with 200

    @API-215
    Scenario: Accept OpenAPI specifications endpoint requests that do not contain a token
      When a GET request is made to "/metadata/specifications"
      Then it should respond with 200

    @API-216
    Scenario: Accept XSD endpoint requests that do not contain a token
      When a GET request is made to "/metadata/xsd"
      Then it should respond with 200

  Rule: Resource API does not accept requests without a token
            Given there is no Authorization header

    @API-217
    Scenario: Reject a Resource endpoint GET request that does not contain a token
      When a GET request is made to "/ed-fi/academicWeeks"
      Then it should respond with 401

    @API-218
    Scenario: Reject a Resource endpoint POST request that does not contain a token
      When a POST request is made to "/ed-fi/academicWeeks" with
        """
        {
         "weekIdentifier": "WeekIdentifier1",
         "schoolReference": {
           "schoolId": 9999
         },
         "beginDate": "2023-09-11",
         "endDate": "2023-09-11",
         "totalInstructionalDays": 300
        }
        """
      Then it should respond with 401

    @API-219
    Scenario: Reject a Resource endpoint PUT request that does not contain a token
      When a PUT request is made to "/ed-fi/academicWeeks/1" with
        """
             {
              "weekIdentifier": "WeekIdentifier1",
              "schoolReference": {
                "schoolId": 9999
              },
              "beginDate": "2023-09-11",
              "endDate": "2023-09-11",
              "totalInstructionalDays": 300
             }
        """
      Then it should respond with 401

    @API-220
    Scenario: Reject a Resource endpoint DELETE request that does not contain a token
      When a DELETE request is made to "/ed-fi/academicWeeks/1"
      Then it should respond with 401

  Rule: Resource API accepts a valid token

    @API-229
    Scenario: Accept a Resource endpoint GET request where the token is valid
      Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
      When a GET request is made to "/ed-fi/academicWeeks"
      Then it should respond with 200

    Scenario: Reject a Resource endpoint GET request where the token signature is manipulated
      Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
      And the token signature is manipulated
      When a GET request is made to "/ed-fi/academicWeeks"
      Then it should respond with 401

    @DMS-823
    Scenario: Verify JWT token contains dmsInstanceIds claim
      Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
      Then the JWT token should contain the dmsInstanceIds claim

  Rule: Token introspection endpoint returns authorization information

    Background:
      Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255950"
      And the system has these "educationServiceCenters"
        | educationServiceCenterId | nameOfInstitution | categories                                                                                                     |
        |                   255950 | ESC Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC"} ] |
      And the system has these "localEducationAgencies"
        | localEducationAgencyId | nameOfInstitution | categories                                                                                                     | educationServiceCenterReference        | localEducationAgencyCategoryDescriptor                                                |
        |                 255901 | LEA Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#LEA"} ] | {"educationServiceCenterId": "255950"} | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district" |
      And the system has these "schools"
        | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference      |
        | 255901001 | School Test       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | {"localEducationAgencyId":255901 } |
      And the system has these "organizationDepartments"
        | organizationDepartmentId | nameOfInstitution | categories                                                                                                                        | parentEducationOrganizationReference |
        |                  2559011 | OD Test           | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#organizationDepartment"} ] | {"educationOrganizationId":255901 }  |
      And the system has these "communityProviders"
        | communityProviderId | nameOfInstitution | categories                                                                                                                   | providerStatusDescriptor                        |  | providerCategoryDescriptor                        |
        |            19255901 | CP Test           | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#communityProvider"} ] | "uri://ed-fi.org/providerStatusDescriptor#Test" |  | "uri://ed-fi.org/providerCategoryDescriptor#Test" |

    Scenario: 01 Valid token returns authorization information including education organization hierarchy
      Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with profiles "E2E-Test-School-IncludeOnly, E2E-Test-School-IncludeOnly-Alt" and namespacePrefixes "uri://ed-fi.org, uri://ns2.org" and educationOrganizationIds "255950, 19255901"
      When a POST request is made to "/oauth/token_info" with
        """
        {
            "tOkEn": "{token}"
        }
        """
      Then it should respond with 200
      And the token_info response body is
        """
        {
          "active": true,
          "namespace_prefixes": [
            "uri://ed-fi.org",
            "uri://ns2.org"
          ],
          "education_organizations": [
            {
              "education_organization_id": 255901,
              "name_of_institution": "LEA Test",
              "type": "edfi.LocalEducationAgency",
              "local_education_agency_id": 255901,
              "education_service_center_id": 255950
            },
            {
              "education_organization_id": 255950,
              "name_of_institution": "ESC Test",
              "type": "edfi.EducationServiceCenter",
              "education_service_center_id": 255950
            },
            {
              "education_organization_id": 2559011,
              "name_of_institution": "OD Test",
              "type": "edfi.OrganizationDepartment",
              "organization_department_id": 2559011,
              "local_education_agency_id": 255901,
              "education_service_center_id": 255950
            },
            {
              "education_organization_id": 19255901,
              "name_of_institution": "CP Test",
              "type": "edfi.CommunityProvider",
              "community_provider_id": 19255901
            },
            {
              "education_organization_id": 255901001,
              "name_of_institution": "School Test",
              "type": "edfi.School",
              "school_id": 255901001,
              "local_education_agency_id": 255901,
              "education_service_center_id": 255950
            }
          ],
          "assigned_profiles": [
            "E2E-Test-School-IncludeOnly",
            "E2E-Test-School-IncludeOnly-Alt"
          ],
          "claim_set": {
            "name": "E2E-NameSpaceBasedClaimSet"
          },
          "resources": [
            {
              "resource": "/ed-fi/absenceEventCategoryDescriptors",
              "operations": [
                "Create",
                "Read",
                "Update",
                "Delete",
                "ReadChanges"
              ]
            },
            {
              "resource": "/ed-fi/schoolYearTypes",
              "operations": [
                "Create",
                "Read",
                "Update",
                "Delete",
                "ReadChanges"
              ]
            },
            {
              "resource": "/ed-fi/surveys",
              "operations": [
                "Create",
                "Read",
                "Update",
                "Delete",
                "ReadChanges"
              ]
            }
          ],
          "services": []
        }
        """

    Scenario: 02 Missing token in request body returns bad request error
      When a POST request is made to "/oauth/token_info" with
        """
        { }
        """
      Then it should respond with 400
      And the token_info response body is
        """
        {
          "detail": "An invalid token was provided",
          "type": "urn:ed-fi:api:bad-request",
          "title": "Bad Request",
          "status": 400,
          "validationErrors": {},
          "errors": [
            "The token was not present, or was not processable."
          ]
        }
        """

    Scenario: 03 Token mismatch between Authorization header and request body returns bad request error
      When a POST request is made to "/oauth/token_info" with
        """
        {
            "Token": "non-matching token"
        }
        """
      Then it should respond with 400
      And the token_info response body is
        """
        {
          "detail": "An invalid token was provided",
          "type": "urn:ed-fi:api:bad-request",
          "title": "Bad Request",
          "status": 400,
          "validationErrors": {},
          "errors": [
            "The Authorization header token does not match the token in the request body."
          ]
        }
        """
