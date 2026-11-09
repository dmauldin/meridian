# One build stage, one small runtime image per service:
#   docker build -f deploy/Dockerfile --target zone -t meridian/zone .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig Meridian.slnx ./
COPY src/ src/
RUN dotnet publish src/Meridian.Server.Auth -c Release -o /out/auth --nologo \
 && dotnet publish src/Meridian.Server.Gateway -c Release -o /out/gateway --nologo \
 && dotnet publish src/Meridian.Server.Zone -c Release -o /out/zone --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS auth
WORKDIR /app
COPY --from=build /out/auth .
USER $APP_UID
EXPOSE 5080/tcp
ENTRYPOINT ["dotnet", "Meridian.Server.Auth.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS gateway
WORKDIR /app
COPY --from=build /out/gateway .
USER $APP_UID
EXPOSE 7777/udp
ENTRYPOINT ["dotnet", "Meridian.Server.Gateway.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS zone
WORKDIR /app
COPY --from=build /out/zone .
USER $APP_UID
EXPOSE 7800/udp
ENTRYPOINT ["dotnet", "Meridian.Server.Zone.dll"]
