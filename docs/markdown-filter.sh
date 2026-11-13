#!/bin/sh
# Filtro de Doxygen para los .md. GitHub dibuja las fórmulas escritas como $…$ y
# en bloques ```math, pero Doxygen no las reconoce: además, convierte \% en %, y
# en TeX % empieza un comentario. El filtro las pasa a la sintaxis de Doxygen,
# \f$…\f$ y \f[…\f], cuyo contenido llega a MathJax sin cambios. Los bloques de
# código y el código en línea no se tocan.
#
# Además, Doxygen enlaza solo cada mención de ARCHITECTURE.md, PATTERNS.md o
# README.md a la página del archivo, que está vacía, y no a la del documento.
# El filtro les antepone %, que Doxygen no muestra, para que no las enlace;
# los enlaces explícitos a esos documentos siguen funcionando.
#
# README.md no está en el sitio: los enlaces a README.md, y a sus secciones,
# pasan al README en GitHub.
#
# En los avisos de GitHub, como > [!TIP], la línea > vacía que sigue al tipo
# GitHub la ignora, pero Doxygen la muestra como un renglón vacío al principio
# del recuadro. El filtro la quita.
perl -0777 -pe '
  my @parts = split /(^```[^\n]*\n.*?^```[^\n]*$)/ms;
  for my $i (0 .. $#parts) {
    if ($i % 2) {
      $parts[$i] =~ s/^```math[^\n]*\n(.*?)^```[^\n]*$/\\f[\n$1\\f]/ms;
    } else {
      $parts[$i] =~ s/^(> \[!\w+\]\n)>[ \t]*\n/$1/mg;
      my @spans = split /(`[^`\n]*`)/, $parts[$i];
      for my $j (0 .. $#spans) {
        next if $j % 2;
        $spans[$j] =~ s/\$([^\s\$](?:[^\$\n]*[^\s\$])?)\$/\\f\$$1\\f\$/g;
        $spans[$j] =~ s/(?<![\w\/%(.\[])((?:ARCHITECTURE|PATTERNS|README)\.md)(?![\w)#])/%$1/g;
        $spans[$j] =~ s{\]\(README\.md(#[^)\s]+)?\)}{](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/README.md$1)}g;
      }
      $parts[$i] = join "", @spans;
    }
  }
  $_ = join "", @parts;
' "$1"
