#!/usr/bin/env bash
set -u
cd "$(dirname "$0")"
dotnet run -c Debug --project src/NanoFTP/NanoFTP.csproj
status=$?
if [[ $status -ne 0 ]]; then
  read -r -p "Echec du lancement. Appuyez sur Entree pour fermer..."
fi
exit $status
