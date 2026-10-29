# Actualizar BorderLess portable 0.1.x a 0.1.10

Esta versión permite elegir la carpeta de archivos recibidos y añade un instalador. Conserva vínculos y preferencias. Para pasar al formato instalable conservando tu perfil, sigue **INSTALAR.md**. Los pasos siguientes corresponden a la versión portable.

1. Extrae `BorderLess-0.1.10-actualizacion-win-x64.zip` en una carpeta temporal en cada PC.
2. Sal de BorderLess desde el menú de su icono en la bandeja → **Salir**, o desde Preferencias. La X mantiene el proceso activo y no basta para reemplazarlo.
3. Copia **todos los archivos que están dentro de Copiar_en_BorderLess-0.1.10** a la carpeta habitual del programa, junto a runtime, aceptando reemplazar archivos. Incluye BouncyCastle.Cryptography.dll y su licencia: no copies solamente el EXE.
4. Conserva **data** y **runtime**. No copies data de un PC al otro. Los vínculos, alias, preferencias y archivos recibidos permanecen allí.
5. Abre BorderLess.exe en ambos PCs y comprueba **0.1.10** abajo a la izquierda. La reconexión existente se mantiene.

Para un portable nuevo utiliza `BorderLess-0.1.10-win-x64.zip`, que incluye runtime. La actualización pequeña requiere una instalación portable existente.

En **Archivos → Carpeta de recepción → Cambiar carpeta** puedes elegir un destino guardado por PC. Las recepciones nuevas usarán ese destino; los archivos anteriores no se mueven y una transferencia en curso conserva su ubicación. **Usar carpeta predeterminada** restablece `data/received` del perfil actual.

## Idioma y cursor

En Preferencias → Apariencia y ventana puedes elegir Español o English. Se guarda por PC. Para probar el cursor, conecta los periféricos a SWO y controla la torre: comprueba enlaces, campos de texto y bordes de ventana. La forma se toma de Windows; cada aplicación decide qué cursor publica.

## Optimizaciones conservadas

- Las imágenes mantienen sus límites y calidad; su preparación se hace en segundo plano. Se conserva la copia más reciente y se descartan trabajos obsoletos al copiar otra cosa, pausar o cerrar.
- La vista de archivos actualiza tamaño/porcentaje y conserva sus filas y foco. No cambia la velocidad limitada de transferencia.
- Si Windows rechaza entrada, el controlador y el receptor muestran el motivo. Si se comprueba una ventana con integridad superior, se indica en qué PC ejecutar BorderLess con permisos adecuados. La app no se eleva automáticamente.

Prueba copiar capturas grandes, alternar imagen/texto rápidamente y usar otras pantallas mientras se procesa. Comprueba el progreso de archivos y la navegación por teclado. El bloqueo simulado de permisos se probó con adaptadores; no se modificaron programas del usuario.

## Elegir qué sucede al bloquear Windows

Abre **Preferencias → Pausa y bloqueo de Windows → Pausar el uso compartido al bloquear Windows**. La opción viene activada y se guarda individualmente en cada PC.

- Activada: el uso compartido se pausa al bloquear y se reanuda al desbloquear. Una pausa que activaste manualmente se conserva hasta pulsar Reanudar.
- Desactivada: el bloqueo no pausa automáticamente los servicios de portapapeles/archivos de BorderLess. Su funcionamiento efectivo sigue sujeto a Windows y la red.
- En ambos casos se libera el control remoto y se revoca el PIN pendiente. No permite capturar ni controlar periféricos desde la pantalla de bloqueo. No modifica la seguridad de Windows.

El botón ahora indica **Pausar uso compartido** o **Reanudar uso compartido**. Si está pausado aparece un aviso con el motivo. Pausar cancela archivos en curso; no los reanuda desde el último bloque. **Volver aquí** devuelve el control local y conserva los datos activos. **Salir** termina la app; la X la mantiene en la bandeja.

## Vincular otro PC sin copiar códigos largos

1. En el receptor: **Añadir equipo → Recibir en este PC → Generar código de 8 dígitos**. Mantén abierto el panel.
2. En el otro equipo: **Añadir equipo → Conectar a otro PC → Buscar equipos**. Selecciona el receptor. Si no aparece, introduce la dirección y puerto que se muestran allí.
3. Escribe los 8 dígitos y pulsa **Vincular con PIN**.
4. Con PIN correcto el vínculo se completa sin otra confirmación. Comparte el código solo con tu otro PC: conocerlo autoriza el vínculo durante su vigencia.

El PIN vence a los 2 minutos, admite 5 intentos y es de un solo uso. Cerrar u ocultar la ventana, revocar, bloquear Windows o suspender cancela el código y la solicitud pendiente. La búsqueda usa UDP 45163 solo al vincular. La entrada manual utiliza el puerto TCP habitual (45161 por defecto), sin depender de la búsqueda.

**Avanzado** conserva el código largo para relay y compatibilidad con versiones antiguas. No se necesita un servidor externo para la vinculación corta por LAN. El PIN nunca se envía en los anuncios de búsqueda ni como contraseña en la conexión.

## Comprobaciones después de actualizar

- Vínculos previos reconectan y conservan alias/bordes.
- Cambia la preferencia de bloqueo, reinicia y verifica que se conserva. Prueba bloqueo/desbloqueo con ambas opciones y con pausa manual previa.
- Mouse, teclado, Shift/Bloq Mayús, portapapeles y archivos se comportan como antes.
- Prueba buscar, conectar con PIN y cancelar; un PIN correcto vincula sin segunda confirmación.
- Verifica bandeja, modo oscuro y Ctrl + Alt + F12.

Se observó un bloqueo de Control de aplicaciones (0x800711C7) sobre una compilación intermedia sin firma; las pruebas de la compilación final pudieron ejecutarse sin cambiar políticas ni instalar certificados. Si Windows bloquea el paquete en alguno de tus PCs, conserva la versión anterior y revisa el bloqueo con el responsable de esa política. El detalle de aquella incidencia está en VALIDACION_0.1.6.md. No se ha modificado Zscaler, FortiClient ni el firewall.
