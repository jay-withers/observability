"""Configuration.

Shaped like gym-log's `settings.py`, minus its Key Vault resolution: this
repo has no Key Vault by decision (CLAUDE.md), so the one secret, the
passcode, reaches the container as a Container Apps secret in `APP_PASSCODE`,
and locally from the environment or `.env`.
"""

from __future__ import annotations

import os
from functools import lru_cache

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Non-secret configuration. Anything in this class is safe in a log line."""

    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    # Which `Environment` value the tables are filtered to. It goes into KQL,
    # so it is held to a plain slug here rather than escaped at every use.
    environment: str = "dev"

    # The Log Analytics workspace's customer ID (a GUID), not its resource ID.
    # Empty means "no Azure": the app serves demo data instead, under a banner.
    workspace_id: str = ""
    # The user-assigned identity's client id. Empty locally, which is the
    # signal to fall back to DefaultAzureCredential (your `az login`).
    azure_client_id: str = ""

    # Serve generated data instead of querying Log Analytics. Implied when no
    # workspace is configured; set it explicitly to see the demo with one.
    demo: bool = False

    # Query results are kept this long. The tables are written by daily jobs
    # and by deploys, so five minutes stale costs nothing and makes every
    # open after the first instant.
    cache_ttl_seconds: int = 300

    # Where the vendored chart library lives. The image puts it at
    # /app/vendor; locally `make app-vendor` fetches it into app/vendor.
    vendor_dir: str = "vendor"

    # The gate defaults on, so forgetting to configure it fails closed.
    require_passcode: bool = True
    # Signs the session cookie. Unset means it is derived from the passcode,
    # so a deploy or a new replica does not log anyone out (as gym-log).
    cookie_secret: str = ""
    cookie_max_age_seconds: int = 60 * 60 * 24 * 30

    log_level: str = Field(default="INFO")

    @field_validator("environment")
    @classmethod
    def _slug(cls, value: str) -> str:
        if not value or not all(c.isalnum() or c == "-" for c in value):
            raise ValueError("ENVIRONMENT must be letters, digits and hyphens")
        return value

    @property
    def use_demo(self) -> bool:
        return self.demo or not self.workspace_id


@lru_cache(maxsize=1)
def settings() -> Settings:
    return Settings()


def passcode() -> str:
    """The passcode, from the environment first, then `.env`. Empty if neither."""
    from_env = os.environ.get("APP_PASSCODE")
    if from_env:
        return from_env
    from dotenv import dotenv_values

    return dotenv_values(".env").get("APP_PASSCODE") or ""
