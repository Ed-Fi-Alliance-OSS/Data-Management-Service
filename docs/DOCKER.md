# Docker files details

## Dockerfile

    The purpose of this file is to facilitate the setup of Data Management Service(DMS) docker image.
    It utilizes the assets and dlls from "src\frontend\EdFi.DataManagementService.Frontend.AspNetCore", src\backend\EdFi.DataManagementService.Backend.Installer\EdFi.DataManagementService.Backend.Installer
    folders.

### Environment variables details for running the container:

    NEED_DATABASE_SETUP=<Flag (true or false) to decide whether the DMS database setup needs to be executed as part of the container setup>
    POSTGRES_ADMIN_USER=<Admin user to use with database setup>
    POSTGRES_ADMIN_PASSWORD=<Admin password to use with database setup>
    POSTGRES_USER=<Non-admin user to use for accessing the database from the DMS application>
    POSTGRES_PASSWORD=<Non-admin user password>
    POSTGRES_PORT=<Port for postgres server Eg. 5432>
    POSTGRES_HOST=<DNS or IP address of the PostgreSQL Server, i.e. sql.somedns.org Eg. 172.25.32.1>
