# Referencia contra Prawn (Ruby)

Genera un PDF con el checkout local de Prawn y muestra el ancho de `Hola mundo` a 12 pt.
Es el oráculo para las pruebas de conformidad de F2/F3.

## Uso

```bash
cd tools/PrawnSharp.Reference
ruby generate_reference.rb prawn_reference.pdf
```

Requiere Ruby 3.x y el checkout de Prawn. Por omisión usa
`C:\projects\ruby\pdf\prawn_sources`; se puede cambiar con `PRAWN_SOURCES`:

```bash
PRAWN_SOURCES=C:/ruta/a/prawn_sources ruby generate_reference.rb salida.pdf
```

El script **no** se compila ni se distribuye con la librería; es una herramienta de
desarrollo.
