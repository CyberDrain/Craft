#!/bin/sh
# Craft container entrypoint.
# Optionally starts the OpenSSH daemon (for Azure App Service SCM access)
# before launching the .NET host. SSH is OFF by default; opt in with
# CRAFT_SSH_ENABLED=true.

set -e

ssh_enabled="${CRAFT_SSH_ENABLED:-false}"

case "$(printf '%s' "$ssh_enabled" | tr '[:upper:]' '[:lower:]')" in
    1|true|yes|on)
        if command -v /usr/sbin/sshd >/dev/null 2>&1; then
            # Azure App Service expects root login with a known password. If the
            # operator hasn't provided one we fall back to the Azure default so
            # `az webapp ssh` works out of the box.
            ssh_password="${CRAFT_SSH_PASSWORD:-Docker!}"
            echo "root:${ssh_password}" | chpasswd
            mkdir -p /run/sshd
            /usr/sbin/sshd
            echo "[Entrypoint] sshd started on port 2222"
        else
            echo "[Entrypoint] CRAFT_SSH_ENABLED=true but openssh-server is not installed in this image"
        fi
        ;;
    *)
        ;;
esac

exec dotnet Craft.dll "$@"
