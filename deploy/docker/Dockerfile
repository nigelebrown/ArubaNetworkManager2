# JCF Intelligent Network Manager, SQL Server/Identity backed (no in-container SQL).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Jcf.NetworkManager/Jcf.NetworkManager.csproj src/Jcf.NetworkManager/
RUN dotnet restore src/Jcf.NetworkManager/Jcf.NetworkManager.csproj
COPY src/Jcf.NetworkManager/ src/Jcf.NetworkManager/
RUN dotnet publish src/Jcf.NetworkManager/Jcf.NetworkManager.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    JCF_CONFIG_FILE=/run/jcf-config/config.json
COPY --from=build --chown=1654:1654 /out/ /app/
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["dotnet","Jcf.NetworkManager.dll"]
