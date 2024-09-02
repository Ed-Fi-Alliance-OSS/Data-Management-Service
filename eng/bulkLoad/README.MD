# Bulk Upload Testing

These scripts can be used with the Data Management Service or with the ODS/API
Platform, version 7.1 or 72. There are two data set options: Grand Bend (small)
and Southridge (large); in each case, it is possible to run a bootstrap data
load only (descriptors, education organizations, learning standards), or to turn
on a full data load.

## Testing with the Data Management Service

Recommended approach for the DMS: run either `start-local-dms.ps1` or
`start-published-dms.ps1` from the `eng/docker-compose` directory. By default,
this will run the API on `http://localhost:8080`.

Then run one of the following commands:

```shell
# Only the bootstrap data
./Invoke-LoadGrandBend.ps1 -LoadSchoolYear

# ... or a larger data set
./Invoke-LoadSouthridge.ps1 -LoadSchoolYear

# Run the entire data set
./Invoke-LoadSouthridge.ps1 -FullDataSet -LoadSchoolYear

# ... or a larger data set
./Invoke-LoadGrandBend.ps1 -FullDataSet -LoadSchoolYear
```

## Testing with the ODS/API

Recommended approach for the ODS/API: use the `bootstrap.ps1 -Hub` the script in
`Ed-Fi-ODS-Implementation/Docker`. Run without any arguments, this will startup
a single local instance of the ODS/API, running on PostgreSQL. By default, this will
run the API on `http://localhost:8001`.

... having trouble with this :-(

Compared to the DMS instructions: don't use `LoadSchoolYear`, change the
`BaseUrl`, and optionally change the key and secret (default values are shown:
`minimalKey` and `minimalSecret`).

```shell
# Only the bootstrap data
./Invoke-LoadGrandBend.ps1 -BaseUrl http://localhost:8001 -Key minimalKey -Secret minimalSecret

# ... or a larger data set
./Invoke-LoadSouthridge.ps1 -BaseUrl http://localhost:8001 -Key minimalKey -Secret minimalSecret

# Run the entire data set
./Invoke-LoadSouthridge.ps1 -FullDataSet -BaseUrl http://localhost:8001 -Key minimalKey -Secret minimalSecret

# ... or a larger data set
./Invoke-LoadGrandBend.ps1 -FullDataSet -BaseUrl http://localhost:8001 -Key minimalKey -Secret minimalSecret
```

## Maybe unnecessary

If running Southridge against the ODS/API, then make sure that client
application is setup to work with education organization ID 255901. The
following script might be useful _if you have only one API client in the
`EdFi_Admin` database_.

```sql
update dbo.applicationeducationorganizations set educationorganizationid=255901;
```
