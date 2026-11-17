# The .NET AI host. Build from the dotnet folder:
#   docker build -t hros-ai-host dotnet
# Set Ai__ChatUrl, Ai__EmbedUrl and Ai__ApiKey (the GPU machine) and Service__ApiKey at run time.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/HrOs.Ai/HrOs.Ai.csproj src/HrOs.Ai/
COPY src/HrOs.Ai.Host/HrOs.Ai.Host.csproj src/HrOs.Ai.Host/
RUN dotnet restore src/HrOs.Ai.Host/HrOs.Ai.Host.csproj
COPY src src
RUN dotnet publish src/HrOs.Ai.Host/HrOs.Ai.Host.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# Inside the container it must listen on every interface; the compose network keeps it private.
ENV ASPNETCORE_URLS=http://0.0.0.0:8200 \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8200
ENTRYPOINT ["dotnet", "HrOs.Ai.Host.dll"]
