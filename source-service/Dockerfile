FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble AS build
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
COPY global.json Directory.Packages.props ./
COPY source-service/QTools.Source.csproj source-service/
RUN dotnet restore source-service/QTools.Source.csproj -r linux-x64
COPY source-service/ source-service/
RUN dotnet publish source-service/QTools.Source.csproj -c Release -r linux-x64 \
    -o /out --no-restore /p:DebugType=none

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.400-noble
WORKDIR /app
COPY --from=build /out/QTools.Source ./QTools.Source
USER root
RUN mkdir -p /data && chown 65532:65532 /data
USER 65532:65532
ENV QTOOLS_SOURCE_ADDR=0.0.0.0:58088 \
    QTOOLS_SOURCE_DATA=/data \
    QTOOLS_SOURCE_SECURE_COOKIE=false
EXPOSE 58088
VOLUME ["/data"]
ENTRYPOINT ["/app/QTools.Source"]
