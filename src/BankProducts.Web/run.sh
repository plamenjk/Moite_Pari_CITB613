#!/usr/bin/env bash
set -e
dotnet restore
dotnet build -c Debug
dotnet run --urls=http://localhost:5226

