# Instalar BorderLess 0.1.11

El archivo `BorderLess-0.1.11-setup-win-x64.exe` instala BorderLess para el usuario actual de Windows 11 x64. Incluye el runtime .NET necesario, acceso en Inicio y desinstalación desde Configuración de Windows. Crear un acceso en el escritorio es opcional. El instalador está disponible en español e inglés.

## Pasar desde la versión portable

1. En cada PC, sal de BorderLess desde su icono junto al reloj → **Salir**, o desde Preferencias → **Salir**. La X mantiene el programa activo en la bandeja.
2. Ejecuta el instalador y conserva su carpeta propuesta. No hace falta ejecutarlo como administrador para instalarlo.
3. En **Configuración existente**, elige **Importar configuración portable de este PC** y selecciona la carpeta **data** de la copia portable que utilizabas en ese mismo PC y con ese usuario Windows. Esa carpeta contiene `settings.json`.
4. Abre BorderLess desde Inicio y comprueba la versión **0.1.11**. Se conservan identidad, vínculos, alias, idioma y preferencias. Si ya existe un perfil instalado, el instalador lo conserva y omite la importación.
5. Repite el proceso en el otro PC con **su propia carpeta data**. No copies la carpeta data de un PC al otro: las claves están protegidas para su usuario Windows.

La importación copia la configuración y valida sus claves antes de escribirla. No modifica el origen. Los archivos recibidos se conservan en la carpeta portable original; esa ubicación queda configurada en la app. **No borres esa carpeta si todavía contiene tus archivos.** Puedes escoger otro destino para recepciones nuevas en **Archivos → Carpeta de recepción → Cambiar carpeta**. Mover archivos anteriores, si lo deseas, es una operación manual.

Si quieres empezar sin vínculos anteriores, elige **Instalación nueva** y vincula los PCs con un PIN. Si falla la importación, revisa `%LOCALAPPDATA%\BorderLess\data\import-error.log`; los datos originales no se reemplazan ni borran.

## Carpetas y actualizaciones

- Programa: `%LOCALAPPDATA%\Programs\BorderLess`, salvo que elijas otra ubicación en el asistente.
- Configuración instalada: `%LOCALAPPDATA%\BorderLess\data`.
- Recepción predeterminada de una instalación nueva: `%LOCALAPPDATA%\BorderLess\data\received`.
- Portable: continúa usando `data` junto al ejecutable; ambos formatos están disponibles.

Para actualizar una instalación, sal del programa y ejecuta el instalador nuevo con el mismo usuario y ubicación. Conserva la configuración existente. Los ZIP pequeños de actualización están destinados a la versión portable.

Puedes desinstalar desde **Configuración → Aplicaciones → Aplicaciones instaladas → BorderLess**. Se eliminan el programa y sus accesos. Se conservan la configuración y los archivos recibidos, incluso si estaban en una carpeta elegida por ti. Una reinstalación con el mismo usuario recupera el perfil conservado.

## Recepción en una carpeta elegida

En **Archivos → Carpeta de recepción**, pulsa **Cambiar carpeta**. La app comprueba que puede escribir allí y guarda la selección individualmente en cada PC. **Abrir carpeta** abre el destino vigente y **Usar carpeta predeterminada** restablece el destino del perfil actual. Solo cambian las recepciones aceptadas después; una transferencia en curso mantiene su carpeta. Los archivos previos no se mueven ni borran.

## Permisos

Instalar no activa inicio automático, servicios ni cambios de firewall. Para controlar aplicaciones elevadas, el receptor necesita ejecutarse con permisos adecuados, igual que la versión portable. No se controla el escritorio seguro de UAC ni la pantalla de bloqueo.

El instalador de BorderLess no tiene firma de código: no disponemos de un certificado. Puede estar sujeto a SmartScreen o a las políticas de la empresa. El formato instalable no elimina una restricción de firma; no se modifican políticas de seguridad ni se instalan certificados.
