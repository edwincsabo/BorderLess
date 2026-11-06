# BorderLess 0.1.14

Mouse, teclado, portapapeles y archivos entre PCs Windows 11 x64, sin transmisión de pantalla.

Nuevo en 0.1.14: actualiza la dirección de un equipo vinculado tras verificar su identidad, localiza el archivo recibido desde su transferencia y activa el cruce rápido de borde en Preferencias.

Desde 0.1.13: BorderLess reutiliza la geometría de los monitores y prepara para envío los movimientos que se conservan en la cola. Los clics, teclas y rueda mantienen su orden.

Disponible desde 0.1.12: **Actividad → Medir durante 30 segundos**. Selecciona el equipo conectado y usa el mouse con normalidad. Verás el RTT mediano y el máximo, el número de muestras y la mayor profundidad observada de la cola de salida local. Usa las respuestas existentes de la conexión TLS y permite cancelar la prueba. Los resultados completados quedan en el historial de Actividad para su exportación.

- Instalador por usuario con runtime incluido y migración opcional del perfil portable del mismo PC.
- Carpeta de recepción configurable por equipo.
- PIN de 8 dígitos, conexiones TLS, reconexión y canales separados para entrada, portapapeles y archivos.
- Español e inglés, temas claro/oscuro, bandeja, alias y mapa de equipos/monitores.

Descarga el archivo **setup-win-x64.exe** para instalar. El ZIP **win-x64** es el portable completo; **actualizacion-win-x64** requiere un portable existente. Los archivos SHA256 permiten comprobar las descargas.

Para conservar vínculos al instalar, sal de la copia portable usando **Salir** y elige **Importar configuración portable de este PC** en el asistente. Usa la carpeta data de ese mismo PC/usuario. No copies perfiles entre equipos.

El instalador no tiene firma digital. La compatibilidad con políticas empresariales depende del equipo. No controla UAC ni la pantalla bloqueada. La transferencia de archivos es opcional, requiere aceptación y está limitada a aproximadamente 1 MiB/s y 1 GiB por archivo.

Licencia: PolyForm Noncommercial 1.0.0. Permite uso, modificación y redistribución para fines no comerciales, sujeto a sus términos. Las dependencias conservan sus licencias.
