-- Creates (or re-passwords) the role Grafana logs in to the shared Postgres
-- server as, and hands it the `grafana` database azure-container-apps
-- created. Run by `make db-role` as the server's Entra administrator, with
-- the password from this repo's Terraform state in the `pw` variable.
--
-- The only password-holding role on the server: Grafana cannot use an Entra
-- token for its own database. Every other role there is Entra.
--
-- Safe to re-run; that is how the password is rotated.
\set ON_ERROR_STOP on

SELECT 'CREATE ROLE grafana LOGIN'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'grafana') \gexec

ALTER ROLE grafana WITH LOGIN PASSWORD :'pw';

-- Postgres 16+ only lets a role give a database to another role it can act
-- as. The Entra administrator created `grafana`, so it may grant itself
-- membership; the ownership change below then succeeds.
GRANT grafana TO CURRENT_USER;

-- Grafana creates and migrates its own tables, so it owns the database and
-- its public schema outright rather than holding a list of grants.
ALTER DATABASE grafana OWNER TO grafana;
ALTER SCHEMA public OWNER TO grafana;
