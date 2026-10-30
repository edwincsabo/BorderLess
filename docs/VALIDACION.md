# Alcance de validación 0.1.11

Pruebas locales en Windows 11 x64, SDK .NET 10.0.403 y runtime 10.0.14:

- 124 comprobaciones de protocolo, PIN, TLS, archivos e integridad.
- 136 comprobaciones Windows con escritorio simulado, persistencia, importación y solicitudes de energía.
- 42 comprobaciones WPF de interfaz, bandeja, idioma, tema, carpeta y mantener activo.
- 29 comprobaciones del portapapeles con backend simulado.
- 15 comprobaciones de formas de cursor y recursos gráficos.
- 13 comprobaciones de instalación/reinstalación/desinstalación aislada y conservación de datos.

359 comprobaciones en total. La opción de mantener activo se probó con una conexión TLS de loopback y un backend de energía simulado: activación, preferencia persistente, ventana oculta, pausa, bloqueo, desconexión y cierre. También se ejercitó una adquisición y liberación nativa breve de las solicitudes de pantalla y sistema de Windows. No se bloqueó ni suspendió físicamente el equipo durante las pruebas.

La función se actualiza por eventos, sin simular entrada, temporizadores nuevos ni tráfico de red adicional. Windows puede limitar las solicitudes; no se garantiza evitar un bloqueo impuesto por políticas. Falta comprobar la inactividad prolongada en los dos PCs físicos. La pérdida de red se reconoce por el cierre del socket o el heartbeat existente.

No se inyectó entrada en programas del usuario durante las pruebas automatizadas. Las capturas del README utilizan datos sintéticos y no representan una transferencia real ni resultados de rendimiento.

La navegación completa del asistente no se automatizó. Se verificaron su compilación, instalación silenciosa, reinstalación, desinstalación y comando de migración. El comportamiento en dos PCs físicos, redes externas y políticas empresariales debe comprobarse en cada entorno. No hay una comparación controlada que demuestre menor latencia que PowerToys.
