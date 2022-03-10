#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
#FROM mcr.microsoft.com/dotnet/sdk:6.0 AS base
WORKDIR /app
EXPOSE 80
#EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["testapi/testapi.csproj", "testapi/"]
RUN dotnet restore "testapi/testapi.csproj"
COPY . .
WORKDIR "/src/testapi"
RUN dotnet build "testapi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "testapi.csproj" -c Release -o /app/publish

RUN dotnet tool install --global dotnet-counters

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=publish /root/.dotnet /root/.dotnet
ENV PATH="/root/.dotnet/tools:${PATH}"

ENV ASPNETCORE_URLS=http://+:80
ENTRYPOINT ["dotnet", "testapi.dll"]