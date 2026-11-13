#!/usr/bin/env bash
# Sets up the map site on a fresh Debian or Ubuntu server, from a clone of bg3gk/map:
#
#   git clone --recursive https://github.com/bg3gk/map && cd map && sudo deploy/install.sh --domain map.example.org
#
# It installs .NET and Caddy, builds the engine, downloads the databases the release pins
# (`map fetch`), renders the site (`map build`) and starts it behind Caddy, which takes care of
# the certificate. Run it again to update: it pulls nothing by itself - `git pull` first - and
# every step can be repeated. `--from <folder or URL>` fetches the databases from somewhere other
# than the pinned release.
set -euo pipefail

domain=""
from=""
while [ $# -gt 0 ]; do
  case "$1" in
    --domain) domain="$2"; shift 2 ;;
    --from) from="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
if [ -z "$domain" ]; then
  echo "usage: deploy/install.sh --domain <the site's domain> [--from <folder or URL holding the databases>]" >&2
  exit 2
fi
if [ "$(id -u)" -ne 0 ]; then
  echo "run it as root (sudo), it installs packages and a service" >&2
  exit 1
fi
here="$(cd "$(dirname "$0")/.." && pwd)"
cd "$here"

echo "== packages"
apt-get update -q
apt-get install -y -q curl git ca-certificates debian-keyring debian-archive-keyring apt-transport-https gnupg sqlite3

if ! command -v caddy >/dev/null; then
  echo "== Caddy"
  curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/gpg.key | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
  curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt > /etc/apt/sources.list.d/caddy-stable.list
  apt-get update -q
  apt-get install -y -q caddy
fi

if [ ! -x /opt/dotnet/dotnet ]; then
  echo "== .NET"
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --jsonfile "$here/global.json" --install-dir /opt/dotnet
fi
export PATH="/opt/dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_ROOT=/opt/dotnet

echo "== the engine"
git submodule update --init --recursive
dotnet publish engine/src/Map.Cli -c Release -o /opt/map/bin -nologo -v q

id map >/dev/null 2>&1 || useradd --system --home /var/lib/map --shell /usr/sbin/nologin map
mkdir -p /var/lib/map/data /var/lib/map/site
export MAP_DATA=/var/lib/map/data

echo "== the databases"
if [ -n "$from" ]; then
  /opt/map/bin/map fetch --force --from "$from"
elif [ ! -f /var/lib/map/data/gameindex.sqlite ]; then
  /opt/map/bin/map fetch
fi

echo "== the site"
/opt/map/bin/map build --dir /var/lib/map/site --coverage "$here/gatekit/research/coverage"
/opt/map/bin/map check --dir /var/lib/map/site
chown -R map:map /var/lib/map

echo "== the service"
install -m 644 deploy/map.service /etc/systemd/system/map.service
systemctl daemon-reload
systemctl enable --now map
systemctl restart map

sed "s/{\$MAP_DOMAIN}/$domain/" deploy/Caddyfile > /etc/caddy/Caddyfile
mkdir -p /var/log/caddy
systemctl reload caddy || systemctl restart caddy

echo "the site is at https://$domain/ (the certificate may take a minute the first time)"
