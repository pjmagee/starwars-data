FROM mcr.microsoft.com/dotnet/runtime:7.0 AS runtime
WORKDIR /app

FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS aspnet
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS cli-build
WORKDIR /src/

COPY ["src/global.json", "."]

COPY ["src/StarWarsData.Models/StarWarsData.Models.csproj", "StarWarsData.Models/"]
RUN dotnet restore "StarWarsData.Models/StarWarsData.Models.csproj"
COPY ["src/StarWarsData.Services/StarWarsData.Services.csproj", "StarWarsData.Services/"]
RUN dotnet restore "StarWarsData.Services/StarWarsData.Services.csproj"

COPY ["src/StarWarsData.CLI/StarWarsData.CLI.csproj", "StarWarsData.CLI/"]
RUN dotnet restore "StarWarsData.CLI/StarWarsData.CLI.csproj"

COPY ["src/", "."]
RUN dotnet build StarWarsData.CLI/StarWarsData.CLI.csproj -c Release -o /app/build

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS web-build
WORKDIR /src/

COPY ["src/global.json", "."]

COPY ["src/StarWarsData.Models/StarWarsData.Models.csproj", "StarWarsData.Models/"]
RUN dotnet restore "StarWarsData.Models/StarWarsData.Models.csproj"
COPY ["src/StarWarsData.Services/StarWarsData.Services.csproj", "StarWarsData.Services/"]
RUN dotnet restore "StarWarsData.Services/StarWarsData.Services.csproj"

COPY ["src/StarWarsData.Server/StarWarsData.Server.csproj", "StarWarsData.Server/"]
RUN dotnet restore "StarWarsData.Server/StarWarsData.Server.csproj"

COPY ["src/StarWarsData.Client/StarWarsData.Client.csproj", "StarWarsData.Client/"]
RUN dotnet workload restore "StarWarsData.Client/StarWarsData.Client.csproj"
RUN dotnet restore "StarWarsData.Client/StarWarsData.Client.csproj"

COPY ["src/", "."]
RUN dotnet build StarWarsData.Server/StarWarsData.Server.csproj -c Release -o /app/build

FROM cli-build AS cli-publish
RUN dotnet publish StarWarsData.CLI/StarWarsData.CLI.csproj -c Release -o /app/publish

FROM web-build as web-publish
RUN dotnet publish StarWarsData.Server/StarWarsData.Server.csproj -c Release -o /app/publish

FROM runtime AS cli
WORKDIR /app
RUN mkdir /data
COPY --from=cli-publish /app/publish .
ENTRYPOINT ["dotnet", "StarWarsData.CLI.dll"]

FROM aspnet AS web
WORKDIR /app
COPY --from=web-publish /app/publish .
ENTRYPOINT ["dotnet", "StarWarsData.Server.dll"]