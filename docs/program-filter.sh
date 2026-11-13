#!/bin/sh
# Filtro de Doxygen para los Program.cs. Las sentencias de nivel superior
# generan una clase Program en el namespace global, una por proyecto, y Doxygen
# las junta en una sola página con las descripciones mezcladas. El filtro mete
# la declaración partial class Program, con su documentación, en el namespace
# del proyecto -Ucu.Andis.<carpeta>-, solo para Doxygen: el código que compila
# no cambia.
project=$(basename "$(dirname "$1")")
perl -0pe "s/((?:^\/\/\/.*\n)+)partial class Program \{ \}/namespace Ucu.Andis.$project\n{\n\$1partial class Program { }\n}/m" "$1"
