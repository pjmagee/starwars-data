FROM mcr.microsoft.com/dotnet/runtime:7.0 AS runtime
WORKDIR /app

FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS aspnet
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS sdk
WORKDIR /src/

COPY ["src/StarWarsData.Models/StarWarsData.Models.csproj", "src/StarWarsData.Models/"]
RUN dotnet restore "src/StarWarsData.Models/StarWarsData.Models.csproj"

COPY ["src/StarWarsData.Services/StarWarsData.Services.csproj", "src/StarWarsData.Services/"]
RUN dotnet restore "src/StarWarsData.Services/StarWarsData.Services.csproj"

COPY ["src/StarWarsData.CLI/StarWarsData.CLI.csproj", "src/StarWarsData.CLI/"]
RUN dotnet restore "src/StarWarsData.CLI/StarWarsData.CLI.csproj"

COPY ["src/StarWarsData.API/StarWarsData.API.csproj", "src/StarWarsData.API/"]
RUN dotnet restore "src/StarWarsData.API/StarWarsData.API.csproj"

COPY ["src/StarWarsData.Statiq/StarWarsData.Statiq.csproj", "src/StarWarsData.Statiq/"]
RUN dotnet restore "src/StarWarsData.Statiq/StarWarsData.Statiq.csproj"

COPY ["src/", "."]

WORKDIR /src/

RUN dotnet restore
RUN dotnet build -c Release -o /app/build

FROM sdk AS publish
RUN dotnet publish -c Release -o /app/publish

FROM runtime AS cli
WORKDIR /app
RUN mkdir /data
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "StarWarsData.CLI.dll"]

FROM aspnet AS api
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "StarWarsData.API.dll"]