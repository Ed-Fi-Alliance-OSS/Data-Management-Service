Feature: Health
    Check the health of the application and the database

        @DMS-066
        Scenario: 01 Health
            Given a request health is made to the server
             Then it returns healthy checks
