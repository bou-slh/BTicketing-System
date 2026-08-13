FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY RapidsolDestek.sln ./
COPY src/RapidsolDestek.Domain/RapidsolDestek.Domain.csproj src/RapidsolDestek.Domain/
COPY src/RapidsolDestek.Infrastructure/RapidsolDestek.Infrastructure.csproj src/RapidsolDestek.Infrastructure/
COPY src/RapidsolDestek.Web/RapidsolDestek.Web.csproj src/RapidsolDestek.Web/
RUN dotnet restore src/RapidsolDestek.Web/RapidsolDestek.Web.csproj
COPY src/ src/
RUN dotnet publish src/RapidsolDestek.Web/RapidsolDestek.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RapidsolDestek.Web.dll"]
