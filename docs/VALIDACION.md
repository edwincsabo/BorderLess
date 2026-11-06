# Alcance de validación

## 0.1.14

Conexiones TLS locales de prueba comprobaron el rechazo de un receptor con otro certificado y de un receptor con clave de vínculo incorrecta. Un fallo simulado al guardar conservó la dirección original. El cambio válido restableció los canales de entrada y portapapeles, y al recargar el perfil se conservaron el alias, el borde y la clave protegida. La prueba WPF abrió el editor desde la ficha del PC desconectado, corrigió una dirección inválida y verificó el nuevo puerto sin PIN.

En una recepción completada se verificó la ruta exacta entregada al lanzador del Explorador con un archivo de prueba cuyo nombre incluía espacios y una coma. Al retirar el archivo se mostró el aviso de ubicación y no se invocó el lanzador. El ensayo sustituyó el lanzador para evitar abrir ventanas en el escritorio del usuario.

Las comprobaciones del borde verificaron los umbrales de 160 ms y 80 ms, así como el reinicio al salir del borde o cambiar de lado. La preferencia persistió al recargar el perfil. Los umbrales corresponden a la espera intencional local mientras llegan eventos de mouse. Falta medir en dos PCs físicos el intervalo completo hasta que el otro equipo concede control.

## 0.1.13

Se midieron cinco rondas después del calentamiento, en el mismo PC, con 20.000 movimientos por ronda. Para recepción se prepararon coordenadas dentro de un monitor real sin inyectar el cursor. La mediana de los tiempos medios de preparación por movimiento pasó de 79,8 ns a 14,8 ns. El percentil 99 de los tiempos individuales fue de 0,1 µs antes y después (medianas de las rondas).

Para envío se drenó la cola cada 100 movimientos. Los 200 estados acumulados coincidieron en ambos casos. Las asignaciones en el hilo del ensayo pasaron de 1.600.032 a 16.032 bytes por ronda. El ahorro depende de cuántos movimientos se sustituyan antes de que el emisor los retire.

Pasaron 126 comprobaciones de protocolo, 140 de integración Windows y 42 de interfaz. Se verificaron el orden y los bytes de los movimientos antes de clics, teclas y rueda, la marca de tiempo original, los orígenes negativos, las esquinas, los puntos entre pantallas y la vuelta al cálculo de Windows al invalidar la geometría. El ensayo local mide trabajo de CPU y memoria. Queda pendiente la prueba física entre dos equipos, incluido cambiar los monitores durante el uso.

## 0.1.12

Desde Actividad se completó una medición de 30 segundos con dos instancias en el mismo PC conectadas por TLS. Se recibieron 30 respuestas. También se comprobó que el botón permite cancelar y volver a medir, y que una desconexión interrumpe la prueba. Pasaron las 45 comprobaciones del recorrido de interfaz, las 136 de integración Windows y las 13 del instalador aislado. Los valores obtenidos en loopback describen únicamente el entorno de prueba. La comparación entre dos PCs físicos queda pendiente.

La compilación Release terminó con cero advertencias y cero errores. La directiva de Control de aplicaciones del equipo bloqueó el inicio del ejecutable independiente de las pruebas de protocolo, por lo que esa suite quedó sin ejecutar en esta validación local.

## 0.1.11

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
