environment = "dev"

# azure-container-apps' `make outputs`: the shared server's name has a random
# suffix, so it is copied here rather than derived.
postgres_fqdn = "psql-platform-dev-ayyl.postgres.database.azure.com"

# CNAME and asuid TXT records are on Cloudflare, DNS only (not proxied): the
# managed certificate is validated against the CNAME.
custom_domain_name = "observability.jaywithers.uk"
