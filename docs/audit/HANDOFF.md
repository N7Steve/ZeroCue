# Handoff ZeroCue: 15 de noviembre de 2026

Documento de traspaso para el siguiente agente o sesión. Léelo junto a `AGENTS.md` y
`docs/audit/2026-11-15-auditoria-zerocue.md` (auditoría completa; §10 tiene la tabla de estado).

## 1. Estado actual

- Rama `main`, último commit `e86fdac`. **Hay 28 cambios sin commitear:** todo el lote de
  drivers, detección y USB descrito abajo. El usuario solo hace commit cuando lo confirma
  explícitamente (regla global de git).
- La build y los tests están en verde:
  - `dotnet build ZeroCue.sln -c Release`: 0 warnings, con warnings tratados como errores.
  - `tests/ZeroCue.Tests`: 49 tests xUnit.
  - `tests/ZeroCue.InputChecks`: 34 checks.
  - `scripts/verify-driver-safety.ps1`: OK.
- Builds de prueba en el escritorio del usuario y en `artifacts/`:
  - `ZeroCue-v0.1.0-alpha.19-dev.1`: tiene el bug del mutex. No usar.
  - `ZeroCue-v0.1.0-alpha.19-dev.2`: probada por el usuario (cable y receptor funcionan). Ver §1.1.
  - `ZeroCue-v0.1.0-alpha.19-dev.3`: añade el arreglo de «reinicio pendiente» (§1.1). Probada (§1.2).
  - `ZeroCue-v0.1.0-alpha.19-dev.4`: arreglos de §1.2. Probada (§1.3).
  - `ZeroCue-v0.1.0-alpha.19-dev.5`: arreglo de §1.3. **Pendiente de prueba.**

### 1.1 Resultado de la prueba de la dev.2 (15/11, 16:22–16:45)

- **Receptor:** vía rápida OK (`applied staged package oem55.inf`, helper en 9 s). Pero
  `UpdateDriverForPlugAndPlayDevices` devolvió `reboot=True` y el `CreateFile` del 3A09 dio
  `win32=31` hasta que el usuario reconectó el receptor; luego conectó y funcionó.
- **Cable:** instalación correcta pero lenta (223 s): wdi-simple se colgó **90 s** (timeout) en
  MI_04 y en MI_03 aunque el binding ya estaba hecho. Después, el handshake de control falló cada 2 s
  durante 2 min hasta que el usuario reconectó el cable.
- **Causa común:** el registro ya dice WinUSB pero sigue corriendo el stack HID anterior, porque
  algún programa tenía el dispositivo abierto y Windows no pudo reiniciarlo (`DN_NEED_RESTART`).
- **Arreglo en dev.3 (sin commitear):**
  - `PnpDevice.IsRestartPending`: flag `DN_NEED_RESTART` o nodo WinUSB cuya interfaz
    `DeviceInterfaceGUIDs` no está registrada (`PnpQuery.IsWinUsbInterfaceActive`).
    Verificado en solo lectura con el receptor real: `iface=True`, sin falsos positivos.
  - Nuevo estado `ScufDeviceReadiness.RestartPending`: el bucle no reintenta y la UI pide reconectar
    (`StatusWiredRestartPending` / `StatusReceiverRestartPending`).
  - Motor: `BindingState.PendingRestart`; nunca se regenera un paquete por esto. Al final,
    `FinishPendingRestartsAsync` hace `pnputil /restart-device` (el padre compuesto si es del mismo
    VID/PID, nunca un hub) y, si sigue pendiente, `ReconnectRequired=true` con aviso en vez de
    «reinicia Windows».
  - wdi-simple: `ProcessCompletionProbe`; si el paquete nuevo ya está publicado y asignado, se le dan
    5 s (`WdiCompletionGrace`) y se termina, en lugar de esperar los 90 s.
  - `[USB] Wired connection attempt failed: …`: el motivo real del fallo del handshake, una vez por causa.
  - Tests: 5 nuevos (54 en total).
- **Qué validar con la dev.3:** instalar el driver por cable y del receptor **con Unity/Steam abiertos**
  (para provocar el veto): esperado `restart-device … exit=0` y `Pending device restart completed`, o
  el aviso de reconectar; y que wdi-simple ya no llegue a 90 s (`stoppedEarly=True`).

### Hardware del usuario (verificado en PnP, solo lectura)

- **Receptor V2:** `1B1C:3A08` en modo base, `1B1C:3A09` en modo activo. Está conectado a un hub VIA
  `2109:2813`. En ese hub dio Code 10 (`0xC000009A`); se recomendó conectarlo a un puerto directo
  de la placa.
- **Estado del receptor al cerrar la sesión:** `3A09` presente con **HidUsb**. El paquete WinUSB
  de ZeroCue `oem55.inf` está *staged* pero no aplicado, porque Windows no vuelve a elegir driver
  para un dispositivo que ya conoce. El instalador nuevo lo resuelve con
  `UpdateDriverForPlugAndPlayDevices` (vía rápida). **Confirmado con la dev.2** (§1.1).
- **Mando por cable `1B1C:3A05`:** no estaba conectado. Sus paquetes `oem91/93/94` siguen en el
  Driver Store.
- **Unity Editor** abre las interfaces del mando por cable y bloquea su retirada durante los
  cambios de driver.

### Qué se hizo en este lote (sin commitear)

| Área | Cambio | Ficheros clave |
|---|---|---|
| Drivers | Helper elevado en C# (`zerocue.exe --driver-helper`). Sustituye al PowerShell generado en `%TEMP%`. | `Services/Drivers/*`, `Services/DriverAutomationService.cs`, `Program.cs` |
| Drivers | Hash de `wdi-simple` verificado en memoria y carpeta de trabajo con ACL solo para administradores. | `ElevatedDriverHelper` |
| Drivers | Mutex global; deadline de 8 min; tratamiento de 0/3010; restauración de iCUE con su estado previo; resultado tipado; log y progreso por named pipe. | `ElevatedDriverHelper`, `DriverOperationEngine`, `DriverAutomationService` |
| Drivers | Vía rápida para paquetes *staged*; clasificación exacta de INF. | `DriverOperationEngine`, `InfClassifier`, `DriverCatalog` |
| Detección | CfgMgr32 (~20 ms), hot-plug con `CM_Register_Notification` y estados Ready / Settling / MissingDriver / DeviceError. | `Services/Pnp/*`, `ScufDeviceService.Usb.cs` (`AutoConnectionLoop`, `RefreshPresence`, `ApplyPresenceStatus`) |
| USB | Puerta de vida útil del handle WinUSB (abort, esperar, liberar); cache de `SetPipePolicy`; límite de fallos consecutivos. | `WirelessDongleWinUsbTransport.cs`, `WinUsbInterop.cs` |
| USB | Drenado de ACKs viejos; las G-keys leídas durante un comando se reenvían a la cola; cancelación ≠ timeout. | `ScufAckReader.cs`, `ScufDeviceService.Usb.cs` (`ForwardWiredAuxiliaryFrame`) |
| USB | Bucle de lectura cableado corregido; `Disconnect` sin `.Wait()` en la UI; libusb fuera del hilo de UI. | `ScufDeviceService.Usb.cs`, `ScufRawTransport.cs` |
| Input | `ReleaseAllSyntheticOutputs` en desconexión y crash; contador exacto de macros; macros sin delay sin spin; fallo de ViGEm no tumba la sesión. | `ScufDeviceService.RemapGestures.cs` |
| App | Instancia única; el autoarranque ya no se reescribe en cada inicio. | `SingleInstanceGuard.cs`, `WindowsStartupService.cs` |
| Repo | `ZeroCue.sln`, `Directory.Build.props`, xUnit, CI con `dotnet test`, `AGENTS.md`, README. | raíz, `tests/ZeroCue.Tests`, `.github/workflows` |

### 1.2 Resultado de la prueba de la dev.3 (15/11, 17:57–18:01)

Las 4 operaciones (restaurar e instalar, cable y receptor) terminaron bien.

- **Cable, instalación:** 42 s en lugar de 223 s. wdi-simple paró antes de tiempo en las 3 interfaces
  (`stoppedEarly=True`, unos 7 s cada una). `restart-device` del padre devolvió 3010 (reinicio
  vetado), el resultado fue `reconnect=True`, la UI mostró `RestartPending` y, al reconectar,
  `Ready` y conexión.
- **Receptor, instalación:** 98 s. Se perdieron 2×39 s en wdi-simple con MI_04/MI_03 del modo base,
  que no estaban conectados. `restart-device` del 3A09 devolvió 50 (reinicio del sistema pendiente) y
  la reconexión lo resolvió.
- **Restauraciones:** 5,7 s (cable) y 4,6 s (receptor). Correctas.
- **`[FATAL] SafeHandle cannot be null`:** al pulsar restaurar durante el handshake de cable,
  `SuspendConnectionsAsync` llamaba a `Disconnect()` antes de tomar `_connectionAttemptGate` y
  liberaba el handle mientras el intento de conexión aún lo usaba (violaba la regla 6). Se recuperó solo.
- **Arreglos en dev.4 (sin commitear):**
  - `SuspendConnectionsAsync` cancela el intento en vuelo (`_connectAttemptCts`, más
    `_wirelessSessionCts` si aún no hay conexión), espera el gate y solo entonces desconecta.
    El intento por cable usa ese token en el handshake y en Software Mode.
  - `IsWdiWorkComplete` también da por terminado un binding ausente (estado del receptor no
    conectado) en cuanto su paquete nuevo está publicado. Test nuevo (55 en total).
- **Qué validar con la dev.4:** que la instalación del receptor baje a unos 30 s
  (`stoppedEarly=True` en MI_04/MI_03), y que restaurar o instalar con el mando conectándose ya no
  produzca `[FATAL]`.

### 1.3 Resultado de la prueba de la dev.4 (15/11, 18:20–18:28)

- **Receptor:** la instalación tardó 4,3 s (vía rápida, los paquetes ya estaban *staged*) y la
  restauración 10 s; ambas correctas. No llegó a ejercitarse la parada temprana con estados ausentes,
  porque wdi-simple no se ejecutó.
- **Sin `[FATAL]`** en las secuencias de suspender y conectar.
- **Problema nuevo:** el mando por cable apareció con **Code 10 en el padre compuesto**
  (`usbccgp!10`, desde las 18:21:04, ya presente antes de la restauración del receptor, que no toca el
  3A05). El usuario lanzó «instalar driver del mando» con el mando en ese estado: sin interfaces, el
  helper ejecutó wdi-simple 3×39 s por interfaz hasta la deadline de 8 min. Probablemente es el hub
  VIA otra vez (ver memoria y §1, hardware).
- **Arreglo en dev.5:** `RecoverFailedCompositeParentAsync`. Antes de cerrar iCUE, si el padre
  compuesto tiene problem code, se intenta un `restart-device`. Si sigue fallando, el resultado es
  `DriverOperationStatus.DeviceError` con `ProblemCode`, y la UI muestra `StatusDeviceErrorFormat`
  (desconectar y usar otro puerto). 2 tests nuevos (57 en total).
- **Qué validar con la dev.5:** conectar el mando a un puerto directo de la placa, instalar y restaurar
  cable y receptor, y repetir la prueba de restaurar mientras el mando conecta. Si vuelve a aparecer
  Code 10, instalar debe terminar en segundos con el mensaje de error.

## 2. Lo primero que hay que hacer

1. **Recoger el resultado de la prueba con hardware de la dev.5** (dev.2–dev.4 analizadas en §1.1–§1.3). El log está en
   `Desktop\ZeroCue-v0.1.0-alpha.19-dev.2-win-x64-portable\logs\zerocue-communication.log`.
   Busca las líneas `[DRIVER]`, `[DRIVER-HELPER]` y `[DETECT]`.
   - **Esperado al instalar el driver del receptor:** «applied staged package oem55.inf ok=True»,
     estado `Success` en pocos segundos y conexión inalámbrica automática.
   - Después hay que probar la restauración del receptor y la instalación y restauración por
     cable, con iCUE abierto y cerrado.
2. Si todo pasa, **pedir confirmación para hacer commit**, dividido en commits lógicos:
   1. Drivers.
   2. Detección.
   3. Transporte y ACK.
   4. Input y app.
   5. Repo, tests y documentación.

   No incluir `artifacts/`.
3. Si algo falla, reproducirlo primero en `FakeDriverSystem` o `ScufAckReaderTests` antes de tocar
   la lógica.

## 3. Backlog priorizado

Las referencias (R*, USB-*, IN-*, AR-*, DRV-*) son de la auditoría. Para cada punto: problema →
qué hacer → criterio de aceptación.

### P0: bugs con impacto directo en el usuario

1. **R2 / IN-3: carrera en las tablas de remapeo al cambiar de perfil.**
   - **Problema:** `LoadProfile` (`ScufDeviceService.Profiles.cs`, llamadas a `MergeDict(...)`) vacía
     y vuelve a llenar en el hilo de UI los diccionarios que el hilo de input lee sin lock.
     `SetRemapTarget` y `ResetGestureState` también tocan `_gestureStates` desde la UI.
   - **Qué hacer:** crear un `MappingSnapshot` inmutable (todas las tablas, macros y delays) y
     publicarlo con `Volatile.Write`. El hilo de input lee solo el snapshot y resetea los gestos
     cuando cambia la versión.
   - **Aceptación:** un test que cambie de perfil en bucle mientras otro hilo procesa frames sin
     excepciones ni mapeos mixtos. Probar con hardware el cambio de perfil con un botón pulsado.

2. **IN-8: D-pad sin diagonales.**
   - **Problema:** `ScufDeviceService.Usb.cs` (`AddButtonGesture("Up", hat == 0x00)` …) y
     `ScufDeviceService.WirelessInput.cs` (`dpadHat == 0x..`) solo aceptan los valores exactos
     0x00, 0x20, 0x40 y 0x60. El cálculo de Shift por D-pad tiene el mismo problema.
   - **Qué hacer:** pedir al usuario una captura de los valores reales del hat en diagonal
     (*plausible*: 0x10, 0x30, 0x50, 0x70) y crear una función pura `DecodeHat(byte) → (up, right, down, left)`.
   - **Aceptación:** test con las 9 posiciones y los dos caminos (cable e inalámbrico) usando la función.

3. **IN-10 (resto): en cable, todo el bloque de gestos depende de ViGEm.**
   - **Problema:** `ScufDeviceService.Usb.cs` (`if (_xbox != null)` alrededor de la sección
     «Sticks and triggers»). Sin ViGEmBus, los remapeos a teclado y ratón no funcionan por cable.
     En inalámbrico ya funcionan.
   - **Qué hacer:** sacar el cálculo de gestos fuera del condicional. `SubmitVirtualOutput` ya
     gestiona `_xbox == null`.

4. **IN-6: `SendInput` solo con VK.**
   - **Problema:** `KeyboardSimulator.cs` no envía scan codes ni el flag de tecla extendida, no
     tiene F1-F12 e ignora el valor de retorno (UIPI).
   - **Qué hacer:** usar `KEYEVENTF_SCANCODE` con `MapVirtualKey(vk, MAPVK_VK_TO_VSC_EX)`, añadir el
     flag extendido para flechas, Ins/Supr/Inicio/Fin/RePág/AvPág, Ctrl y Alt derechos y Num /, y
     loguear los fallos.
   - **Aceptación:** test de la tabla VK → scan code y teclas extendidas.

5. **IN-7: estado de los gestos separado por capa de Shift.**
   - **Problema:** `ScufDeviceService.RemapGestures.cs` (`GetGestureStateKey`, `UpdateShiftHeld`).
     Al alternar Shift con un botón pulsado, la siguiente pulsación se pierde o un Hold se
     dispara al instante.
   - **Qué hacer:** indexar el estado por la fuente física, o cerrar ambas capas al cambiar de capa.

6. **AR-4: un perfil corrupto se sobrescribe con los datos del perfil anterior.**
   - **Problema:** el `catch` de `LoadProfile` solo escribe un warning en el log.
   - **Qué hacer:** renombrar el fichero a `*.corrupt-<ts>.json`, avisar en la UI, bloquear el
     guardado con ese nombre y mantener un `.bak`.

### P1: comunicación USB (fase 3 de la auditoría)

7. **USB-9: escrituras periódicas al mando.**
   - **Problema:** `StartWirelessSoftwareModeRefresh` (cada 45 s) y `ApplyWirelessRuntimeProfileAsync`
     reescriben RGB, brillo y el supresor de G5. `DeviceModeController` reenvía mappings y curvas
     «fake» con `ReplayTolerant`.
   - **Qué hacer:** averiguar con hardware si el firmware vuelve solo al modo hardware. Si no lo
     hace, quitar el refresco periódico. Escribir solo cuando el valor difiera y validar el status.
8. **USB-5 (completo): correlador de comandos.**
   - **Problema:** hay 3 lectores de ACK y 3 heartbeats: `ScufAckReader`, `WirelessSessionController`
     y `WirelessWinUsbAggressiveSessionProbe`.
   - **Qué hacer:** unificarlos en un `CommandCorrelator` (un comando en vuelo por canal, emparejado
     por canal, opcode y sub-opcode si el firmware lo devuelve) y una bomba lectora única por endpoint IN.
9. **USB-2: máquina de estados de conexión.**
   - **Qué hacer:** conectar, desconectar y suspender pasan por una única cola (`Idle → Opening →
     Initializing → Active → Draining`). Cada sesión es dueña de su CTS y de sus tareas.
     `DisconnectAsync` real. Eliminar `Task.CurrentId == pollingTask.Id` (no funciona).
   - **Aceptación:** un test con transporte falso que compita conexión contra desconexión.
10. **USB-11: input procesado dentro de `UsbLock`.**
   - **Problema:** el heartbeat drena el pipe unificado y procesa el input dentro del lock.
   - **Qué hacer:** la bomba encola los frames y un consumidor dedicado los procesa.
11. **USB-13: el probe «Aggressive» es la ruta de conexión de producción.**
   - **Qué hacer:** convertirlo en una conexión determinista y dejar el probe solo como herramienta
     de diagnóstico.
12. **USB-17: rumble.**
   - En cable espera detrás de los ACKs.
   - En los receptores `3A09`/`5046` se descarta en silencio.
   - **Qué hacer:** una cola de escritura por endpoint.

### P1: seguridad y drivers (pendientes del lote anterior)

13. **Extracción nativa en single-file.**
    - **Problema:** `IncludeNativeLibrariesForSelfExtract=true` (en `scripts/publish-portable.ps1`)
      extrae DLLs nativas a `%TEMP%\.net\…`. El helper elevado no las carga, porque usa
      `SetDefaultDllDirectories` y no inicializa Avalonia, pero es una superficie innecesaria.
    - **Qué hacer:** valorar un `ZeroCue.DriverHelper.exe` mínimo (sin Avalonia, sin extracción y,
      en el futuro, firmado).
14. **DRV-20: certificados que deja libwdi.**
    - **Problema:** cada paquete deja un certificado «USB\VID_… (libwdi autogenerated)» en Trusted
      Root y Trusted Publisher. Está confirmado por el campo «Nombre del firmante» de
      `pnputil /enum-drivers`.
    - **Qué hacer:** eliminar al restaurar los certificados cuyo subject coincida con los IDs del
      catálogo, o documentarlo en el README.
15. **CI-3: `wdi-simple.exe` sin procedencia.**
    - **Qué hacer:** compilarlo en CI desde un tag fijado de libwdi y actualizar a la vez
      `WdiSimpleSha256` y `scripts/verify-runtime-assets.ps1`.
16. **DRV-23 / AR-10: datos personales en los logs.**
    - **Qué hacer:** redactar los números de serie de los instance IDs y las rutas de usuario
      (incluido el `[FOREGROUND]` de `StartWirelessForegroundMonitor`).

### P2: arquitectura y app (fase 5)

17. **AR-6: orden de apagado.**
    - **Problema:** `AutoConnectionLoop` es un `while(true)` sin token, así que puede reconectar
      después de salir. `ZeroCueLog` se reinicializa y **trunca el log** (`FileMode.Create`) si se
      escribe tras `Shutdown()`.
    - **Qué hacer:** un ciclo de vida `IAsyncDisposable` con orden fijo: auto-connect → liberar
      salidas → desconectar → ViGEm → guardar → log.
18. **AR-10: logs.**
    - **Problema:** se truncan en cada arranque y están junto al exe.
    - **Qué hacer:** moverlos a `%LocalAppData%\ZeroCue\logs` y rotar en vez de truncar, para
      conservar el `[FATAL]` del crash anterior.
19. **AR-3 / AR-7: persistencia.**
    - **Problema:** hay unas 30 llamadas a `SaveProfile` síncronas en el hilo de UI, incluido el
      arrastre de curvas.
    - **Qué hacer:** un `ProfileRepository` con escritor único y debounce de unos 500 ms. Separar
      «aplicar perfil cargado» de «el usuario editó».
20. **AR-2 / AR-8 / AR-9: hilo de UI.**
    - Reenviar el `PropertyChanged` del servicio con el Dispatcher.
    - Coalescer el refresco de la entrada en vivo y pausarlo con la ventana oculta.
    - Foreground con `SetWinEventHook` en vez de sondear cada segundo, y eliminar el sondeo duplicado.
21. **AR-11: versionado de perfiles.** Migraciones por `FormatVersion` y enums en lugar de
    «Sin Mapeo», «Lineal» y «Dinamica».
22. **Dividir `MainViewModel` (5.300 líneas).** Usar CommunityToolkit.Mvvm y VMs hijos en el orden
    de §8, fase 5 de la auditoría.
23. **AR-15: batería falsa.** «80% - In use» es un literal. Mostrar telemetría real u ocultarla.
24. **AR-14 y otros:**
    - Fugas de handlers en `TriggersView`, `SticksView` y `ControllerVectorView`.
    - Código muerto en el VM.
    - Renombrar `DataProbe` a `ZeroCue.App` al final.

### P2: tests y CI

25. Migrar `tests/ZeroCue.InputChecks` a xUnit con `InternalsVisibleTo`; ahora usa reflexión por
    nombre de miembro privado.
26. **Tests golden:** grabar paquetes reales (cable, receptor base y receptor activo) en
    `tests/data/captures` y reproducirlos contra el parser y el mapeo.
27. `release.yml`:
    - Fijar las actions por SHA.
    - Separar build (solo lectura) de publish (escritura).
    - `attest-build-provenance`.
    - Quitar `--clobber`.
    - Verificar que el tag está en `main`.
28. Que el paso `dotnet list package --vulnerable` falle de verdad parseando la salida `--format json`.

## 4. Riesgos y trampas conocidas

- **No ejecutar nunca operaciones de drivers ni escribir en el mando** (regla 1 de `AGENTS.md`). El usuario prueba con UAC.
- **Mutex:** el mutex del helper se adquiere y se libera en el hilo principal de
  `ElevatedDriverHelper.Run`. No moverlo dentro de código `async`: ese fue el bug de la dev.1.
- **Elevación:** el helper se ejecuta como administrador desde la carpeta portable del usuario, que
  es escribible por él. Es inherente a una app portable sin firmar y el usuario debe saberlo.
- **Estados del receptor:** la transición base/activo (`3A08` ↔ `3A09`) ocurre al encender o apagar
  el mando. Las pruebas de drivers deben cubrir los dos estados.
- **Heredocs en Bash:** en este entorno (Git Bash en Windows), los heredocs largos con C# fallan al
  parsear. Escribir los ficheros con la herramienta de escritura o con un script Python en `%TEMP%`.
- **Finales de línea:** `.gitattributes` normaliza a LF. Los avisos «CRLF will be replaced» son
  esperados y no indican un problema.
- **`artifacts/` ocupa ~3,7 GB** de builds antiguas. Está ignorado por git; se puede limpiar
  manualmente si el usuario lo pide.

## 5. Verificación antes de cerrar cualquier tarea

```powershell
dotnet restore ZeroCue.sln --locked-mode
dotnet build ZeroCue.sln -c Release
dotnet test tests/ZeroCue.Tests/ZeroCue.Tests.csproj -c Release --no-build
dotnet run --project tests/ZeroCue.InputChecks/ZeroCue.InputChecks.csproj -c Release
./scripts/verify-runtime-assets.ps1
./scripts/verify-driver-safety.ps1
# Build portable de prueba:
./scripts/publish-portable.ps1 -Version 0.1.0-alpha.19-dev.N
```

Indica siempre qué queda pendiente de validar con hardware: cable V2, receptor V2 en modo base y
activo, y V1 experimental.
