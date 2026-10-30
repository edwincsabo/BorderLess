# BorderLess 0.1.11

Mouse, teclado, portapapeles y archivos entre PCs Windows 11 x64, sin transmisión de pantalla.

Nuevo en 0.1.11: **Preferencias → Mantener el equipo activo**. Opción desactivada por defecto, guardada por PC, que solicita a Windows evitar suspensión y apagado de pantalla mientras exista una conexión autenticada. Se libera al pausar, desconectar el último equipo, bloquear, suspender o salir. No simula entrada ni cambia políticas de seguridad; el bloqueo corporativo puede seguir aplicándose.

- Instalador por usuario con runtime incluido y migración opcional del perfil portable del mismo PC.
- Carpeta de recepción configurable por equipo.
- PIN de 8 dígitos, conexiones TLS, reconexión y canales separados para entrada, portapapeles y archivos.
- Español e inglés, temas claro/oscuro, bandeja, alias y mapa de equipos/monitores.

Descarga el archivo **setup-win-x64.exe** para instalar. El ZIP **win-x64** es el portable completo; **actualizacion-win-x64** requiere un portable existente. Los archivos SHA256 permiten comprobar las descargas.

Para conservar vínculos al instalar, sal de la copia portable usando **Salir** y elige **Importar configuración portable de este PC** en el asistente. Usa la carpeta data de ese mismo PC/usuario. No copies perfiles entre equipos.

El instalador no tiene firma digital. La compatibilidad con políticas empresariales depende del equipo. No controla UAC ni la pantalla bloqueada. La transferencia de archivos es opcional, requiere aceptación y está limitada a aproximadamente 1 MiB/s y 1 GiB por archivo.

Licencia: PolyForm Noncommercial 1.0.0. Permite uso, modificación y redistribución para fines no comerciales, sujeto a sus términos. Las dependencias conservan sus licencias.
