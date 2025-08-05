CREATE TABLE if not exists dmscs.openiddict_authorization (
id uuid NOT NULL,
application_id uuid NOT NULL,
subject text NOT NULL,
status text NOT NULL,
type text NOT NULL,
CONSTRAINT openiddict_authorization_pkey PRIMARY KEY (id)
)
USING heap;

comment on Table dmscs.openiddict_authorization is 'OpenIddict authorizations storage.';

comment on Column dmscs.openiddict_authorization.id is 'Authorization unique identifier.';

comment on Column dmscs.openiddict_authorization.application_id is 'Associated application id.';

comment on Column dmscs.openiddict_authorization.subject is 'Subject (user or client id).';

comment on Column dmscs.openiddict_authorization.status is 'Authorization status.';

comment on Column dmscs.openiddict_authorization.type is 'Authorization type.';
