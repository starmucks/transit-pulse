# Multi-stage publish for one of the four demo units (ARG PROJECT / ASSEMBLY).
# Build from the repository root, e.g.:
#   docker build -f deploy/demo/Dockerfile --build-arg PROJECT=src/TransitPulse.Api/TransitPulse.Api.csproj --build-arg ASSEMBLY=TransitPulse.Api -t transitpulse-api .
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json TransitPulse.slnx ./
COPY src/ ./src/
ARG PROJECT=src/TransitPulse.Api/TransitPulse.Api.csproj
RUN dotnet restore "$PROJECT" \
    && dotnet publish "$PROJECT" -c Release -o /app/publish --no-restore

FROM ${RUNTIME_IMAGE} AS final
WORKDIR /app
ARG ASSEMBLY=TransitPulse.Api
ENV DOTNET_ASSEMBLY=${ASSEMBLY}.dll
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$DOTNET_ASSEMBLY\""]
