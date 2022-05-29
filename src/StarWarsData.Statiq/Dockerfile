FROM mcr.microsoft.com/dotnet/runtime:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["StarWarsData.Statiq/StarWarsData.Statiq.csproj", "StarWarsData.Statiq/"]
RUN dotnet restore "StarWarsData.Statiq/StarWarsData.Statiq.csproj"
COPY . .
WORKDIR "/src/StarWarsData.Statiq"
RUN dotnet build "StarWarsData.Statiq.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "StarWarsData.Statiq.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "StarWarsData.Statiq.dll"]
