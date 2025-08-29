Feature: ClaimSet Upload and Management via CMS
    # This feature demonstrates claim set upload and management operations
    # through the Configuration Management Service (CMS) running on port 8081
    # Note: Dynamic authorization changes require new token generation after upload

    Scenario: 01 Demonstrate initial restriction - Limited claim set cannot access student resources
        # Start with a restricted claim set that cannot access students
        Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
        # The original restricted claim set should deny access to students
        When a GET request is made to "/ed-fi/students"
        Then it should respond with 403
         And the response body is
            """
            {
               "detail": "Access to the resource could not be authorized.",
               "type": "urn:ed-fi:api:security:authorization:",
               "title": "Authorization Denied",
               "status": 403,
               "validationErrors": {},
               "errors": []
            }
            """
        When a POST request is made to "/ed-fi/students" with
            """
            {
                "studentUniqueId": "S001",
                "firstName": "John",
                "lastSurname": "Doe",
                "birthDate": "2010-01-01"
            }
            """
        Then it should respond with 403

    Scenario: 02 Upload new claim set that grants student access via CMS
        # Switch back to restricted claim set
        Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
        # Upload a new claim set via CMS that includes student permissions for the same client
        When a claim set is uploaded to CMS that grants student access to "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
        Then the upload should be successful

    Scenario: 03 Verify SIS Vendor retains student access
        # Switch to SIS Vendor which has full access
        Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
        # SIS Vendor should be able to access students
        When a POST request is made to "/ed-fi/students" with
            """
            {
                "studentUniqueId": "S001",
                "firstName": "John",
                "lastSurname": "Doe",
                "birthDate": "2010-01-01"
            }
            """
        Then it should respond with 201
        When a GET request is made to "/ed-fi/students"
        Then it should respond with 200
         And the response should contain the created student
        
    Scenario: 04 Verify SIS Vendor can create schools
        # SIS Vendor should be able to create schools
        Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
        When a POST request is made to "/ed-fi/schools" with
            """
            {
                "schoolId": 255901002,
                "nameOfInstitution": "Test School 2",
                "educationOrganizationCategories": [
                    {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"}
                ],
                "gradeLevels": [
                    {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"}
                ]
            }
            """
        Then it should respond with 201

    Scenario: 05 Test DMS claimset reload functionality
        # Upload a new claimset to CMS that grants additional access
        Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
        When a claim set is uploaded to CMS that grants student access to "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
        Then the upload should be successful
        
        # Call DMS reload-claimsets endpoint to reload from CMS
        When a POST request is made to DMS "/management/reload-claimsets"
        Then the DMS reload should be successful
        
        # Call DMS view-claimsets endpoint to verify the new claimset is loaded
        When a GET request is made to DMS "/management/view-claimsets"
        Then the DMS view should be successful
         And the claimsets view should contain "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
        
        # Test that authorization behavior changes with the new claimset
        # The claimset should now allow student access
        Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
        When a GET request is made to "/ed-fi/students"
        Then it should respond with 200
        When a POST request is made to "/ed-fi/students" with
            """
            {
                "studentUniqueId": "S003",
                "firstName": "Test",
                "lastSurname": "Student",
                "birthDate": "2010-03-01"
            }
            """
        Then it should respond with 201

    Scenario: 06 Test DMS claimset reload fetches current state from CMS
        # Ensure we have the enhanced claimset with student access uploaded to CMS
        # This simulates the state after Scenario 02 upload
        When a claim set is uploaded to CMS that grants student access to "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
        Then the upload should be successful
        
        # Call DMS reload-claimsets to fetch current state from CMS
        When a POST request is made to DMS "/management/reload-claimsets"
        Then the DMS reload should be successful
        
        # Verify that DMS now has the current state from CMS (with student access)
        Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
        When a GET request is made to "/ed-fi/students"
        Then it should respond with 200
        When a POST request is made to "/ed-fi/students" with
            """
            {
                "studentUniqueId": "S004",
                "firstName": "Reload",
                "lastSurname": "Test",
                "birthDate": "2010-04-01"
            }
            """
        Then it should respond with 201