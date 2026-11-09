FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["AP1/AP1/AP1.csproj", "AP1/AP1/"]
RUN dotnet restore "AP1/AP1/AP1.csproj"

COPY . .
WORKDIR /src/AP1/AP1
RUN dotnet publish "AP1.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AP1.dll"]
