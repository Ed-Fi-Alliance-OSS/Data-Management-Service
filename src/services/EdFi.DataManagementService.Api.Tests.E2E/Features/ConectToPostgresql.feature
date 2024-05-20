Feature: ConectToPostgresql
    Verify the connection to the Postgresql database


Scenario: Verify database connection is successful
  Given a running PostgreSQL database
	When I connect to the database
	Then the connection should be successful
