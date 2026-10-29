# Alcance de validación 0.1.10

Pruebas locales en Windows 11 x64, SDK .NET 10.0.403 y runtime 10.0.14:

- 124 comprobaciones de protocolo, PIN, TLS, archivos e integridad.
- 123 comprobaciones Windows con escritorio simulado, persistencia e importación.
- 33 comprobaciones WPF de interfaz, bandeja, idioma, tema y carpeta.
- 29 comprobaciones del portapapeles con backend simulado.
- 15 comprobaciones de formas de cursor y recursos gráficos.
- 13 comprobaciones de instalación/reinstalación/desinstalación aislada y conservación de datos.

No se inyectó entrada en programas del usuario durante las pruebas automatizadas. Las capturas del README utilizan datos sintéticos y no representan una transferencia real ni resultados de rendimiento.

La navegación completa del asistente no se automatizó. Se verificaron su compilación, instalación silenciosa, reinstalación, desinstalación y comando de migración. El comportamiento en dos PCs físicos, redes externas y políticas empresariales debe comprobarse en cada entorno. No hay una comparación controlada que demuestre menor latencia que PowerToys.
