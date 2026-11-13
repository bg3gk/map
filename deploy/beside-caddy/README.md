# Beside a site that already has Caddy

When the server already runs Caddy for another site - in Docker, with an `import` of a folder of
extra sites - the map does not need a Caddy of its own:

1. `map serve` runs as a service on the host, listening on the Docker network's gateway
   (`172.18.0.1` here), which that Caddy can reach and the internet cannot: `map.service`.
2. One file joins the other Caddy's folder of extra sites: `map.caddy`. Validate the whole
   configuration with that Caddy, then reload it gracefully; the other site keeps serving.

Nothing of the other site is touched. Adjust the gateway address and the domain to the server.

## How it was done on a server that already ran Caddy in Docker

- The site is rendered on the machine that has the databases (`map build`, `map check`) and the
  two files are copied to `/var/lib/map/site`; the server has 2 cores and shares its memory with
  the other site, so it is not asked to render 3.6 million pages itself.
- The databases (for `/api/refs`) were copied as a release and unpacked with
  `map fetch --from /var/lib/map/release` into `/var/lib/map/data`.
- The engine is a self-contained Linux build
  (`dotnet publish engine/src/Map.Cli -c Release -r linux-x64 --self-contained true`), unpacked
  into `/opt/map/bin`. A tarball made on Windows loses the executable bit: `chmod +x /opt/map/bin/map`.
- ufw lets only the Docker network reach the service, as it already did for the other site's
  service on port 3000:
  `ufw allow in on <bridge> from 172.18.0.0/16 to 172.18.0.1 port 8765 proto tcp`.
- `map.caddy` went into the folder that Caddy `import`s its extra sites from; then
  `docker exec <caddy container> caddy validate ...` and `caddy reload ...` with
  that Caddy's own `--config` and `--adapter caddyfile`.
