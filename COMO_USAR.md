# BorderLess 0.1.11 para Windows 11 x64

Puedes usar el instalador `BorderLess-0.1.11-setup-win-x64.exe` siguiendo **INSTALAR.md**, o conservar la versión portable. Si ya utilizas un portable 0.1.x, consulta `ACTUALIZAR.md` para conservar vínculos y preferencias. La 0.1.11 añade una carpeta de recepción configurable y conserva las funciones y optimizaciones anteriores.

## Primera conexión por cable

1. Extrae el ZIP completo en cada PC, en una carpeta en la que tu usuario pueda escribir. Abre `BorderLess.exe`. Conserva al lado la carpeta `runtime`: el paquete incluye .NET y no necesita instalarlo.
2. Desactiva **Mouse sin bordes** de PowerToys antes de controlar para evitar que ambos programas capturen la entrada. BorderLess no cambia PowerToys por ti.
3. En el PC receptor: **Añadir equipo → Recibir en este PC → Generar código de 8 dígitos**. El código vence a los 2 minutos, admite hasta 5 intentos y se usa una sola vez. Mantén el panel abierto.
4. En el otro PC: **Añadir equipo → Conectar a otro PC → Buscar equipos**. Selecciona el receptor, escribe sus 8 dígitos y pulsa **Vincular con PIN**. Si no aparece, introduce manualmente su IP o nombre y puerto (normalmente 45161), que se muestran en el receptor. El PIN correcto completa el vínculo automáticamente: no hace falta volver al receptor para confirmar.
5. Cierra el panel. Pulsa **Controlar** o el equipo conectado en el mapa; también puedes asignarle un borde y mantener el mouse en ese borde unos 160 ms. El equipo receptor debe tener permitido el control en sus Preferencias.
6. Para volver: cruza el borde por el que entraste, usa **Ctrl + Alt + F12**, o pulsa **Volver aquí**. Si estás en el receptor, mover su mouse físico o usar su teclado recupera su control local.

Si Windows solicita permitir tráfico de red para el ejecutable, revisa y concede tú el permiso adecuado a tu red. El puerto de recepción predeterminado es **TCP 45161**. No hay cambios automáticos de firewall, privilegios o router. Una conexión que el firewall bloquee no podrá completarse.

## Código corto y búsqueda local

No necesitas llevar el código a una página web. El PIN se verifica mediante J-PAKE; después de verificar el código correcto, se guardan claves largas y el vínculo reconecta sin volver a escribirlo. Generar el código autoriza a quien lo conozca durante su vigencia. El receptor puede revocarlo antes de usarlo. Generar otro PIN invalida el anterior. Cerrar u ocultar la ventana, bloquear Windows o suspender el equipo revoca el código corto y cancela la vinculación pendiente, manteniendo los vínculos que ya existían.

Buscar equipos realiza dos consultas UDP y termina a los dos segundos. El anuncio solo existe mientras está abierto el panel del receptor con un código activo; no incluye el PIN ni las claves. La detección usa UDP 45163 y depende de la red y sus reglas. La dirección manual usa el mismo receptor TCP de la aplicación y permite vincular aunque la búsqueda local no funcione. El nombre detectado es una pista; la conexión se verifica al introducir el PIN.

En **Añadir equipo → Avanzado** se mantiene el código largo de 10 minutos para relay y compatibilidad con versiones anteriores. No lo publiques: contiene una clave temporal. El PIN corto se usa en conexión directa LAN o por una dirección de VPN accesible; para relay utiliza Avanzado.

## Idioma y puntero auxiliar

En **Preferencias → Apariencia y ventana → Idioma de la aplicación**, selecciona **Español** o **English**. Cambia al instante y se guarda por PC; no renombra equipos, alias o archivos. En inglés la ruta es Preferences → Appearance and window → Application language. Los controles nativos de Windows y errores del sistema pueden seguir su idioma del sistema.

El puntero auxiliar se utiliza solo al recibir control y cuando Windows oculta su cursor. Ahora toma la imagen y el punto de clic del cursor real: flecha, mano, texto, redimensionamiento horizontal/vertical/diagonal, movimiento, no disponible y espera. Los cursores de espera estándar pueden animarse. Las formas dependen del cursor que publique la aplicación de destino: un botón que normalmente usa flecha seguirá mostrándola. Las aplicaciones que dibujan su cursor por separado pueden no publicar esa forma.

No se cambia el tema de cursores de Windows, no se activa Mouse Keys ni se instalan controladores. La opción Mostrar puntero auxiliar cuando Windows lo oculta conserva su comportamiento. Falta comprobar el cambio de forma con el receptor sin mouse físico y los periféricos en el PC de origen.

## Pausar, volver al PC y salir

- **Volver aquí** devuelve mouse y teclado a este PC; portapapeles y archivos siguen disponibles.
- **Pausar uso compartido** suspende control remoto, portapapeles y archivos en este PC sin cerrar la app ni borrar vínculos. Cancela transferencias activas. El botón cambia a **Reanudar uso compartido** y un aviso indica si la pausa es manual o por bloqueo.
- **Reanudar uso compartido** permite compartir otra vez. Para controlar otro equipo vuelve a cruzar su borde o pulsa Controlar. Los archivos cancelados deben enviarse de nuevo.
- **Salir** termina la aplicación. La X y minimizar mantienen BorderLess activo en la bandeja.

En **Preferencias → Pausa y bloqueo de Windows**, la opción **Pausar el uso compartido al bloquear Windows** viene activada. Puedes desactivarla individualmente en cada PC. Si está activada, bloquear suspende los servicios y desbloquear los reactiva; una pausa manual no se elimina al desbloquear. Si está desactivada, la app no pausa automáticamente portapapeles ni archivos al bloquear. El acceso efectivo al portapapeles sigue sujeto a Windows.

Esta opción no permite controlar ni capturar periféricos en la pantalla de bloqueo. Al bloquear siempre se libera la sesión de control remoto y se revoca el PIN pendiente. Después de desbloquear puedes volver a controlar; no se repiten teclas retenidas ni se recupera automáticamente el control anterior. La suspensión del equipo, desconexión de red y cierre de sesión siguen siendo situaciones diferentes.

## Mantener el equipo activo mientras está conectado

En **Preferencias → Mantener el equipo activo**, activa **Mantener activo mientras haya otro equipo conectado** en cada PC donde lo necesites. Viene desactivado y se guarda individualmente. Un equipo vinculado pero desconectado no activa la función.

Mientras exista al menos una conexión de entrada autenticada y el uso compartido no esté pausado, BorderLess solicita a Windows mantener pantalla y sistema activos. No hace falta estar controlando el otro PC; también funciona con la ventana en la bandeja. El estado se muestra debajo de la opción.

La solicitud se libera al pausar, desmarcar la opción, desconectar el último equipo, bloquear Windows, suspender o salir. Al reanudar o reconectar vuelve a solicitarse si se cumplen las condiciones. No mueve el puntero, no escribe teclas, no añade tráfico de red y no modifica configuraciones de energía ni políticas del equipo.

Windows decide si respeta la solicitud. No desbloquea una sesión ni impide Win + L, suspensión manual, cierre de tapa o políticas empresariales de bloqueo. Mantener la pantalla encendida puede aumentar el consumo de batería. Una conexión cortada se considera perdida cuando la red o el heartbeat detectan el cierre, no necesariamente en el instante en que se retira un cable.

## Portapapeles

Texto Unicode hasta 1 MB e imágenes PNG hasta 8 MB/16 MP. PNG, Base64, hash y preparación de mensajes se procesan fuera de la interfaz con un trabajo activo y uno pendiente como máximo; las copias recientes sustituyen trabajo obsoleto. Windows sigue requiriendo que la lectura y escritura del portapapeles ocurran en su hilo de interfaz. Se comparte con los equipos vinculados que tengan el canal de portapapeles conectado; también puedes usar **Compartir ahora**. Las transferencias van por una conexión TLS separada y limitada en ritmo de envío. Los códigos BL1 y el PIN local mostrado durante su vigencia se excluyen del envío automático.

**Pausar** detiene entrada, portapapeles y archivos, cancelando las transferencias activas. No se sincroniza el historial de Windows. El envío de archivos se hace desde Archivos; copiar/pegar o arrastrar archivos y carpetas no está incluido.

## Varios equipos y bordes

Puedes vincular varios equipos (hasta 12 credenciales), controlar un destino a la vez y asignar un vecino a izquierda, derecha, superior e inferior. En **Organizar equipos**, arrastra a un borde o usa las listas de equipo/ubicación. Guardar aplica; Cancelar descarta. Un lugar ocupado intercambia vecinos. Para otros equipos sin borde, usa **Controlar**. La organización es relativa a cada PC; si cambias de origen, configura el borde opuesto allí.

El mapa representa la geometría que Windows informa de los monitores, incluyendo proporciones y principal. No cambia los ajustes de pantalla ni mueve monitores individuales. Se cruza en los límites exteriores del escritorio virtual, sin tránsito automático por una cadena arbitraria. No se cruza al mantener un botón del mouse pulsado; el arrastre dentro del destino sí se transmite.

**Editar alias** permite cambiar la etiqueta visible sin renombrar Windows ni cambiar claves. El alias propio se informa al par; un alias que asignas localmente a otro equipo tiene prioridad. El menú contextual de un equipo en el mapa también permite editarlo. Resolución y escala de pantalla están en sus tooltips; si un extremo es antiguo o está offline, se indica la falta de datos o la última disposición conocida.

## Archivos

Viene desactivado por defecto. Abre **Archivos → Habilitar envío y recepción de archivos** en ambos PCs. Selecciona el destino y pulsa **Elegir archivo y enviar**; el receptor debe pulsar **Aceptar archivo** en su pantalla Archivos. Si está oculto, se muestra un aviso de solicitud pendiente en la bandeja.

Un envío y una recepción simultáneos como máximo por PC. Cada archivo tiene un máximo de 1 GiB y el canal usa un ritmo conservador aproximado de 1 MiB/s. Progreso y cancelación están en Archivos. La solicitud vence tras dos minutos sin aceptación. Deshabilitar la función, pausar, desvincular o perder conexión cancela la transferencia; volver a habilitar no reenvía archivos automáticamente.

Se usa un tercer canal TLS autenticado independiente de mouse/teclado y portapapeles. Lectura, escritura y hashing ocurren fuera del hilo de UI y del hook. Cola acotada a ocho bloques de 64 KiB y progreso como máximo cuatro veces por segundo, sin repintar la ventana oculta. Los recursos físicos siguen siendo compartidos: no hay garantía de latencia adicional cero.

En **Carpeta de recepción → Cambiar carpeta** eliges dónde guardar los archivos. Se comprueba escritura y se guarda por PC. **Abrir carpeta** muestra el destino y **Usar carpeta predeterminada** restaura la carpeta `received` del perfil actual: `data/received` junto al portable o `%LOCALAPPDATA%\BorderLess\data\received` en una instalación nueva. Si importaste un perfil portable, se mantiene su destino anterior hasta cambiarlo. Las recepciones aceptadas después usan la nueva ubicación; las que están en curso conservan su carpeta. Los archivos anteriores no se mueven ni borran.

Cada archivo queda en una subcarpeta única. Se validan nombre, tamaño, orden y SHA-256 antes de renombrar el temporal como archivo completo. Nunca se sobrescribe ni ejecuta el archivo. Una cancelación normal elimina el temporal; una terminación abrupta o corte de energía puede dejar `.partial`. No hay envío de carpetas, arrastre, reanudación ni transferencia automática al copiar un archivo al portapapeles.

## Internet

**Directo/VPN:** el código usa la dirección local del receptor; Dirección alternativa permite sustituirla por una IP o nombre accesible desde el otro equipo. La VPN y sus reglas de acceso se configuran fuera de BorderLess.

**Relay propio:** el paquete incluye `relay/BorderLess.Relay.exe`. Ejecútalo en un servidor Windows x64 accesible desde ambos PCs; el puerto predeterminado es TCP 45162, configurable como argumento (`BorderLess.Relay.exe 45162`). En el receptor, abre **Preferencias**, escribe el servidor/puerto y pulsa **Activar relay**. Después genera un código con **Usar el relay de internet configurado**. El emisor pega ese código en Añadir equipo → Avanzado → Conectar con código largo.

El relay enruta el TLS entre los PCs; no descifra teclas, portapapeles o archivos. Puede conocer dirección IP, identificador de sala, tiempos y volúmenes. La cabecera de encaminamiento se envía antes del TLS interior; su identificador es una capacidad temporal de ruta, no la credencial de control. Un relay o un tercero que interfiera en la ruta puede cortar o bloquear el servicio, pero no supera la verificación de certificado y clave de los PCs. Para exposición pública con requisitos operativos mayores, hacen falta gestión de servicio, límites por origen, supervisión y endurecimiento adicionales. No se ha desplegado ningún servidor público.

En esta versión el receptor debe volver a activar el relay después de reiniciar la aplicación y generar un código nuevo si cambia la sala. Internet tiene latencia de trayecto: no se garantiza la misma respuesta que en Ethernet local.

## Seguridad y recuperación

- TLS 1.2/1.3, certificados propios con verificación por huella, prueba HMAC de posesión del código y clave derivada por par. Los certificados propios no se instalan como autoridades de confianza del sistema.
- Certificado y claves se guardan protegidos con DPAPI del usuario Windows en `data/settings.json` junto al portable o `%LOCALAPPDATA%\BorderLess\data\settings.json` en el formato instalado. Copia el ZIP o instalador limpio al segundo equipo; no copies la carpeta `data` de uno al otro. Cada PC necesita su propia identidad.
- **Desvincular** revoca la credencial local y cierra los canales. Si desvinculas en un solo PC, el otro puede seguir intentando conectar, pero no se autenticará.
- Entrada en hilo dedicado; espera de red fuera del hook; cola acotada, coalescencia de movimiento acumulado, épocas de sesión y liberación de entrada sintética ante cierre/fallo.
- Heartbeat y reconexión automática en el PC que inició el vínculo. Si cambia la IP o identidad del receptor, vuelve a vincular con un código nuevo.
- La X y minimizar mantienen la app activa en la bandeja junto al reloj. Doble clic en el icono abre la ventana. El menú del icono permite abrir, pausar, recuperar el control y Salir; Preferencias también incluye Salir. El tema Claro/Oscuro se guarda en Preferencias.
- Por defecto se pausa al bloquear Windows y se reanuda al desbloquear, conservando cualquier pausa manual. La preferencia Pausar el uso compartido al bloquear Windows permite desactivar la pausa automática de los servicios de datos. No controla la pantalla de bloqueo ni el escritorio seguro de UAC. Las ventanas elevadas requieren integridad equivalente; si una aplicación se ejecuta como administrador, la inyección de un proceso normal puede ser bloqueada.
- Actividad muestra datos de funcionamiento; no guarda teclas o contenido. El diagnóstico exportado puede incluir nombres/direcciones que hayas usado y debes revisarlo antes de compartirlo.

Si Windows rechaza el control, la explicación aparece en los dos PCs. La consulta de la ventana de destino y su nivel de permisos se hace solo al fallar. Se muestra una causa general si no puede verificarse; SendInput no identifica por sí mismo un bloqueo UIPI. No se cambia la pausa ni se solicitan privilegios automáticamente.

## Estado de validación

Las pruebas locales comprueban el protocolo, vinculación TLS mutua, rechazos de autenticación, tres canales, archivos, reconexión y relay; la integración de entrada se comprueba con un adaptador de escritorio simulado para no inyectar en aplicaciones del usuario. Se revisa la interfaz real en Windows.

Queda pendiente la prueba física con los dos PCs y el Redragon M607, incluyendo movimiento de cursor, aceleración, permisos, pérdida de cable, escalados y portapapeles real entre máquinas. No se ha demostrado superioridad de latencia ni estabilidad frente a PowerToys. La versión se entrega para esa validación, no como producto ya probado en producción.



