#!/usr/bin/env sh
# Runs the opt-in Docker integration tests inside a .NET SDK container that talks to the local
# Docker daemon through its socket.
#
# Instance containers publish no ports: their databases are reachable only on a Docker network.
# The database integration tests connect to them over that network, so the test process has to be
# on it. On a Linux Docker host a plain `AURORA_DOCKER_TESTS=1 dotnet test` works too; on Docker
# Desktop (macOS, Windows) container networks are not reachable from the host, and this script is
# the way to run them.
#
# Usage: tests/run-docker-integration-tests.sh [extra `dotnet test` arguments]
#   e.g. tests/run-docker-integration-tests.sh --filter "FullyQualifiedName~DatabaseOperations"
set -eu

root=$(cd "$(dirname "$0")/.." && pwd)

# The sources are mounted read-only and copied without build output, so the container's build
# never mixes with the host's bin/ and obj/ directories. ("bin?Debug" is a directory literally
# named bin\Debug that the EF Core tools leave behind on macOS; MSBuild on Linux chokes on it.)
exec docker run --rm \
  -v "$root":/src:ro \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v aurora-db-manager-test-nuget:/root/.nuget/packages \
  -e AURORA_DOCKER_TESTS=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c '
    set -eu
    mkdir /work
    cd /src
    tar -cf - --exclude="bin" --exclude="bin?Debug" --exclude=obj --exclude=.git . | tar -xf - -C /work
    cd /work
    if [ "$#" -eq 0 ]; then set -- --filter "Category=DockerIntegration"; fi
    exec dotnet test "$@"
  ' sh "$@"
