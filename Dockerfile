# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# The build runs on the builder's own platform: the published app is portable, so an arm64 image needs no emulation here.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/MatSmtp.Core/MatSmtp.Core.csproj", "src/MatSmtp.Core/"]
COPY ["src/MatSmtp/MatSmtp.csproj", "src/MatSmtp/"]
RUN dotnet restore "src/MatSmtp/MatSmtp.csproj"

COPY src src
# Fall back to today's date when BuildDate is not supplied, so the version never ends in "-".
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/MatSmtp/MatSmtp.csproj" \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE

# ---------------------------------------------------------------------------
# Runtime stage (ASP.NET Core base image)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS="" \
    MATSMTP_WEB_PORT=2525 \
    MATSMTP_DATA=/data \
    TZ=Europe/Berlin \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1

# Configuration (JSON), the SQLite database, the queue (.eml files), the keys of the cookies and secrets, the TLS certificates, scratch space of the
# backup: one volume.
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data
VOLUME ["/data"]

# 2525: web interface. 2025, 2587, 2465: SMTP with STARTTLS, submission with STARTTLS, SMTP over TLS (unprivileged ports inside the container; the compose
# file maps them to 25, 587 and 465 outside).
EXPOSE 2525 2025 2587 2465

COPY --from=build /app/publish .
USER $APP_UID

# The app asks its own /api/health (200 for ok and degraded, 503 for down: a degraded relay is not restarted), so the image needs no curl.
HEALTHCHECK --interval=30s --timeout=8s --start-period=40s --retries=3 \
    CMD ["dotnet", "MatSmtp.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "MatSmtp.dll"]
