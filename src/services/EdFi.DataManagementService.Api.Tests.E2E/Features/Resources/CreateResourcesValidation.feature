# This is a rough draft feature for future use.
@ignore
Feature: Resources Create Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized

        @ignore
        Scenario: Verify new resource can be created successfully
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  | setting          | value                                          |
                  | codeValue        | Sick Leave                                     |
                  | description      | Sick Leave                                     |
                  | namespace        | uri://ed-fi.org/AbsenceEventCategoryDescriptor |
                  | shortDescription | Sick Leave                                     |
             Then it should respond with 201
              And the record can be retrieved with a GET request

        @ignore
        Scenario: Verify error handling with POST using invalid data
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  | setting          | value                                          |
                  | codeValue        |                                                |
                  | description      | Wrong Value                                    |
                  | namespace        | uri://ed-fi.org/AbsenceEventCategoryDescriptor |
                  | shortDescription | Wrong Value                                    |
             Then it should respond with 400
              And the response message includes:
                  | Validation of 'AbsenceEventCategoryDescriptor' failed.\r\n\tCodeValue is required.\n |

        @ignore
        Scenario: Verify error handling with POST using invalid data Forbidden
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  | setting          | value            |
                  | codeValue        | Wrong Value      |
                  | description      | Wrong Value      |
                  | namespace        | uri://.org/wrong |
                  | shortDescription | Wrong Value      |
             Then it should respond with 403
              And the response message includes:
                  | Access to the resource item could not be authorized based on the caller's NamespacePrefix claims: 'uri://ed-fi.org', 'uri://gbisd.org', 'uri://tpdm.ed-fi.org'. |

        @ignore
        Scenario: Verify error handling with POST using empty body
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  | setting          | value |
                  | codeValue        |       |
                  | description      |       |
                  | namespace        |       |
                  | shortDescription |       |
             Then it should respond with 403
              And the response message includes:
                  | Access to the resource item could not be authorized because the Namespace of the resource is empty. |

        @ignore
        Scenario: Verify POST of existing record without changes
            #an existing record to
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  | setting          | value                                          |
                  | codeValue        | Sick Leave                                     |
                  | description      | Sick Leave                                     |
                  | namespace        | uri://ed-fi.org/AbsenceEventCategoryDescriptor |
                  | shortDescription | Sick Leave                                     |
             Then it should respond with 200
              And the record can be retrieved with a GET request

        @ignore
        Scenario: Verify POST of existing record (change non-key field) works
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  | setting          | value                                          |
                  | codeValue        | Sick Leave                                     |
                  | description      | Sick Leave Edit                                |
                  | namespace        | uri://ed-fi.org/AbsenceEventCategoryDescriptor |
                  | shortDescription | SL                                             |
             Then it should respond with 200
