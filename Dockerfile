FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["InternalCommunicationApp/InternalCommunicationApp.csproj", "InternalCommunicationApp/"]
RUN dotnet restore "InternalCommunicationApp/InternalCommunicationApp.csproj"

COPY . .
WORKDIR "/src/InternalCommunicationApp"
RUN dotnet publish "InternalCommunicationApp.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "InternalCommunicationApp.dll"]
