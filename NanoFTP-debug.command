#!/bin/bash
cd "$(dirname "$0")"
dotnet run -c Debug --project src/NanoFTP/NanoFTP.csproj
status=$?
echo
read -n 1 -s -r -p "Appuyez sur une touche pour fermer..."
exit $status
