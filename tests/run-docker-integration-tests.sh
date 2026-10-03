#!/usr/bin/env sh
# Runs the opt-in Docker integration tests inside a .NET SDK container that talks to the local
# Docker daemon through its socket.
#
# Instance containers publish no ports: their databases are reachable only on a Docker network.
# The database and backup integration tests connect to them over that network, so the test
# process has to be on it. The backup tests also run the real pg_dump and mysqldump, which the
# container has installed. On a Linux Docker host with those two programs, a plain
# `AURORA_DOCKER_TESTS=1 dotnet test` works too; on Docker Desktop (macOS, Windows) container
# networks are not reachable from the host, and this script is the way to run them.
#
# The S3 backup tests start their own LocalStack container (S3 only, no published port) on the
# test network; its image, localstack/localstack:4.4.0 unless AURORA_LOCALSTACK_IMAGE says
# otherwise, is pulled on first use. No AWS account or credentials are involved.
#
# Usage: tests/run-docker-integration-tests.sh [extra `dotnet test` arguments]
#   e.g. tests/run-docker-integration-tests.sh --filter "FullyQualifiedName~S3BackupIntegration"
set -eu

root=$(cd "$(dirname "$0")/.." && pwd)
image=aurora-db-manager-tests

# The SDK plus the dump programs, matching the engine versions the tests provision. Built once
# and then served from Docker's cache.
docker build --quiet --tag "$image" - >/dev/null <<'DOCKERFILE'
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client-16 mysql-client \
 && rm -rf /var/lib/apt/lists/*
DOCKERFILE

# The sources are mounted read-only and copied without build output, so the container's build
# never mixes with the host's bin/ and obj/ directories. ("bin?Debug" is a directory literally
# named bin\Debug that the EF Core tools leave behind on macOS; MSBuild on Linux chokes on it.)
exec docker run --rm \
  -v "$root":/src:ro \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v aurora-db-manager-test-nuget:/root/.nuget/packages \
  -e AURORA_DOCKER_TESTS=1 \
  -e AURORA_LOCALSTACK_IMAGE="${AURORA_LOCALSTACK_IMAGE:-}" \
  "$image" \
  sh -c '
    set -eu
    mkdir /work
    cd /src
    tar -cf - --exclude="bin" --exclude="bin?Debug" --exclude=obj --exclude=.git --exclude=.aurora . | tar -xf - -C /work
    cd /work
    if [ "$#" -eq 0 ]; then set -- --filter "Category=DockerIntegration"; fi
    exec dotnet test "$@"
  ' sh "$@"
