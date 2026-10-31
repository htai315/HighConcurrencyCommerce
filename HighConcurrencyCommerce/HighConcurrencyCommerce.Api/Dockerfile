FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY HighConcurrencyCommerce/HighConcurrencyCommerce.slnx HighConcurrencyCommerce/
COPY HighConcurrencyCommerce/HighConcurrencyCommerce.Api/HighConcurrencyCommerce.Api.csproj HighConcurrencyCommerce/HighConcurrencyCommerce.Api/
COPY HighConcurrencyCommerce/HighConcurrencyCommerce.Application/HighConcurrencyCommerce.Application.csproj HighConcurrencyCommerce/HighConcurrencyCommerce.Application/
COPY HighConcurrencyCommerce/HighConcurrencyCommerce.Domain/HighConcurrencyCommerce.Domain.csproj HighConcurrencyCommerce/HighConcurrencyCommerce.Domain/
COPY HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/HighConcurrencyCommerce.Infrastructure.csproj HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/
RUN dotnet restore HighConcurrencyCommerce/HighConcurrencyCommerce.Api/HighConcurrencyCommerce.Api.csproj

COPY HighConcurrencyCommerce/ HighConcurrencyCommerce/
COPY database/ database/
RUN dotnet publish HighConcurrencyCommerce/HighConcurrencyCommerce.Api/HighConcurrencyCommerce.Api.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
RUN apk add --no-cache curl \
    && mkdir -p /var/lib/hcc/keys \
    && chown -R app:app /var/lib/hcc
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HighConcurrencyCommerce.Api.dll"]

