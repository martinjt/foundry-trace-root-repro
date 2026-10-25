FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FixedReplyAgent.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Foundry probes 8088; AgentHost listens there unless PORT says otherwise.
EXPOSE 8088
ENTRYPOINT ["dotnet", "FixedReplyAgent.dll"]
