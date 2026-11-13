# The map site in a container, for a host where Docker is easier than install.sh:
#
#   docker build -f deploy/Dockerfile -t gatekit-map .
#   docker run -d -p 8765:8765 -v map-data:/data gatekit-map
#
# The first start downloads the databases the release pins and renders the site into the volume;
# later starts reuse them. Put a reverse proxy with a certificate in front, as deploy/Caddyfile does.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish engine/src/Map.Cli -c Release -o /app -nologo -v q

FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=build /app /app
COPY --from=build /src/gatekit/research/coverage /coverage
COPY --from=build /src/release.json /app/release.json
COPY --from=build /src/Map.slnx /app/Map.slnx
ENV MAP_DATA=/data/databases DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME /data
EXPOSE 8765
# MAP_FETCH_FROM fetches the databases from a folder or URL instead of the pinned release.
ENTRYPOINT ["/bin/sh", "-c", "[ -f /data/site/pages.sqlite ] || { /app/map fetch ${MAP_FETCH_FROM:+--from \"$MAP_FETCH_FROM\"} && /app/map build --dir /data/site --coverage /coverage; } && exec /app/map serve --dir /data/site --host 0.0.0.0 --port 8765"]
