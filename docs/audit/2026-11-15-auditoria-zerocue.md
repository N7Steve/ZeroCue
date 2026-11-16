# Auditoría técnica de ZeroCue: 15 de noviembre de 2026

Commit auditado: `e86fdac` (`main`, árbol limpio). Versión publicada: `v0.1.0-alpha.18`.
Alcance: código completo (unas 29.000 líneas de C# y XAML), comunicación USB, instalación de drivers,
pipeline de entrada y remapeo, arquitectura, MVVM, persistencia, build, CI, release y organización del repositorio.
Es una revisión de solo lectura: no se ha modificado código.

---

## 1. Resumen ejecutivo

ZeroCue funciona y tiene algunas partes sorprendentemente cuidadas: las firmas P/Invoke son correctas,
`AtomicFile` está bien hecho, la matemática de deadzones y curvas es correcta, el empaquetado está bloqueado
con lockfile, hay auditoría de licencias y la documentación del rendimiento es buena. Pero la base es frágil.
Se nota que se construyó a base de parches incrementales, cada uno arreglando un síntoma concreto
("Fix receiver driver re-enumeration", "Harden WinUSB installation with retries"...), sin un modelo
de concurrencia ni de ciclo de vida común.

**Los 4 problemas raíz que explican la mayoría de los bugs:**

1. **No hay dueño claro de los recursos ni de los hilos.** El hilo de USB, el pool de tareas, el hilo de UI y
   las tareas de macros comparten estado mutable (tablas de remapeo, handles de WinUSB, campos de sesión)
   sin sincronización. De ahí vienen los crashes, el use-after-free de handles, las teclas pegadas
   y las carreras entre conexión y desconexión.
2. **No hay una máquina de estados de conexión.** La conexión, la desconexión, la suspensión para
   instalar drivers, la transición del receptor 3A08 a 3A09 y la reconexión automática están repartidas
   en bucles `while(true)`, llamadas `.Wait(ms)` sobre código async y tareas fire-and-forget.
3. **No hay un núcleo puro y testeable.** El parseo de paquetes, los gestos, las macros, la salida
   (ViGEm y SendInput), el log y las notificaciones a la UI están mezclados en un singleton de unas
   5.000 líneas repartido en 9 ficheros `partial`, además de un `MainViewModel` de 5.291 líneas.
   Los tests actuales (34 checks) no cubren ni remapeo, ni gestos, ni macros, ni ACKs, ni drivers.
4. **El subsistema de drivers genera PowerShell como texto y lo ejecuta elevado desde `%TEMP%`.**
   Es inseguro (escalada de privilegios local), imposible de testear, y sus "tests de seguridad"
   solo comprueban que existan ciertas cadenas en el código fuente.

**Valoración global (1 a 5):**

| Área | Nota | Comentario |
|---|---|---|
| Interop nativo (P/Invoke) | 4 | Las firmas y structs son correctos. Falla el ciclo de vida de los handles. |
| Comunicación USB | 2 | Hay races, use-after-free, ACKs mal correlados y errores de lectura invertidos. |
| Instalación de drivers | 2 | La lógica es muy elaborada, pero hay un problema de seguridad grave y no se puede testear. |
| Pipeline de entrada y remapeo | 2 | Teclas pegadas, sin diagonales en el D-pad, y hay estado compartido entre hilos. |
| Arquitectura y MVVM | 1,5 | Dos objetos dios (VM y servicio), singleton usado como service locator y estáticos mutables. |
| Persistencia | 3 | La escritura atómica es buena, pero se guarda en exceso, la corrupción se gestiona mal y no hay migraciones. |
| Build, CI y release | 3 | Lockfile, auditorías y verificación de assets están bien. Faltan firma, pinning por hash y tests reales. |
| Organización y docs | 2,5 | El README es bueno. No hay AGENTS.md, el nombre "DataProbe" está obsoleto y hay 3,6 GB en `artifacts/`. |

---

## 2. Metodología

- Lectura completa de los servicios críticos, con 4 revisiones independientes en paralelo
  (USB, drivers, input/CI y arquitectura/UI). Revisé a mano en el código los hallazgos más importantes
  (están marcados ✅).
- Build Release `--no-incremental`: **0 warnings, 0 errores**. Ojo: esto no lo impone ninguna configuración,
  porque no hay `TreatWarningsAsErrors` ni analizadores.
- `tests/ZeroCue.InputChecks`: **34/34 OK**. `ZeroCue.XInputProbe --self-test`: **OK**.
- No se ejecutó nada que toque hardware ni drivers. Los hallazgos marcados como *Plausible*
  dependen de comportamiento del firmware o de Windows que hay que validar con el mando.

Escala de severidad:

- **Crítico:** crash, corrupción o una tecla pegada en un uso normal.
- **Alto:** fallo probable en escenarios reales, o un problema de seguridad.
- **Medio:** degradación, o un fallo en casos límite.
- **Bajo:** higiene del código.

---

## 3. Top 12 riesgos (atacar primero)

| # | Sev. | Problema | Dónde |
|---|---|---|---|
| R1 | Crítico | **Teclas y botones del ratón se quedan pulsados** al desconectar, cerrar la app, al lanzarse una excepción o al cambiar de perfil. El único sitio que envía key-up es la comparación entre frames. ✅ | `ScufDeviceService.RemapGestures.cs:1306-1326`, `Usb.cs:333-475`, `App.axaml.cs:26` |
| R2 | Crítico | **Las tablas de remapeo se mutan en el hilo de UI mientras el hilo USB las lee**, sin lock (`LoadProfile` hace clear y refill). Puede dar "Collection was modified", mapeos a medias y, al reventar el bucle, una desconexión. ✅ | `ScufDeviceService.Profiles.cs:147-247`, lecturas en `Usb.cs:735,1240` |
| R3 | Crítico | **Handles de WinUSB liberados (`WinUsb_Free`) con lecturas en curso.** Son `IntPtr` crudos y no hay `AbortPipe`. Provoca access violation o lecturas sobre un handle reciclado. | `WirelessDongleWinUsbTransport.cs:32,188-299,472-487`, `ScufDeviceService.Wireless.cs:554-597` |
| R4 | Crítico | **Las macros pueden dejar una tecla pulsada para siempre.** Los Down se cuentan, pero la limpieza solo descuenta 1. | `RemapGestures.cs:386-429, 488-536` |
| R5 | Alto | **Escalada de privilegios local.** El script elevado se escribe en `%TEMP%` (escribible por el usuario) y se ejecuta con `runas`. Además, `wdi-simple.exe` **no se verifica por hash en runtime** (el README dice que sí) y se busca en rutas de fallback, incluidos el directorio actual y los padres que contengan `.git`. | `DriverAutomationService.cs:52-55,397-418,670-702`, `ZeroCuePaths.cs:337-452` |
| R6 | Alto | **El bucle de lectura cableado tiene la lógica de errores invertida:** `Error.Io` resetea el contador y reintenta sin pausa, mientras que `Timeout` cuenta como error. Al desenchufar, puede quedarse girando para siempre. ✅ | `ScufDeviceService.Usb.cs:687-713` |
| R7 | Alto | **`Disconnect()` no espera a una conexión en curso**, y bloquea el hilo de UI con `CleanupWirelessAsync().Wait(1000)`. El resto de la limpieza puede ejecutarse más tarde y matar la **siguiente** sesión. ✅ | `Usb.cs:355-372`, `Wireless.cs:106-165` |
| R8 | Alto | **Los ACKs se emparejan solo por canal y opcode.** Un ACK tardío satisface al siguiente comando y desde ahí todo queda desfasado una posición. Además, los G-keys y paddles que llegan por IN 0x82 **se descartan** mientras hay un comando en vuelo (las liberaciones se pierden y queda un botón pegado). | `ScufAckReader.cs:33-105`, `Usb.cs:588-633` |
| R9 | Alto | **Se escribe configuración fija en el mando en cada conexión y cada 45 s**, ignorando el status (mappings "fake", curvas, rumble=100). Puede pisar el perfil interno del mando y, si va a flash, desgastarlo. | `DeviceModeController.cs:383-440`, `Wireless.cs:256-360` |
| R10 | Alto | **No hay instancia única.** Dos procesos (autoarranque oculto en la bandeja más un doble clic) se pelean por el USB, crean dos pads ViGEm (entrada duplicada), se truncan el log y se pisan los perfiles. ✅ | `Program.cs` (no hay Mutex) |
| R11 | Alto | **Un perfil corrupto se sobrescribe en silencio con los datos del perfil anterior** (el catch solo registra en el log y deja el estado previo en memoria). ✅ | `ScufDeviceService.Profiles.cs:254-257, 401-452` |
| R12 | Alto | **D-pad sin diagonales:** solo se aceptan los valores exactos 0x00, 0x20, 0x40 y 0x60 del hat. Arriba+derecha no produce ninguna salida (*plausible* que las diagonales sean 0x10/0x30/0x50/0x70, hay que confirmarlo con hardware). ✅ | `Usb.cs:1033-1036`, `WirelessInput.cs:95-99` |

---

## 4. Hallazgos por área

### 4.1 Comunicación USB

**Lo que está bien:**

- Los layouts de `WINUSB_PIPE_INFORMATION`, `HIDP_CAPS` y `SP_DEVICE_INTERFACE_DETAIL` (cbSize 8/6) son correctos.
- El marshalling de BOOL es correcto.
- La comprobación de `INVALID_HANDLE_VALUE` es correcta.
- La separación entre los lectores de 0x81 y 0x82 en el modo cableado (alpha.18) es un buen paso.

| ID | Sev. | Hallazgo | Ubicación | Arreglo |
|---|---|---|---|---|
| USB-1 | Crítico | Use-after-free de los handles de WinUSB (ver R3). El rumble escribe en el transporte radio sin sincronización. En el probe, la ruta de error desconecta, pero el heartbeat sigue leyendo. | `WirelessDongleWinUsbTransport.cs`, `Emulation.cs:131`, `WirelessWinUsbAggressiveSessionProbe.cs:324-346` | Un `SafeHandle` propio con `DangerousAddRef` alrededor de cada I/O. Orden de cierre: cancelar, `AbortPipe`, esperar a los workers y liberar. |
| USB-2 | Alto | Carrera entre conexión y desconexión: `Disconnect` no toma `_connectionAttemptGate`, y la sesión inalámbrica puede publicarse **después** de la suspensión para instalar drivers. | `Usb.cs:32-57,100-305`, `Wireless.cs:106-165` | Una sola máquina de estados serializada (ver §5). |
| USB-3 | Alto | `.Wait()` sobre async en el hilo de UI: la limpieza siempre agota el timeout (1 s + 0,5 s) y luego sigue en segundo plano tocando campos de la nueva sesión. | `Usb.cs:361,365`, `DeviceModeController.cs:447` | Una desconexión 100 % async con `ConfigureAwait(false)` y sin `.Wait()`. |
| USB-4 | Alto | Las tareas de comandos sobreviven al dispositivo: el keepalive se espera 500 ms pero puede tardar 1,5 s, y el perfil inicial se lanza con `CancellationToken.None`. | `Usb.cs:363-366,436-458`, `ScufDeviceService.cs:809-826` | Un CTS por sesión, y esperar a todas sus tareas antes de liberar. |
| USB-5 | Alto | Correlación de ACKs (ver R8): no se drena antes de enviar, no hay timestamps, y Eco, brillo y rumble comparten el opcode 0x01. | `ScufAckReader.cs`, `WirelessSessionController.cs:253-293` | Un correlador único: drenar, emparejar por canal, opcode y sub-opcode, y descartar respuestas anteriores al envío. |
| USB-6 | Alto | Se pierden los frames 0x82 (G-keys y paddles) mientras el lector de ACKs tiene `UsbLock`. | `ScufAckReader.cs:79-85`, `Usb.cs:588-633` | Un único lector por endpoint que enruta: las entradas a la cola de input y los ACKs al correlador. |
| USB-7 | Alto | Errores de lectura invertidos (ver R6). | `Usb.cs:687-713` | Tratar el timeout como "sin datos". `Io`, `NoDevice` y `Pipe` son fatales tras N intentos con backoff. |
| USB-8 | Medio-Alto | El transporte WinUSB "traga" errores: una escritura que falla con SEM_TIMEOUT, ABORTED o GEN_FAILURE devuelve éxito, y GEN_FAILURE en lectura se trata como "sin datos" en un bucle a unos 200 Hz. | `WirelessDongleWinUsbTransport.cs:205-219,289-294` | Un resultado tipado: `Ok`, `Timeout`, `Disconnected` o `Error`. |
| USB-9 | Alto | Escrituras fijas y periódicas en el mando (ver R9). El flag `AllowNonZeroStatus` nunca se lee. | `DeviceModeController.cs:383-440`, `WirelessInitializationSequence.cs` | Leer el estado, escribir solo si difiere, validar el status y quitar el refresco periódico. |
| USB-10 | Medio | No hay notificaciones de hot-plug (`CM_Register_Notification`): la desconexión y la transición 3A08 a 3A09 se detectan a base de timeouts, en decenas de segundos. | (no existe) | Suscribirse a la llegada y retirada de la interfaz de dispositivo. |
| USB-11 | Medio | La entrada inalámbrica se procesa en el hilo de I/O con `UsbLock` tomado (remap, ViGEm, macros y log de ficheros dentro del lock). En 3A09, el bucle del heartbeat *es* la bomba de input. | `WirelessSessionController.cs:121-147` | La bomba de lectura solo encola. El consumidor de input va en su propio hilo. |
| USB-12 | Medio | Cada lectura WinUSB hace `Task.Run` + 2 × `SetPipePolicy` + `Array.Clear`, lo que añade jitter a 250-1000 Hz. | `WirelessDongleWinUsbTransport.cs:247-268` | Fijar la policy una vez y usar un hilo dedicado con lecturas overlapped encoladas. |
| USB-13 | Medio | El probe "Aggressive" (experimental) **es** la ruta de conexión inalámbrica de producción: hasta 95 s con el gate tomado y fugas en la ruta de fallo. | `Wireless.cs:82-106`, `WirelessWinUsbAggressiveSessionProbe.cs` | Una conexión determinista, y el probe pasa a ser solo diagnóstico. |
| USB-14 | Medio | La detección `Task.CurrentId == pollingTask.Id` está rota (se compara con un `WhenAll`). Si el polling no para en 2 s, `_device` y `_context` se ponen a null **sin liberarlos**, y las interfaces quedan reclamadas. | `Usb.cs:370,384-458`, `Wireless.cs:529` | Un flag explícito de "parada interna", y no abandonar nunca recursos nativos. |
| USB-15 | Medio | La cancelación se convierte en `Exception("Timeout…")`, así que los apagados normales se registran como fallos de protocolo. Una lectura en vuelo no se puede cancelar. | `ScufAckReader.cs:89-93` | Relanzar `OperationCanceledException` y usar `CancelIoEx` o `AbortPipe`. |
| USB-16 | Medio | Comandos cableados síncronos (libusb) disparados desde handlers de UI: congelan hasta 1,5 s por comando. | `ScufRawTransport.cs:35-61`, `MainViewModel.cs:1228,1783-1790` | Una cola de comandos async en el worker de la sesión. |
| USB-17 | Bajo-Medio | El rumble cableado espera detrás de los ACKs (comparte `UsbLock` aunque OUT 0x01 es independiente). En receptores 3A09/5046 el rumble se descarta en silencio. | `Emulation.cs:85-141` | Una cola de escritura por endpoint y una ruta de rumble según la topología. |
| USB-18 | Bajo | *Plausible:* ACKs parseados como input (el normalizador acepta 0x06 en los offsets 0, 16, 32 y 48). | `WirelessInput.cs:218-291` | Clasificar los frames antes de enrutarlos. |
| USB-19 | Bajo | Paquetes cortos: no se valida `xferred` antes de leer el byte 13, y el `ByteIndex` que viene del JSON no se valida. | `Usb.cs:794-822,899` | Longitud mínima y validar el mapping al cargarlo. |
| USB-20 | Bajo | Se crea un contexto libusb nuevo cada 300-500 ms solo para detectar la presencia del cable. | `Wireless.cs:57-70,493-513` | Sustituirlo por hot-plug (USB-10). |
| USB-21 | Bajo | Estado compartido inseguro: `_heartbeatFailures++` sin Interlocked, y el singleton con `??=` no es thread-safe. ✅ | `WirelessSessionController.cs:65,187`, `ScufDeviceService.cs:35` | |
| USB-22 | Bajo | Duplicación: 2 copias de la enumeración SetupDi, 3 lectores de ACK, 3 implementaciones de heartbeat y 2 pipelines de mapeo (cableado e inalámbrico). `SetEcoMode` escribe sin lock ni ACK. | varios | Consolidar (§5). |

### 4.2 Instalación y restauración de drivers

**Lo que está bien (comprobado):**

- Manipular el manifiesto en `%APPDATA%` no permite borrar INFs arbitrarios: el proceso elevado vuelve a verificar
  cada `oem*.inf` en `C:\Windows\INF`.
- No hay inyección de línea de comandos (se usa `PowerShellLiteral`).
- No se parsea la salida de pnputil, así que no depende del idioma de Windows.
- No hay confusión entre `MI_0` y `MI_03`.

| ID | Sev. | Hallazgo | Ubicación | Arreglo |
|---|---|---|---|---|
| DRV-1 | Alto | **El script `.ps1` elevado vive en `%TEMP%`.** Cualquier proceso del mismo usuario puede reescribirlo entre la validación y el consentimiento de UAC. El diálogo de UAC dice "Windows PowerShell", no "ZeroCue". | `DriverAutomationService.cs:52-55,397-418,670-702` | Modo helper del propio exe (`zerocue.exe --driver-helper <op> <target>`) que recalcula el plan en memoria. |
| DRV-2 | Alto | **`wdi-simple.exe` no se verifica por hash en runtime** (solo en CI). El README lo afirma. Se busca con fallbacks: el directorio actual, los padres que contengan `.git` y `Assets/` antes que `resources/Assets/`. | `DriverAutomationService.cs:45-50,228`, `ZeroCuePaths.cs:337-452`, `README.md:167` | Embeber el exe como recurso, copiarlo desde el lado elevado a `%ProgramData%\ZeroCue\bin` con ACL de admin y verificar el SHA-256 contra una constante compilada. |
| DRV-3 | Alto (*plausible*) | El directorio de trabajo de libwdi (`-d`) está en `%TEMP%`: libwdi extrae y ejecuta `installer_x64.exe` y el INF ahí, como admin. | `DriverAutomationService.cs:216-228` | Un directorio de trabajo creado por el proceso elevado con ACL restrictiva. |
| DRV-4 | Alto | El "hard timeout" de 20 min no funciona: `Kill()` sobre un proceso elevado falla y luego `WaitForExitAsync()` espera sin token. El peor caso legítimo (unos 34 min) es además mayor que ese timeout. | `DriverAutomationService.cs:421-433` | El deadline lo aplica el lado elevado, que informa por named pipe. Presupuesto calculado a partir del plan. |
| DRV-5 | Alto | **iCUE se relanza elevado** (`Start-Process` dentro del PowerShell admin), y se arrancan **todos** los servicios de Corsair aunque antes estuvieran parados o en manual. | `DriverAutomationService.cs:1173-1186,382,952` | Guardar el estado previo y restaurar solo ese. Relanzar iCUE desde el proceso no elevado. |
| DRV-6 | Medio | Sin `try/finally` en el script: si se interrumpe, iCUE y los servicios de Corsair quedan parados y no se escribe el resultado. | `:169,382` | Restaurar en un `finally` (mejor en C#). |
| DRV-7 | Medio | Si la app se cierra durante la instalación, la propiedad de los paquetes se pierde (el manifiesto se escribe después de que salga el proceso elevado). | `:437-475` | Un diario de intención escrito por el lado elevado en `%ProgramData%` y reconciliado al arrancar. |
| DRV-8 | Medio | La restauración da por bueno un dispositivo **sin driver** (Code 28), porque solo comprueba `-notmatch 'WINUSB'`. Contradice el README. | `:917-921` | Capturar el servicio original y exigir ese mismo servicio con `ProblemCode == 0`. |
| DRV-9 | Medio | La restauración no tiene timeout: `WaitForExitAsync()` espera sin token. | `:707` | Igual que DRV-4. |
| DRV-10 | Medio | pnputil 3010 ("reinicio necesario") se trata como fallo, lo que provoca que falle la migración o la restauración. | `:547,552,873,890-902` | Tratar 0 y 3010 como éxito y mostrar "reinicio necesario". |
| DRV-11 | Medio | La detección de paquete "staged" usa una regex sin anclar y acepta **cualquier** INF de WinUSB (por ejemplo, de Zadig) como si fuera de ZeroCue. | `:185-193,210,261,348-350` | Parsear `[Models]` y comparar el token exacto del ID de hardware y las marcas de ZeroCue. |
| DRV-12 | Medio | El log mezcla UTF-16LE (`>`/`>>` en PS 5.1) con UTF-8 (`Out-File -Encoding utf8`), así que los tramos más útiles salen ilegibles. | `:74,81,243-244,546,551` | Un único helper de log en UTF-8. |
| DRV-13 | Medio | `ValidatePowerShellScriptSyntax` lee stdout y stderr de forma síncrona antes de `WaitForExit(10_000)`: el timeout es código muerto y se bloquea el hilo de UI durante 1-3 s. | `:1032-1034`, `MainViewModel.cs:3370-3380` | Desaparece con el helper en C#. |
| DRV-14 | Medio | No hay mutex global de instalación. El script hace `Stop-Process -Name wdi-simple/installer_x64` en **toda la máquina** (mata a Zadig o a otra instancia). | `:1162-1163,393-394` | `Global\ZeroCue.Driver` y matar solo los PIDs propios. |
| DRV-15 | Medio | `taskkill /F` sobre wdi-simple a los 90 s puede dejar un driver a medio instalar. | `:229-233` | Un tope más largo con progreso, y comprobar los problem codes antes de reintentar. |
| DRV-16/17 | Medio | Todo es un `bool`: una validación fallida del receptor se muestra como "instalado correctamente", y la cancelación de UAC (1223), "no hay dispositivo" y un timeout dan el mismo mensaje. | `:459-492`, `MainViewModel.cs:3415-3421` | `DriverOperationResult { Success, SuccessWithWarnings, Failed, Cancelled, RebootRequired, Reason, LogPath }`. |
| DRV-18 | Bajo-Medio | La restauración borra también paquetes de Zadig o "legacy" que ZeroCue no creó (el README dice lo contrario). | `:817-835` | Documentarlo o pedir confirmación explícita. |
| DRV-19 | Bajo | `($isManifestOwned -and $isZeroCueOwned) -or $isZeroCueOwned` se reduce a `$isZeroCueOwned`, así que el manifiesto no influye en la migración. | `:532,537` | Un único `InfClassifier` con tests. |
| DRV-20 | Bajo (*plausible*) | libwdi deja certificados autogenerados en Trusted Root y Trusted Publisher, y la restauración no los elimina. | n/a | Verificar en `certlm.msc`. Eliminarlos al restaurar o documentarlo. |
| DRV-21/22/23 | Bajo | `oem*.inf` validado con `TryParse` laxo. Los IDs de dispositivo están en 3 sitios con flags *experimental* incoherentes (3A04). Los logs incluyen el número de serie USB y la ruta del usuario. | varios | Un `DriverCatalog` único y redactar los logs. |

**Crítica a `scripts/verify-driver-safety.ps1`:** comprueba que existan o no ciertas *subcadenas del código fuente*,
incluidos mensajes de log literales. Congela la redacción, no el comportamiento: DRV-8, DRV-10 y DRV-11 pasan el check.

### 4.3 Pipeline de entrada, remapeo y salida

**Lo que está bien:**

- Deadzone radial.
- El clamp de -32768 y la inversión de Y con `short.MinValue`.
- El escalado de triggers de 10 bits a 0-255.
- El layout de `INPUT` (40 bytes en x64).
- El lock de coalescencia del rumble.

| ID | Sev. | Hallazgo | Ubicación | Arreglo |
|---|---|---|---|---|
| IN-1 | Crítico | Teclas pegadas (R1). | `RemapGestures.cs:1306-1326` | `ReleaseAllSyntheticOutputs()` en todas las rutas de teardown, en `ProcessExit`, en el cambio de perfil y en un `finally`. |
| IN-2 | Crítico | Macros con contador asimétrico (R4). | `RemapGestures.cs:386-429,488-536` | Que cada macro posea su propio conjunto de salidas, o un contador exacto por tecla. |
| IN-3 | Crítico | Tablas mutadas en caliente (R2). `SetRemapTarget` y `ResetGestureState` también tocan `_gestureStates` desde la UI. | `Profiles.cs:189-204`, `RemapGestures.cs:124-153,690-698` | Un `MappingSnapshot` inmutable publicado con `Volatile.Write`. |
| IN-4 | Alto | Una macro repetitiva con todos los delays a 0 consume un core al 100 % y compite por `_macroPlaybackLock` con el hilo de input. | `RemapGestures.cs:412-419` | Exigir al menos un `await` por iteración. |
| IN-5 | Alto | Los pasos de macro con delay 0 se pierden: un Down y un Up dentro del mismo frame nunca se emiten. Los delays usan `Task.Delay`, con resolución de unos 15,6 ms. | `RemapGestures.cs:345-348` | Una cola de eventos de macro que se drena por frame, o que la macro emita directamente. |
| IN-6 | Alto | `SendInput` sin scan codes ni el flag de tecla extendida: los juegos que leen raw input o DirectInput no lo ven, y las flechas se confunden con el numpad. No hay F1-F12, y el bloqueo por UIPI se ignora. | `KeyboardSimulator.cs:71-113,166-181` | `KEYEVENTF_SCANCODE` con `MapVirtualKey(MAPVK_VK_TO_VSC_EX)`, el flag extendido y loguear los fallos. |
| IN-7 | Alto | El estado de los gestos es independiente por capa de Shift y nunca se sincroniza: tras alternar Shift, la siguiente pulsación no se registra o un Hold se dispara al instante. | `RemapGestures.cs:700-703,750-763,935` | Indexar el estado por la fuente física, o resetear al cambiar de capa. |
| IN-8 | Alto | D-pad sin diagonales (R12). El comentario de `Usb.cs:~819` contradice el código. | `Usb.cs:1033-1036`, `WirelessInput.cs:95-99` | Decodificar el hat a bits y testear las 9 posiciones. |
| IN-9 | Alto (*plausible*) | Si `_xbox.Connect()` lanza una excepción, `_xbox` no queda en null y `SubmitReport` revienta la sesión. `_client` tiene una fuga. | `Usb.cs:199-212`, `Wireless.cs:138-150` | Poner `_xbox = null` y hacer dispose en el catch. |
| IN-10 | Medio | Los remapeos a teclado y ratón **no funcionan sin ViGEm** (return temprano). Si `SubmitReport` lanza una excepción, el estado de teclas queda desincronizado. | `RemapGestures.cs:1257-1260,1292` | Desacoplar la salida de teclado de ViGEm. |
| IN-11 | Medio | Las macros sobreviven a cambios de perfil y desconexiones (las de repetición, para siempre). | `RemapGestures.cs:442-453` | Cancelarlas en el teardown. |
| IN-12 | Medio | Asignaciones de memoria por paquete en el hot path: unos 31 strings por gesto, `new List`/`HashSet`, LINQ, 3 arrays por evaluación de curva y `PropertyChanged` por eje desde el hilo de input. | varios | Estado precomputado, bitflags y tangentes cacheadas. |
| IN-13 | Medio | Los gestos usan `Environment.TickCount64` (±16 ms): el pulso de 55 ms, el doble toque de 80 ms y `PADDLE_HOLD_MS=20` tienen jitter. | `Usb.cs:939`, `WirelessInput.cs:121`, `ScufDeviceService.cs:583` | Usar `Stopwatch` e `IClock`. |
| IN-14 | Medio | Editar un remapeo mientras el botón está pulsado desincroniza la tecla. | `RemapGestures.cs:694-697` | |
| IN-15 | Medio | Salir de la app puede congelar la UI unos 5,5 s (esperas encadenadas). | `App.axaml.cs:26` | Apagado async y ordenado. |
| IN-16/17 | Bajo | La lectura de XInput de diagnóstico va en el hilo de input. Se crea `new byte[64]` en cada rumble inalámbrico. | `RemapGestures.cs:1374-1427`, `Emulation.cs:122` | |

### 4.4 Arquitectura, MVVM y persistencia

**Lo que está bien:**

- `AtomicFile` (WriteThrough, `Flush(true)`, `File.Replace`).
- El patrón cancelar-y-esperar de `OnClosing`.
- La rotación de logs acotada (4 MiB × 3).
- La validación de nombres en la importación, que impide path traversal.

| ID | Sev. | Hallazgo | Ubicación | Arreglo |
|---|---|---|---|---|
| AR-1 | Alto | **No hay instancia única** (R10). | `Program.cs` | Un `Mutex` con nombre y una named pipe para traer al frente la primera instancia. |
| AR-2 | Alto | `PropertyChanged` del servicio reenviado a la UI desde hilos del pool, sin Dispatcher. | `MainViewModel.cs:3103-3162` | `IUiDispatcher` y snapshots inmutables. |
| AR-3 | Alto | **Unas 30 llamadas a `SaveProfile` síncronas en el hilo de UI**, también al arrastrar curvas (más de 60 escrituras con fsync por segundo). Cada guardado vuelve a leer el fichero (read-modify-write con carrera). | `MainViewModel.cs:1380,1438,1453`, `TriggersView.axaml.cs:77`, `Profiles.cs:405` | Un `ProfileRepository` con un único escritor y `RequestSave()` con debounce de 500 ms. |
| AR-4 | Alto | Perfil corrupto sobrescrito con datos de otro (R11). | `Profiles.cs:254-257` | Renombrarlo a `.corrupt-<ts>.json`, avisar al usuario, bloquear el guardado y mantener un `.bak`. |
| AR-5 | Alto | No hay handler de excepciones del Dispatcher. Hay `RelayCommand` sin try/catch, `File.Move` y `File.Delete` sin proteger, y `async void` (`MainViewModel.cs:3343`). | `Program.cs:18`, `MainViewModel.cs:2493,2599,5226` | Un handler global y `AsyncRelayCommand`. |
| AR-6 | Alto (*plausible*) | Apagado desordenado: `AutoConnectionLoop` es un `while(true)` sin token y puede **reconectar después de salir**. Si se loguea tras `Shutdown()`, el log se reinicializa y **se trunca**. El VM nunca se libera. | `Usb.cs:68-78`, `ZeroCueLog.cs:28-78` | Un ciclo de vida `IAsyncDisposable` con un orden fijo (ver §5). |
| AR-7 | Medio | Cargar un perfil usa setters con efectos secundarios: hasta 4 comandos al dispositivo y 4 guardados por cada cambio automático de perfil. Hay CTS sin liberar y `catch {}` vacíos. | `MainViewModel.cs:2128-2145,183,220` | Separar "aplicar estado cargado" de "el usuario editó". |
| AR-8 | Medio | El refresco de la entrada en vivo hace hasta 100 posts por segundo × unos 40 `PropertyChanged`, **incluso con la ventana oculta** mientras se juega. | `MainViewModel.cs:4495-4564` | Coalescer y pausar cuando la ventana está oculta. |
| AR-9 | Medio | La detección de la app en primer plano se hace por polling de 1 s en el hilo de UI (`Process.MainModule` lanza excepciones con anti-cheat), y está duplicada en el servicio, que además loguea cada ruta de exe. | `MainViewModel.cs:2788-2832`, `Wireless.cs:409-440` | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` y `QueryFullProcessImageName`. |
| AR-10 | Medio | Los logs están junto al exe (no se puede escribir en Program Files), **se truncan en cada arranque** (se pierde el `[FATAL]` del crash anterior) e incluyen datos personales (rutas con el nombre de usuario). | `ZeroCueLog.cs:159`, `ZeroCuePaths.cs:22` | `%LocalAppData%\ZeroCue\logs`, rotar en vez de truncar y redactar. |
| AR-11 | Medio | Sin versionado ni migraciones: `FormatVersion` solo se comprueba al importar. Hay identificadores en español persistidos ("Sin Mapeo" ×46, "Lineal", "Dinamica"). Una versión antigua pierde campos nuevos al guardar. | `Models/ScufProfile.cs`, `ProfileTransferService.cs:90` | Una cadena de `IProfileMigrator`, enums con `JsonStringEnumConverter` y negarse a guardar versiones futuras. |
| AR-12 | Medio | **El autoarranque se reescribe en cada lanzamiento y borra el override de StartupApproved:** si el usuario lo desactiva desde el Administrador de tareas, ZeroCue lo reactiva en silencio. ✅ | `Profiles.cs:43`, `WindowsStartupService.cs:68` | Borrar el override solo cuando el usuario lo activa explícitamente. |
| AR-13 | Medio | Singleton usado como service locator desde `App`, `MainWindow`, el VM y **los converters**. El constructor hace I/O, toca el registro y arranca el polling USB. Hay estáticos mutables de UI (`RemapContext.IsShiftModeUi`, `MappingIconCatalog`). | `ScufDeviceService.cs:35,528-536`, `RemapConverters.cs:29,130` | DI e interfaces (§5). |
| AR-14 | Medio | Fugas de handlers: `TriggersView` y `SticksView` añaden lambdas en cada `DataContextChanged`. `ControllerVectorView` reengancha en cada `Loaded`. | `TriggersView.axaml.cs:34`, `SticksView.axaml.cs:35`, `ControllerVectorView.axaml.cs:78-80` | Handlers con nombre y desengancharlos en `Unloaded`. |
| AR-15 | Medio | **La batería es falsa:** siempre muestra "80% - In use" (es un literal de localización). ✅ | `LocalizationService.cs:46,334`, `MainWindow.axaml:357` | Usar telemetría real u ocultarla. |
| AR-16 | Medio | La importación permite macros de teclado arbitrarias: un perfil compartido puede "teclear" Win+R seguido de un comando. Los valores de enum no se validan, y la validación no se aplica a los perfiles locales. | `ProfileTransferService.cs:144-197` | Listas de valores permitidos y un aviso de "contiene macros". |
| AR-17 | Bajo-Medio | Localización inconsistente: hay literales en el VM ("Grabando"), excepciones en español que llegan a la UI, y las claves son strings sueltos (un typo pasa en silencio). | `MainViewModel.cs:278-321,563` | `.resx` o claves generadas y un test de paridad de claves. |
| AR-18…22 | Bajo | Variantes de iconos que no persisten. Tema sin validar. Código muerto (`GetGamepadIconName`…, `AdvancedMappingIconCache`). Un rename que solo cambia mayúsculas no hace nada. Números mágicos de layout en el VM. | varios | |

**Las 16 responsabilidades de `MainViewModel`:**

1. Estado de conexión
2. Visualización de la entrada en vivo
3. RGB, rumble y eco
4. Curva de triggers
5. Curva de sticks y deadzones
6. Modal de remapeo
7. Macros
8. Shift
9. Mapeos avanzados
10. CRUD, importación y exportación de perfiles
11. Apps vinculadas y cambio automático de perfil
12. Drivers
13. Ajustes
14. Navegación
15. Log de eventos
16. Unos 8 diálogos

### 4.5 Build, CI, release y cadena de suministro

| ID | Sev. | Hallazgo | Arreglo |
|---|---|---|---|
| CI-1 | Alto | Los tests no cubren lo que se rompe: los 34 checks de `InputChecks` solo prueban lectores, la cola auxiliar, el filtro inalámbrico y los trackers. No hay tests de gestos, macros, Shift, D-pad, curvas, ACKs, drivers ni persistencia. Además acceden a miembros privados **por reflexión y por nombre** (un rename da un NullReference). | xUnit e `InternalsVisibleTo` (§6). |
| CI-2 | Alto | `release.yml`: las actions están fijadas por tag y no por SHA. `contents: write` se aplica a todo el job, incluidos los pasos que ejecutan código del repo. No hay firma Authenticode ni provenance. El `.sha256` se publica en la misma release (no añade confianza). `--clobber` reemplaza assets en silencio. No se verifica que el tag esté en `main`. | Separar build (solo lectura) de publish (escritura), fijar por SHA, `attest-build-provenance`, firmar y quitar `--clobber`. |
| CI-3 | Alto | `wdi-simple.exe` (3,3 MB, se ejecuta como admin) no tiene procedencia reproducible, según el propio `THIRD_PARTY_NOTICES.md`. | Compilarlo en CI desde un tag fijado de libwdi. |
| CI-4 | Medio | No hay `.sln` ni `Directory.Build.props`. Los proyectos de test y la herramienta no tienen lockfile. No hay `TreatWarningsAsErrors`, `AnalysisLevel` ni builds deterministas. | Configuración centralizada. |
| CI-5 | Bajo | `dotnet list package --vulnerable` siempre sale con código 0, así que ese paso de CI nunca falla. | Parsear la salida `--format json`. |
| CI-6 | Bajo | `artifacts/` ocupa **3,6 GB** en local (ignorado por git, pero contiene 18 zips de releases antiguas, auditorías y probes). | Script de limpieza y retener solo las N últimas. |
| CI-7 | Bajo | `libusb-1.0.dll` y `wdi-simple.exe` están commiteados en el repo. | Usar Git LFS o descargarlos en el build, verificados por hash. |

---

## 5. Arquitectura objetivo

```
ZeroCue.sln
├─ Directory.Build.props        Nullable, TreatWarningsAsErrors, AnalysisLevel, Deterministic, lockfiles
├─ src/
│  ├─ ZeroCue.Core/             sin Avalonia/LibUsb/ViGEm/Win32: 100 % testeable
│  │   ├─ Protocol/             parsers RawReport → ControllerState (cableado e inalámbrico), builders de comandos, tabla de opcodes
│  │   ├─ Mapping/              MappingEngine.Step(in ControllerState, MappingSnapshot, now) → OutputFrame
│  │   │                        gestos, macros (timeline), Shift, curvas y deadzones, D-pad
│  │   ├─ Profiles/             esquema, migraciones, validador, IProfileRepository
│  │   ├─ Devices/              DeviceCatalog (VID/PID/MI/topologías/experimental): fuente ÚNICA
│  │   └─ Abstractions/         IClock, IUsbEndpoint, IVirtualPad, IKeyboardMouseSink, IUiDispatcher…
│  ├─ ZeroCue.Platform.Windows/
│  │   ├─ Usb/                  WinUsbDevice (SafeHandle + overlapped + AbortPipe), EndpointPump, CommandCorrelator
│  │   ├─ Session/              ConnectionStateMachine (Idle→Enumerating→Opening→Initializing→Active→Draining→Closed)
│  │   ├─ Hotplug/              CM_Register_Notification
│  │   ├─ Output/               ViGEmPad, SendInputSink (scancodes, ReleaseAll)
│  │   ├─ Drivers/              PnpQuery (CfgMgr32), InfClassifier, DriverPlanner (puro), DriverExecutor
│  │   └─ System/               ForegroundWatcher (WinEventHook), StartupRegistration, SingleInstance
│  ├─ ZeroCue.App/              Avalonia (antes "DataProbe"): raíz de composición con DI, Shell VM + VMs hijos
│  └─ ZeroCue.DriverHelper/     (o modo --driver-helper del mismo exe) único código que corre elevado
├─ tests/
│  ├─ ZeroCue.Core.Tests/       xUnit + Verify (snapshots) + FsCheck (propiedades)
│  ├─ ZeroCue.Platform.Tests/   transporte con endpoints falsos (sucesor de InputChecks)
│  └─ data/captures/*.bin       paquetes reales grabados para tests "golden"
├─ tools/                       XInputProbe, (Aggressive probe pasa aquí, fuera de producción)
├─ scripts/
└─ docs/                        architecture.md, protocol.md, drivers.md, testing.md, audit/
```

**Contratos de concurrencia (la clave de todo):**

1. **Un hilo lector por endpoint IN.** Solo clasifica los frames: las entradas van al canal de input
   y los ACKs y respuestas al correlador. Nadie más lee de un endpoint, y desaparece `UsbLock` del camino de lectura.
2. **Un único hilo consumidor de input por sesión.** Es el dueño exclusivo del estado de gestos, macros y salida.
3. **Configuración inmutable:** la UI construye un `MappingSnapshot` y lo publica con un swap atómico.
   El hilo de input detecta el cambio de versión y resetea los gestos en su propio hilo.
4. **La sesión es dueña de su CTS y de todas sus tareas.** `DisposeAsync` cancela, aborta los pipes, espera
   a las tareas, llama a `ReleaseAll()` (teclas y pad) y luego libera los handles.
5. **La UI solo recibe snapshots inmutables**, coalescidos por un adaptador que se pausa con la ventana oculta.
6. **Persistencia con un único escritor:** una cola con debounce, sin read-modify-write.
7. **Frontera de privilegios:** el proceso normal nunca escribe nada que vaya a ejecutar el proceso elevado.

---

## 6. AGENTS.md

**Estado actual: el repo no tiene `AGENTS.md` ni `CLAUDE.md`.** El único que existe es el global
(`~/.codex/AGENTS.md`), con normas de git genéricas. Los agentes que han trabajado en ZeroCue
no tenían ninguna guía específica del proyecto. Eso explica parte de la deriva: arreglos
sintomáticos, logs en español mezclados con inglés, código duplicado entre cableado e inalámbrico,
y "tests" que comprueban cadenas de texto.

Propuesta de `AGENTS.md` para la raíz (se crearía en la fase 0; aquí solo va el borrador):

```markdown
# ZeroCue — guía para agentes

## Qué es
App Windows x64 (.NET 8, Avalonia 11) que lee un SCUF Envision Pro por WinUSB (cable o receptor),
lo expone como mando Xbox 360 vía ViGEmBus y aplica remapeos/macros. Instala drivers WinUSB con
elevación. Errores aquí = teclas pegadas en el PC del usuario, mando inutilizable o driver roto.

## Comandos
- Build:  `dotnet build ZeroCue.sln -c Release -warnaserror`
- Tests:  `dotnet test ZeroCue.sln -c Release`
- Checks: `pwsh scripts/verify-runtime-assets.ps1`, `pwsh scripts/verify-driver-safety.ps1`
- Restore siempre con `--locked-mode`; si cambias paquetes, actualiza `packages.lock.json`.

## Mapa del código
- `src/ZeroCue.Core`: lógica pura (protocolo, mapeo, perfiles). SIN referencias a Win32/USB/UI.
- `src/ZeroCue.Platform.Windows`: USB, sesión, ViGEm, SendInput, drivers, sistema.
- `src/ZeroCue.App`: UI Avalonia. Los ViewModels no tocan USB ni ficheros directamente.
- `docs/protocol.md`: formato de paquetes, opcodes, topologías por VID/PID. Actualízalo si cambias el protocolo.
- `docs/drivers.md`: flujo de instalación/restauración y frontera de privilegios.

## Reglas no negociables
1. **Nunca ejecutes instalación/restauración de drivers, wdi-simple, pnputil ni nada elevado.**
   Tampoco escribas al dispositivo real. Pide al usuario que lo pruebe con hardware.
2. **Hilos:** cada endpoint IN tiene un único lector; el estado de mapeo pertenece al hilo de input
   de la sesión; la UI solo recibe snapshots inmutables vía `IUiDispatcher`. Nada de `.Wait()`/`.Result`
   sobre async, nada de `async void` (salvo handlers de eventos de UI con try/catch), nada de
   fire-and-forget sin registrar la tarea en la sesión.
3. **Recursos nativos:** handles en `SafeHandle`; cancelar → abortar pipes → esperar workers → liberar.
4. **Toda ruta de teardown llama a `ReleaseAll()`** (teclado, ratón, pad, macros). Añade un test si
   creas una nueva ruta de salida.
5. **Escrituras al mando:** solo vía la capa de comandos, con validación de status. Nada de escrituras
   periódicas "por si acaso".
6. **Proceso elevado:** el proceso normal no escribe nada que el elevado ejecute. Hashes de binarios
   embebidos verificados en runtime.
7. **IDs de dispositivo:** solo en `DeviceCatalog`. No dupliques VID/PID en otros ficheros.
8. **Perfiles:** cualquier cambio de esquema incrementa `FormatVersion` y añade una migración + test.

## Convenciones
- Código, identificadores, logs y comentarios en **inglés**. Textos de UI solo vía localización
  (claves en ambos idiomas; hay un test de paridad).
- Logs con prefijo de categoría (`[USB]`, `[SESSION]`, `[DRIVER]`, `[INPUT]`…). Nunca loguear por paquete
  en el hot path; usar los contadores `*-PERF`. No loguear rutas de usuario ni números de serie sin redactar.
- Sin asignaciones por paquete en el hot path (parseo/mapeo).
- Tiempo: `IClock`/`Stopwatch`, nunca `Environment.TickCount64` ni `DateTime.Now` para temporización.
- Ficheros < ~600 líneas; si una clase pasa de ahí, divídela por responsabilidad.

## Cómo trabajar
- Arregla la causa raíz, no el síntoma. Si un bug es de concurrencia/ciclo de vida, escribe primero
  un test que lo reproduzca con endpoints/reloj falsos.
- Cambios de protocolo: añade una captura en `tests/data/captures` y un test golden.
- Antes de terminar: build sin warnings + `dotnet test` + scripts de verificación. Describe qué
  necesita validación con hardware (cable / receptor V2 / V1) y no afirmes que funciona sin ella.
- No toques `artifacts/`, no commitees binarios nuevos sin hash ni procedencia en THIRD_PARTY_NOTICES.

## Validación manual con hardware (checklist para el humano)
Cable y receptor por separado: conexión, desconexión en caliente, cambio cable↔receptor, G1–G5,
paddles, SAX, diagonales del D-pad, macros (tap/hold/repeat), Shift, cambio de perfil con botón
pulsado, cierre de la app con botón pulsado (ninguna tecla debe quedar pegada), rumble, RGB.
```

El `scripts/verify-driver-safety.ps1` actual también debería reescribirse: en lugar de buscar subcadenas,
debe ser un conjunto de tests de `DriverPlanner` e `InfClassifier` sobre snapshots grabados de PnP.

---

## 7. Organización del proyecto

| Problema | Propuesta |
|---|---|
| El proyecto se llama `ZeroCue.DataProbe`, un nombre heredado del prototipo, y está codificado en las URIs `avares://` y en `ZeroCuePaths`. | Renombrarlo a `ZeroCue.App` al final del refactor (es un cambio mecánico). |
| No hay `.sln` ni `Directory.Build.props`. | Crearlos en la fase 0. |
| Un `ScufDeviceService` de unas 5.000 líneas en 9 `partial` (cada partial es un "fichero por tema", no una clase). | Clases reales (§5). |
| Un `MainViewModel` de 5.291 líneas con clases anidadas. | Shell VM + unos 10 VMs hijos y CommunityToolkit.Mvvm. |
| Logs, mensajes y algunos identificadores persistidos en español ("Sin Mapeo"). | Todo en inglés, con migración de perfiles. |
| `docs/` solo tiene `input-performance.md`. El conocimiento del protocolo está disperso en comentarios y en el README. | Añadir `docs/protocol.md`, `docs/drivers.md`, `docs/architecture.md` y `docs/testing.md`. |
| `artifacts/` ocupa 3,6 GB y mezcla releases, auditorías, páginas HTML de test y respuestas a issues (`issue-4-response.md`). | `artifacts/` solo para la salida del build. Lo útil se mueve a `docs/` o `tools/`. |
| `scuf_mapping.json` es un fichero suelto que se copia al output y no se valida. | Recurso embebido, con validación al cargarlo. |
| El probe "Aggressive" está en `Services/` de producción. | Moverlo a `tools/` o a un modo de diagnóstico. |
| Hay 7 temas XAML casi idénticos de 59 líneas cada uno. | Un diccionario base más overrides de color. |

---

## 8. Plan de ataque

Principio: **primero la red de seguridad y los bugs que afectan al usuario, después el refactor por capas, y nunca un big-bang.**
Cada fase se puede publicar como su propia alpha y validar con hardware antes de pasar a la siguiente.

### Fase 0: cimientos (1-2 días, sin cambios de comportamiento)

- Crear `ZeroCue.sln`, `Directory.Build.props` (warnings como errores, analizadores, lockfiles en todos los proyectos) y `AGENTS.md` (§6).
- Crear `tests/ZeroCue.Core.Tests` con xUnit. Migrar `InputChecks` a xUnit con `InternalsVisibleTo` en vez de reflexión.
- CI: `dotnet test`, que el audit de vulnerabilidades falle de verdad y fijar las actions por SHA.
- Limpiar `artifacts/` (localmente) y documentar qué va ahí.
- **Criterio de salida:** CI en verde con la nueva estructura y la app idéntica.

### Fase 1: hotfixes críticos de usuario (3-5 días) → `alpha.19`

Arreglos acotados sobre el código actual, cada uno con su test cuando sea posible:

1. **IN-1/R1:** `ReleaseAllSyntheticOutputs()` llamado desde `Disconnect`, Exit, `ProcessExit`, el catch del polling, el cambio de perfil y la pérdida de sesión inalámbrica.
2. **IN-2, IN-4, IN-11:** contadores de macro correctos, un yield mínimo en macros de repetición y cancelación en el teardown y al cambiar de perfil.
3. **IN-3/R2:** el snapshot inmutable de las tablas de remapeo (es el cambio más pequeño que elimina la carrera).
4. **USB-7/R6:** corregir la lógica de errores del bucle cableado.
5. **AR-1/R10:** Mutex de instancia única.
6. **AR-4/R11:** cuarentena de perfiles corruptos.
7. **IN-8/R12:** D-pad con diagonales (**hay que capturar los valores reales del hat con el mando**).
8. **AR-12:** no reactivar el autoarranque en silencio.
9. **IN-9:** gestionar bien los fallos de `ViGEm.Connect`.
10. Quitar el indicador falso de batería (AR-15).

### Fase 2: seguridad de drivers (1 semana) → `alpha.20`

- DRV-2: embeber `wdi-simple.exe` y verificar su SHA-256 en runtime desde un directorio con ACL de admin. Eliminar los fallbacks de ruta en Release (también AR `ZeroCuePaths`).
- DRV-1 y DRV-3: modo `--driver-helper` del propio exe. El script pasa a construirse **dentro** del proceso elevado (paso intermedio) y luego se migra a C# con CfgMgr32.
- DRV-4, 6, 9, 10, 14: deadline aplicado por el lado elevado, `try/finally` para restaurar Corsair, 3010 como éxito y mutex global.
- DRV-5: restaurar solo el estado previo de iCUE y relanzarlo sin elevación.
- DRV-16/17: `DriverOperationResult` tipado y mensajes distintos en la UI.
- Extraer `DeviceCatalog`, `InfClassifier` y `DriverPlanner` como código puro, con tests a partir de snapshots reales de PnP (3A08↔3A09, dispositivos fantasma, INFs de Zadig, 3010).
- **Criterio:** `verify-driver-safety.ps1` sustituido por tests de comportamiento, y el README alineado con el código.

### Fase 3: núcleo USB y sesión (2-3 semanas) → `alpha.21`

Por orden:

1. `WinUsbDevice` con `SafeHandle`, overlapped, `AbortPipe` y resultados tipados (USB-1, 8, 15).
2. `EndpointPump` (un lector por endpoint) más `CommandCorrelator` (USB-5, 6, 11, 12, 18). Unifica los 3 lectores de ACK y los 3 heartbeats.
3. `ConnectionStateMachine` con un CTS por sesión, desconexión async y hot-plug (USB-2, 3, 4, 10, 14, 20, AR-6).
4. Una capa de comandos de dispositivo que lee, compara y escribe validando el status. Fuera las escrituras periódicas (USB-9). **Esto requiere sesiones con hardware para confirmar qué persiste el firmware.**
5. Migrar la ruta cableada de LibUsbDotNet a WinUSB directo, para tener un único modelo de handles (opcional pero recomendable).
6. Sacar el probe "Aggressive" de producción (USB-13).

Tests: endpoints falsos y scriptados que reproducen ACKs tardíos, desconexiones en mitad de una lectura y la transición 3A08→3A09.

### Fase 4: motor de mapeo puro (1-2 semanas) → `alpha.22`

- Parsers `RawReport → ControllerState` y un único pipeline para cable e inalámbrico.
- `MappingEngine.Step(...)` puro con `IClock`: gestos (IN-7, IN-13), macros como timeline (IN-5), Shift y curvas.
- Sinks: `SendInputSink` con scan codes y teclas extendidas (IN-6), independiente de ViGEm (IN-10).
- Eliminar las asignaciones en el hot path (IN-12).
- **Tests golden:** grabar capturas reales (cable y receptor V2/V1) en `tests/data/captures`, reproducirlas y comparar con snapshots de `OutputFrame`.
- Tests de propiedades: cada Down tiene su Up tras `ReleaseAll`, salidas dentro de ±32767, monotonía de las curvas.

### Fase 5: UI, persistencia y DI (2-3 semanas, incremental)

1. `IScufDeviceService` + `IUiDispatcher` inyectados (AR-2), sin cambios de comportamiento.
2. `ProfileRepository` con escritor único y debounce (AR-3, AR-7), versionado y migraciones (AR-11), y "Sin Mapeo" pasa a enum.
3. Ciclo de vida de la app y orden de apagado (AR-6), handler global (AR-5), logs en `%LocalAppData%` con rotación (AR-10).
4. CommunityToolkit.Mvvm y división del VM, del menos al más acoplado: Settings → DriverSetup → EventLog → Profiles/LinkedApps → Lighting → Curves → LiveInput → Macros → Remap/Advanced/Shift.
5. `ForegroundWatcher` con WinEventHook (AR-9), refresco coalescido y pausado al ocultar la ventana (AR-8), y arreglar las fugas de handlers (AR-14).
6. Localización con claves tipadas y test de paridad (AR-17). Validar las importaciones y avisar de las macros (AR-16).

### Fase 6: release y cadena de suministro (en paralelo desde la fase 2)

- Separar los jobs de build y publish en `release.yml`, `attest-build-provenance`, quitar `--clobber` y verificar que el tag está en `main`.
- Compilar `wdi-simple` desde las fuentes de libwdi en CI (CI-3).
- Firma Authenticode (por ejemplo, Azure Trusted Signing o un certificado OV) cuando sea viable.
- Builds reproducibles (`ContinuousIntegrationBuild`, zip determinista).
- Rename final `DataProbe` → `ZeroCue.App`.

### Esfuerzo y orden recomendado

```
Semana 1      F0 + F1 (hotfixes)      → alpha.19   ← máximo impacto/usuario por hora invertida
Semana 2      F2 (drivers/seguridad)  → alpha.20
Semanas 3-5   F3 (USB/sesión)         → alpha.21   ← la más arriesgada; requiere hardware
Semanas 5-6   F4 (motor de mapeo)     → alpha.22
Semanas 6-9   F5 (UI/persistencia)    → beta.1
Continuo      F6
```

---

## 9. Validación con hardware necesaria (no se puede deducir del código)

1. Valores reales del hat en las diagonales (IN-8).
2. Si el firmware persiste en flash las escrituras de mappings, curvas y rumble (USB-9).
3. Si las respuestas ACK repiten el sub-opcode, para la correlación (USB-5).
4. Si el mando envía datos estando en reposo, o deja de enviarlos (afecta a USB-7).
5. Qué códigos de error da WinUSB y libusb al desenchufar el cable y el receptor (USB-7, USB-8).
6. Si libwdi deja certificados en el almacén de la máquina (DRV-20).
7. El comportamiento del rumble en los receptores 3A09/5046 (USB-17).

Recomendación: antes de la fase 3, hacer una sesión de captura con el mando (cable, receptor V2 y, si está disponible, el V1)
para construir el corpus de `tests/data/captures`. Es lo que va a hacer posible todo lo demás sin depender de probar a mano.

---

## 10. Estado de ejecución (actualizado 2026-11-15)

Primer lote aplicado, centrado en drivers, detección y comunicación USB. Pendiente de validar con hardware.

| Hallazgo | Estado | Cambio |
|---|---|---|
| DRV-1/2/3 (escalada local) | ✅ | `zerocue.exe --driver-helper`. Hash de `wdi-simple` verificado en memoria y ejecutado desde `%SystemRoot%\Temp\ZeroCue-<guid>` con ACL solo para administradores. El PowerShell generado está eliminado. |
| DRV-4/9 (timeouts) | ✅ | El deadline lo aplica el propio helper (8 min). La app espera 9 min sin matar el proceso. |
| DRV-5/6 (iCUE) | ✅ | Solo se reinician los servicios que ya corrían, dentro de un `finally`. iCUE se relanza sin elevación y solo si estaba abierto. |
| DRV-8/10/11/16/17/19/21 | ✅ | Comprueba de verdad el driver original al restaurar. 3010 cuenta como éxito con reinicio pendiente. `InfClassifier` usa tokens exactos. Resultado tipado (`DriverOperationResult`) con mensajes distintos por caso. |
| DRV-14 (concurrencia) | ✅ | Mutex `Global\ZeroCue.DriverOperation`. Ya no se mata `wdi-simple` en toda la máquina. |
| Instalación lenta / paquete preinstalado ignorado | ✅ | Vía rápida `UpdateDriverForPlugAndPlayDevices` cuando el paquete ya está en el Driver Store. CfgMgr32 sustituye a `Get-PnpDevice`. Sondeo cada 250 ms en vez de 1 s. |
| Detección lenta / log inundado | ✅ | `ScufPresenceSnapshot` (~20 ms) y `DevicePresenceMonitor` (`CM_Register_Notification`). Solo se abren transportes con WinUSB. Estados claros: falta el driver / error de Windows (código N) / configurándose. |
| USB-1 (use-after-free) | ✅ | Puerta de vida útil en el transporte: abort de pipes → esperar la I/O en curso → `WinUsb_Free`. |
| USB-3 (`.Wait` en la UI) | ✅ | La limpieza se ejecuta en el thread pool. |
| USB-5/6 (ACKs) | ✅ parcial | Drenado de ACKs viejos antes de enviar y reenvío de G-keys/paddles leídos durante la espera. Falta el correlador completo. |
| USB-7 (bucle de lectura cableado) | ✅ | Los errores de I/O cuentan y tienen backoff. `NoDevice` desconecta. |
| USB-8 (errores tragados) | ✅ | Hay límite de fallos consecutivos en escritura y en lectura `GEN_FAILURE`. |
| USB-12 (latencia por `SetPipePolicy`) | ✅ | La política del pipe se cachea. |
| USB-15 | ✅ | La cancelación ya no se reporta como timeout de protocolo. |
| USB-16 | ✅ | La E/S libusb de comandos va fuera del hilo de UI. |
| IN-1/2/4/9 | ✅ | `ReleaseAllSyntheticOutputs` en la desconexión y en el crash. Contador exacto de macros. Las macros sin delay ya no saturan la CPU. `ViGEm.Connect` fallido no tumba la sesión. |
| IN-10 | ✅ parcial | El teclado ya no depende de ViGEm en inalámbrico. En cable, el bloque de gestos sigue condicionado a ViGEm. |
| AR-1 | ✅ | Instancia única: la segunda instancia trae la ventana al frente. |
| AR-12 | ✅ | El autoarranque ya no se reescribe en cada inicio. Solo se repara si el exe registrado ya no existe. |
| Fase 0 | ✅ | `ZeroCue.sln`, `Directory.Build.props` (warnings como errores), `tests/ZeroCue.Tests` (xUnit, 49 tests), CI con `dotnet test`, `AGENTS.md`. |

Siguientes pasos:
- Validación con hardware.
- Fase 3 completa: correlador de comandos y máquina de estados de conexión. Quitar el refresco periódico de 45 s (USB-9) una vez validado.
- R2/IN-3: snapshot inmutable de las tablas de remapeo.
- IN-6/7/8.
- Fase 5.
