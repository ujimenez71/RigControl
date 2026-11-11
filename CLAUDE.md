# RigControl

Software propio de control del rig de simulación, inspirado en Hangar Control de OverKill Simulations
(https://overkillsimulations.com/hangar-control/), adaptado a mis periféricos concretos.

## Estructura (solución `RigControl.slnx`, repo git en rama `main`)

- `src/RigControl.Core/`: biblioteca con todo lo que no es interfaz: dispositivos (`DeviceScanner`,
  `DeviceCatalog`, `DeviceMonitor`), `BravoLights`, y `Bindings/` (motor, perfiles, salidas teclado
  y SimConnect), `Hangar/` (secuencias, prioridad, ventanas) y `Mods/` (biblioteca de mods con uniones).
  Copia `SimConnect.dll` del SDK a la salida de quien la referencia.
- `src/RigProbe/`: consola de sondeo y pruebas (`Program.cs`, `ConsoleInput.cs`).
  `dotnet run --project src/RigProbe -- <comando>`; exe en `src/RigProbe/bin/Debug/net10.0/RigProbe.exe`.
- `src/RigControl/`: app WPF (`dotnet run --project src/RigControl`). MVVM sin librerías
  (`ViewModels/ObservableObject`), vistas en `Views/`.
- `tests/RigControl.Tests/`: pruebas de escenarios en consola, sin paquetes externos
  (`dotnet run --project tests/RigControl.Tests`; `--hardware` añade la prueba que escribe en el
  Bravo real). Ejes, acciones, repetición, luces, DCS-BIOS (con un DCS falso por UDP local) y Hangar.
  Pasarlas antes de cada commit que toque Core.
- `src/HapticSeat.*` y `tests/HapticSeat.Tests`: programa aparte para el cojín HF8 Pro (documentación
  en `docs/HapticSeat.md`, léela antes de tocarlo). Sin dependencias con RigControl y en su propio
  proceso a propósito: un fallo nativo en RigControl no debe dejar los motores encendidos. Pasar
  también `dotnet test tests/HapticSeat.Tests` antes de cada commit que lo toque.
- `tools/icono/rigcontrol_icon.py`: genera el icono de la app (`src/RigControl/Assets/RigControl.ico`, caza
  sobre el horizonte, elegido por el usuario el 10/11/2026; vista previa grande en `tools/icono/RigControl.png`).
  `python -I tools/icono/rigcontrol_icon.py src/RigControl/Assets` (necesita Pillow).
- `profiles/`: perfiles de ejemplo/fuente; los que se usan van en `%APPDATA%\RigControl\profiles`.
- El runner (`BindingRunner.Run`) recibe un `CancellationToken`: la consola lo cancela con ESC.
- Core NO escribe en la consola: usa `Log` (Info/Warning/Error/Action, `Log.Sink`). RigProbe lo
  imprime (hora delante de las acciones); la app lo encola y lo vuelca cada 50 ms.
- `RunSession` (Core) = un "run" completo (SimConnect, teclado, fuente de perfiles, runner) con
  estado legible desde otro hilo (SimConnected, SimName, Aircraft, Profile). La consola usa
  `Run(token)`; la app `Start()/Stop()` en un hilo propio con su propia instancia de DirectInput.

## Stack

- .NET 10, C#, Windows 11, x64 (`PlatformTarget` x64 en todos: SimConnect.dll es de 64 bits)
- Vortice.DirectInput para entradas; HidSharp para HID (salidas: LEDs, pantallas)
- IDE: Visual Studio Community 2026
- UI: WPF en `net10.0-windows` con el tema Fluent (`ThemeMode="System"` en App.xaml; aviso
  experimental WPF0001 silenciado). Elegido frente a WinUI 3 (01/11/2026): mismo aspecto Windows 11,
  sin Windows App SDK ni empaquetado, diseñador de VS. Usar colores del tema (DynamicResource /
  SystemColors.AccentColorBrushKey), no pinceles clásicos como ControlBrush (no siguen el modo oscuro).

## Periféricos (10 dispositivos, todos con VID:PID único)

| Alias | VID:PID | Uso | Salidas |
|---|---|---|---|
| Moza AB9 + MH16 | 346E:1000 | jet, helo (stick/cíclico) | NO TOCAR: FFB y config de Pit House |
| Throttle F-15EX (Orion Throttle Base II) | 4098:BD64 | jet | output 14 B (protocolo SimAppPro) |
| Pedales WinWing metal | 4098:BEF0 | jet, helo, ga | output 14 B |
| MFD izquierdo (CarrierAce) | 4098:BEE1 | jet | output 14 B |
| MFD derecho (CarrierAce) | 4098:BEE2 | jet | output 14 B |
| PTO 2 (CarrierAce) | 4098:BF05 | jet | output 14 B |
| ICP (ViperAce) | 4098:BF06 | jet | output 64 B, dejar para el final |
| Colectivo AH-64 (VPC Rotor TCS PLUS) | 3344:832E | helo | interfaz mi_01 = config VPC, NO TOCAR |
| Yoke Alpha (Honeycomb) | 294B:1900 | ga | sin LEDs, ignorar su output |
| Bravo (Honeycomb) | 294B:1901 | ga | feature report 5 B = luces anunciadoras |

Simuladores: DCS World (jets y helicópteros) y MSFS 2024 (aviación general). Vuelo en VR.

## Reglas de diseño

- Identidad de dispositivo: clave `VID:PID:serial` (o `VID:PID` sin serial). Nunca `InstanceGuid`.
- Abrir todo en modo **no exclusivo / compartido**. Conviven SimAppPro, Pit House, VPC Configurator
  y SimHub. Nunca enviar force feedback ni tocar firmware.
- DirectInput y HID se emparejan por VID:PID (válido porque todos son únicos).
- Polling ~60 Hz, sin reflexión ni asignaciones innecesarias en el bucle caliente.
- Catálogo en `%APPDATA%\RigControl\devices.json`, editable a mano; los alias y tags se conservan.

## Estado actual

Comandos de RigProbe: `list [all]`, `monitor`, `catalog`, `bravo-test`, `check <perfil>`, `run <perfil>|auto [--dry]`,
`sim [eventos [filtro]]`, `sim vars <perfil> [s]`, `dcs [s]`, `dcs enviar CONTROL [VALOR]`, `dcs leer CONTROL`,
`dcs vigilar [filtro]` (cada cambio de los mandos de la cabina con su posición y descripción: para
comprobar un perfil o saber qué control es cada interruptor; con `run` a la vez),
`hangar` (estado de todos, no lanza nada), `hangar <nombre> --dry` (qué haría), `hangar <nombre>` (abre; Ctrl+C cancela), `ventanas <proceso>` (posición de sus ventanas, para `"window"`),
`mods` (estado de cada biblioteca), `mods <conjunto> [--dry]`, `mods importar <biblioteca> [--dry]`.
- Fase 5, App Hangar (08/11/2026; escenarios con host falso + ping real, pantalla comprobada fuera de
  pantalla; ✅ verificado por el usuario con los programas reales: ningún paso en rojo, MSFS arranca,
  HapticSeat aparece en la bandeja y SimAppPro abre sin aviso de UAC): `Core/Hangar/`
  (`HangarFile`, `HangarRunner`, `IProcessHost`) + pantalla Hangar (`HangarViewModel`, se guarda sola).
  `%APPDATA%\RigControl\hangar.json` (copia de `profiles/hangar.json`), hangares "DCS World" y
  "MSFS 2024": cada paso = nombre, `target` (ruta con %VARIABLES% o URI), `args`, `process`,
  `waitSeconds`, `timeoutSeconds`, `enabled`; `startRun` inicia la ejecución automática al terminar
  (solo en la app, no en RigProbe). Lo ya abierto no se relanza; un fallo no detiene la secuencia; NUNCA cierra
  nada (cancelar solo deja de esperar y de lanzar). JSON roto = se muestra el error y no se sobrescribe.
  MSFS 2024 es de la Store: `shell:AppsFolder\Microsoft.Limitless_8wekyb3d8bbwe!App`, proceso
  `FlightSimulator2024` (arranca vía GameLaunchHelper), ✅ arranque verificado por el usuario (08/11/2026). DCS está en G: (el registro aún dice E:).
  VPC no necesita programa residente. OpenKneeboard: su carpeta de Program Files no tiene .exe, sin incluir.
  HapticSeat va en los dos hangares (copia publicada, ver `docs/HapticSeat.md`), con telemetría de IL-2, MSFS 2024 y DCS (✅ verificado con el AH-64D, efecto de rotor incluido, 09/11/2026).
  SimAppPro pide administrador: el Hangar lanza la tarea programada `RigControl\SimAppPro` (nivel elevado, sin
  disparadores, creada el 08/11/2026) con `schtasks /run` para que no salga el aviso de UAC. Si se reinstala en otra
  ruta, hay que recrear la tarea (crearla requiere elevación).
  Para verificar la interfaz sin robar el foco a DCS (se minimiza y se congela): ventana fuera de pantalla con
  `ShowActivated=false` + `RenderTargetBitmap`, en un proyecto aparte que referencie RigControl.
  Prioridad de CPU (10/11/2026, escenarios + ping real; ✅ verificada por el usuario con DCS y el launcher: queda en Por encima de lo normal): `priority` en
  cada paso (`idle`, `belowNormal`, `normal`, `aboveNormal`, `high`; sin tiempo real a propósito, y números
  rechazados), desplegable "Prioridad de CPU" en la pantalla. Se pone a todos los procesos con el nombre de
  `process` al aparecer, o al pasar por el paso si ya estaba abierto (no relanza). No se puede con los procesos
  elevados (SimAppPro: acceso denegado) ni sin `process`: el paso queda en aviso (en rojo, como los fallos) y la secuencia sigue.
  La prioridad se MANTIENE mientras el programa siga abierto (también acabada la secuencia; cada segundo se
  corrige; la app lo para al cerrarse o al abrir otro hangar, RigProbe espera o Ctrl+C) y hasta 60 s después de
  cerrarse. Motivo (10/11/2026): DCS.exe abre primero un launcher, se relanza (`--restarted`) al pulsar pantalla/VR
  y al arrancar fija él mismo su prioridad (edCore.dll llama a SetPriorityClass; reparte "render/IO cores" él
  solo): ponerla una vez no bastaba. Un cambio a mano también se corrige (para no tocarla: "Sin cambiar").
  Si aparece tras `timeoutSeconds`, también se le pone.
  `hangar X --dry` la muestra.
  Gestor de ventanas (11/11/2026; escenarios con ventanas falsas + una ventana real transparente sin activar
  en las pruebas; ✅ verificado por el usuario con los programas reales el 11/11/2026): `"window"` en cada paso (`state`
  normal/minimized/maximized, `x`/`y` y `width`/`height` por parejas, `topmost` true/false; lo que falta no se toca)
  y `"focus": true` (vale el último paso marcado). `WindowHost` (Core/Hangar, user32, en píxeles reales: DPI por
  monitor en el hilo) detrás de `IWindowHost`. Ventanas principales = visibles, sin dueño, no de herramientas, no
  ocultas por DWM. Se coloca cada ventana NUEVA al verla y ~3 s más (programas que se recolocan solos), nunca
  después (no pelear con lo hecho a mano); se buscan nuevas hasta 120 s tras la secuencia (DCS se relanza). También
  lo ya abierto. Posición fuera de todas las pantallas = no se mueve y aviso; elevados (SimAppPro) = aviso.
  Foco: al acabar la secuencia (o al aparecer su ventana, dentro del plazo) se trae al frente UNA vez la ventana más
  reciente del paso (AttachThreadInput + SetForegroundWindow, sin teclas); cancelar = nada. Pantalla: "Ventana",
  "Siempre encima", "Traer al frente al terminar" (uno por hangar) y "Colocar" con "Usar la posición actual".
  `RigProbe ventanas <proceso>` da las coordenadas para el JSON. El ejemplo del repo marca los simuladores con focus.
  Mods (11/11/2026; escenarios con uniones REALES en una carpeta temporal; ✅ verificado por el usuario con MSFS 2024 y
  la carpeta Community real el 11/11/2026: importar, conjuntos y hangar): `Core/Mods/` (`Junction`, `ModsFile`, `ModManager`) + pantalla Mods + `"modSet"` en
  cada hangar (se aplica antes de abrir nada; un fallo no para la secuencia). `%APPDATA%\RigControl\mods.json`
  (copia de `profiles/mods.json`, copiada el 11/11/2026): bibliotecas = `library` (carpeta con un mod por
  subcarpeta), `target` (donde los busca el simulador), `process` (abierto = no se cambia nada), `always`
  (activos en todo conjunto); conjuntos = nombre + mods por biblioteca (las que no salen no se tocan).
  Activar = UNIÓN de directorio (junction, FSCTL_SET_REPARSE_POINT; no necesita administrador; el modo
  desarrollador está desactivado, así que los symlinks sí lo necesitarían). Reglas: nunca se borra una carpeta de
  verdad, solo enlaces que apuntan a la biblioteca; lo suelto en el destino ("Foreign") no se toca salvo al
  importar (mover a la biblioteca, MISMA unidad = renombrar, y dejar el enlace: sigue activo). Biblioteca en
  `C:\RigControl\Mods\<biblioteca>` (decisión del usuario: misma unidad que Community). Bibliotecas: "MSFS 2024"
  (Community de la Store: `%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\Community`,
  35 mods sueltos ~100 GB el 11/11/2026; Community2024 vacía), "DCS aircraft" y "DCS tech" (Saved Games\DCS.openbeta;
  solo VAICOMPRO, "always"). Liveries de DCS sin incluir (van por avión: una biblioteca por avión si hace falta).
  Conjuntos de ejemplo "Canarias" y "Bush PNG": a revisar por el usuario. El usuario importa desde la app (Claude
  no mueve sus mods).
  Falta en la fase 5: snapshots.
- Fase 4 (en curso, hecho sin el usuario el 01/11/2026; NADA de esto probado aún en vuelo):
  - Luces del Bravo desde MSFS: sección `"lights"` del perfil: nombre de `BravoLed` -> condición o
    lista (AND) sobre `A:VAR[, unidades]` o `L:Var` con `> >= < <= =`, rango `=a..b`, `!` niega;
    sin operador = distinto de 0 (`LightRules.cs`). `SimConnectOutput` pide esas variables con
    PERIOD_SIM_FRAME + FLAG_CHANGED (definiciones desde 10000) y guarda `Values`;
    `BravoLightsOutput` evalúa y escribe solo al cambiar. Si el perfil no tiene luces NO toca el
    Bravo (no pelear con otros programas). Funciona también en simulación (--dry).
    `c172.json` tiene 18 luces (AP, avisos con la batería conectada, tres verdes), ✅ verificadas
    en vuelo con MSFS 2024 (02/11/2026). En este C172 `ELECTRICAL MAIN BUS VOLTAGE` vale siempre 0:
    los avisos usan `ELECTRICAL MASTER BATTERY` y LOW VOLTS = `ELECTRICAL BATTERY LOAD` > 1 A.
    El AP se desconecta solo en tierra (normal). `RigProbe sim vars <perfil> [segundos]` muestra
    cada segundo los valores que llegan de las variables de luces (para diagnosticar).
    MUY IMPORTANTE: MSFS 2024 trae un perfil de controles propio para el Bravo (y el Yoke) que
    también actúa sobre los botones y escribe en sus luces. Si un botón está asignado en los dos,
    las órdenes de tipo interruptor se anulan (el AP se conectaba y desconectaba) y el trim va
    doble. El usuario quitó en MSFS las asignaciones de los botones del AP y del trim (02/11/2026);
    los ejes de las palancas siguen en MSFS. Ante un "se activa y se desactiva", mirar esto primero.
    Pantalla Ejecución: panel "Luces del Bravo" con las 28 luces y sus colores.
  - DCS-BIOS (`DcsBiosOutput`): escucha 239.255.50.10:5010 (multicast compartido) y manda órdenes
    de texto a 127.0.0.1:7778. Acción `D:CONTROL` = pulsador (1 al pulsar, 0 al soltar; toque = 0
    a los 50 ms), `D:CONTROL VALOR` (INC, DEC, TOGGLE, números). Avión en la dirección 0x0000
    (24 car.) -> `"aircraft": ["dcs:f-15e"]` en `run auto`. Probado con un DCS falso por UDP y ✅ verificado con DCS y el AH-64D (02/11/2026): avión detectado
    ("AH-64D_BLK_II"), órdenes D: (ANTICOL RED/OFF/WHT), estado leído, y perfil hecho con el editor
    (botones del colectivo VPC -> D:PLT_EXTL_ACOL_L_SW 2 / 1) activado solo en `run auto`. DCS-BIOS
    manda los cambios al momento pero repite el estado completo poco a poco (~16 s el AH-64D).
    `DcsBiosCatalog` lee las definiciones (sugerencias y descripciones en el editor).
    OJO, carpeta de DCS: DCS se ejecuta como **openbeta** (`G:\Eagle Dynamics\DCS World OpenBeta`,
    `dcs_variant.txt` = openbeta), así que la carpeta ACTIVA es `Saved Games\DCS.openbeta` (su
    dcs.log es el que se actualiza). `Saved Games\DCS` está en desuso (log de 2023). Comprobarlo
    siempre por la fecha de `Logs\dcs.log`.
    Export.lua activo (`DCS.openbeta\Scripts\Export.lua`): carga SimAppPro (`wwt\wwtExport.lua`,
    UDP 16535-16537) y MOZA (`MOZA\MOZA.lua`); los dos encadenan las funciones LuaExport* previas.
    Hooks: SimHub, bHaptics, PassthroughForge, OpenKneeboard. DCS-BIOS v0.11.8 (GitHub
    DCS-Skunkworks, 18/10/2026) va en `Scripts\DCS-BIOS` + la línea
    `dofile(lfs.writedir() .. [[Scripts\DCS-BIOS\BIOS.lua]])` al final (también encadena con pcall).
    Claude NO puede modificar Export.lua ni Scripts de DCS (lo bloquea el sistema de permisos): se
    le da al usuario el comando para que lo ejecute con "!". El 02/11/2026 se instaló primero por
    error en `Saved Games\DCS` (inactiva; copia en export.lua.antes-de-dcs-bios).
    Definiciones de controles: `Scripts\DCS-BIOS\doc\json\<avión>.json`. `RigProbe dcs [s]` escucha
    y muestra el avión; `RigProbe dcs enviar CONTROL [VALOR]` manda una orden.
  - Estado del simulador en condiciones y luces (03/11/2026, probado con escenarios y el DCS falso, y
    ✅ verificado con DCS y el AH-64D el 03/11/2026: b18 alterna ANTICOL OFF <-> WHT, 11 de 11): variables `D:CONTROL` (valor entero del control en la imagen de
    DCS-BIOS: dirección/máscara/desplazamiento de `DcsBiosCatalog`, sin unidades) valen en `"lights"`
    y en la nueva condición `{"state": "D:PLT_EXTL_ACOL_L_SW=1"}` (misma sintaxis que las luces;
    también `A:`/`L:` de MSFS). Se evalúa al pulsar, como el resto. Ejemplo: un botón que alterna
    = binding con `when: {state: ...=1}` + el mismo sin condición. Sin datos = NaN = no se cumple
    (también con `!` dentro del texto; OJO `{"not": {"state"}}` sin datos SÍ se cumple).
    Luces y condiciones comparten `WatchedVars`; `D:` en perfil de MSFS o `A:/L:` en uno de DCS =
    error. `DcsBiosOutput.Prepare/Fill` resuelve los D: con el avión de DCS cargado (o el primer
    `dcs:` del perfil) y avisa de los que no existen; `ActionDispatcher.Values` junta SimConnect +
    DCS-BIOS y el motor lo lee por `IActionSink.Values`. Editor: pieza "Estado del simulador" en
    las condiciones (NO = "!"), luces con sugerencias D: y vista en vivo también con DCS.
    `RigProbe sim vars <perfil>` muestra también los D:.
- Fase 3 (✅ completa). Acciones con prefijo, interpretadas al compilar (`Bindings/ActionSpec.cs`):
  `K:EVENTO[=entero]`, `B:INPUT_EVENT[=valor, 1 por defecto]`, `L:Var=valor`, `key:CTRL+SHIFT+G`;
  sin prefijo = solo se imprime (INFO en Conflict Guard). `ActionDispatcher` las reparte;
  `--dry` no envía nada.
  - Teclado (`KeyboardOutput`), ✅ verificado con el rig en el Bloc de notas (01/11/2026): SendInput
    por scancode, toque de 50 ms soltado en `Tick`, hold = tecla mantenida INICIO→FIN, cuenta de
    referencias por tecla, todo se suelta al salir. Va a la ventana con el foco.
    OJO: las apps en español localizan atajos (Bloc de notas: CTRL+A = Abrir, CTRL+E = Seleccionar
    todo). No es un fallo nuestro.
  - SimConnect (`SimConnectOutput`), ✅ verificado con MSFS 2024 (SunRise 12.2) y el UH-1H
    (01/11/2026): B: (batería, beacon, luz de aterrizaje), K:TOGGLE_NAV_LIGHTS y L: correctos.
    P/Invoke directo a la `SimConnect.dll` nativa del SDK de MSFS 2024 (`C:\MSFS 2024 SDK`, v1.8.1);
    el .csproj la copia a la salida (variable `MSFS2024_SDK` o ruta por defecto) y fuerza x64.
    No usar la DLL managed (es para .NET Framework) ni `D:\MSFS SDK` (SDK de 2020, sin B:/L:).
    Todo en el hilo del bucle: `Tick` conecta/reintenta cada 3 s y vacía `GetNextDispatch`.
    K: se registran al cargar el perfil; si MSFS rechaza el nombre, se avisa y no se vuelven a
    enviar. B: se enumeran por avión (hash) al conectar y al cambiar de avión. Avión cargado vía
    sistema "AircraftLoaded", ruta normalizada desde `simobjects\` (MSFS la da relativa o absoluta).
    `sim` muestra conexión y avión; `sim eventos [filtro]` lista los B: del avión cargado.
  - Cambio automático de perfil (`run auto`, `AutoProfileSource`), ✅ verificado UH-1H → C172
    (01/11/2026): lee todos los .json de `%APPDATA%\RigControl\profiles`; cada perfil declara
    `"aircraft": ["uh1h"]` (trozos de la ruta normalizada del aircraft.cfg, "*" = comodín). Gana el
    primero por nombre de archivo; si ninguno encaja, mandos sin bindings. Guardar cualquier perfil
    recarga la carpeta; un perfil roto mantiene su versión anterior. `run <perfil>` usa
    `FileProfileSource`. Ambos derivan de `ProfileSource` (el runner solo llama a `Poll`).
    Rutas vistas: UH-1H `simobjects\airplanes\microsoft_uh1h\...`, C172
    `simobjects\airplanes\asobo_c172sp_g1000\...` (233 B:).
  - Perfiles en `%APPDATA%\RigControl\profiles` (copias de `profiles/` del repo; editar en el repo
    y copiar): `c172.json` ✅ verificado en vuelo (01/11/2026): interruptores del Yoke → B: eléctricos
    y luces, magnetos `B:ENGINE_MAGNETO_1` OFF=0 R=1 L=2 BOTH=3 START=4, autopiloto del G1000 en el
    Bravo (`B:AS1000_AUTOPILOT_*_PFD`, HDG largo = sync, AP doble = `K:AUTOPILOT_OFF`), trim con la
    rueda del Bravo (b22 → `K:ELEV_TRIM_DN`, b23 → `K:ELEV_TRIM_UP`, un evento por clic).
    `uh1h.json` (B: de batería/luces). `ah64d.json` (DCS, 03/11/2026): colectivo VPC completo
    (réplica del real, puesto del piloto): 61 bindings a `PLT_COLLECTIVE_*`, botones sacados de las
    asignaciones de DCS de abril de 2025 (`Config\Input\AH-64D_BLK_II_PLT\joystick\LEFT VPC Rotor
    TCS PLUS {8D807C20...}.diff.lua`; el archivo activo, `L-VPC ... {8D80CA40...}`, solo tiene ejes,
    así que DCS no asigna botones del colectivo: no hay órdenes dobles. Si el usuario vuelve a
    asignarlos en DCS, chocarán). Interruptores con muelle = press -> posición, release -> 1 (Off);
    pulsadores = hold. (b18 alternaba ANTICOL con `state`, verificado; se quitó porque b18 es el
    Cursor Display del colectivo real.) ✅ Probado en DCS (03/11/2026), muchos leyendo
    el estado de vuelta con `sim vars` y un perfil de capas "state" (las órdenes no se ven en
    cabina en frío): b3-b6, b8-b11, b14-b16, b18, b20, b23, b24, b26-b29, b31-b35, b37, b38,
    b40-b42, b44 (B/S=0, PLRT=2, FCR Scan C=0, estabilizador NU=0/ND=2, foco UD/LR, rueda de
    cola y Missile Advance 1->0; todos vuelven a Off=1), y con `dcs vigilar` b12, b17, b22, b39, b43.
    ✅ COLECTIVO COMPLETO VERIFICADO. FCR Scan S es b22 (las asignaciones viejas de DCS decían b2).
    CHOP funciona con la funda cerrada; el jettison (b35) envía bien, sin comprobar con la funda.
    PENDIENTE (03/11/2026, a seguir el 04/11): botones del colectivo que NO están en el perfil,
    vistos con `monitor` en un repaso completo: b7 (justo tras el hat FCR Mode), b19 (justo tras
    b7), b2 (tras el hat FCR Scan Size), b21 (varias veces, tras el hat Sight Select) y el eje Y
    (recorrido completo; DCS no lo tiene asignado, el colectivo es X). ✅ b7/b2/b21 = pulsar el
    centro de los hats FCR Mode / FCR Scan Size / Sight Select (confirmado el 04/11/2026); el real
    no los tiene: se dejan SIN ASIGNAR (decisión del usuario). Único control del colectivo sin
    botón en DCS-BIOS: PLT_COLLECTIVE_ALT_CURSOR_ENTER -> ✅ b19 (gatillo de la cabeza del
    colectivo), hold, verificado con `dcs vigilar` (04/11/2026). Perfil: 62 bindings. Eje Y = palanca de fricción
    del colectivo (frena la palanca físicamente); DCS no simula la fricción del colectivo del
    Apache (solo "Power Levers Friction" de las palancas de motor), así que se deja sin asignar.
    ✅ Todas las entradas del colectivo identificadas. Ejes en DCS (archivo activo): X = Collective,
    RX/RY = HOCAS Cursor Controller X/Y (la mini-palanca del cursor; va bien, no duplicar). OJO:
    el archivo activo es `LEFT VPC Rotor TCS PLUS {8D80CA40...11f0...}` (DCS lo reescribió el
    04/11/2026; el `L-VPC ...` de diciembre de 2025 es viejo); mismos ejes.
    Cíclico en el Moza MH16 (04/11/2026, ✅ verificado con `dcs vigilar` en DCS): 32 bindings
    `moza:` -> `PLT_CYCLIC_*` (perfil: 94). Mapa del MH16 (5 hats, todo en modo botones de Pit
    House): gatillo b1 primer tope / b6 a fondo (b1 sigue pulsado); b2 botón de arriba del todo;
    b5 lado derecho de la cabeza; hat de arriba b20 U, b21 R, b22 D, b23 L, centro b24 (diagonal =
    dos botones); hat del pulgar b28 U, b29 R, b30 D, b31 L (sin centro; en "Hat switch mode" de
    Pit House pasa a ser Hat 1 POV y desaparecen b28-b31); hat de debajo del pulgar b11 U, b12 R,
    b13 D, b14 L; hat central (debajo de b2) b7 U, b9 D, b8 R, b10 L (igual que el TMS del A-10C
    del usuario; verificado con George); hat de la izquierda b15 U,
    b16 R, b17 D, b18 L, centro b19; basculante b26 arriba / b25 abajo. Asignación: gatillo ->
    TRIGGER Half(1)/Full(2); hat del pulgar = Force Trim/Hold (b28 = R/Up lo tiene DCS asignado
    en `MOZA AB9 FFB Base {03447680...}.diff.lua`: NO duplicar), b30 D, b31 AT, b29 AL; hat de
    arriba = Weapons Action (G/A/R/M), centro = ATA Cage; hat de la izquierda = Symbology (U/D,
    centro = Depress) + RTS (L RADIO / R ICS); b5 = RTS Depress (solo acepta TOGGLE, sin salida:
    no se ve en `dcs vigilar`); b2 Flare; basculante Chaff / FMC Release. George (IA del CPG)
    asignado EN DCS por el usuario (04/11/2026, ✅ funciona; no está en DCS-BIOS y las teclas
    W/A/S/D chocarían): perfil de controles aparte "AH-64D George AI Helper"
    (`Config\Input\AH-64D_BLK_II_AI_Menu`, activo solo con el menú abierto) = hat central
    Up/Down/Left/Right + Hide b11; en el del piloto Show/Hide b11, Multifunctional Input (Center)
    b13, TADS Store Target b14. Libre en el Moza para el Apache: b12 (y b3, b4, que usa el A-10C). OJO: DCS-BIOS tiene al revés las etiquetas de WEAPON_LR (0 dice "M"
    pero en cabina es R = cohetes a la izquierda; 2 = misiles a la derecha), verificado en cabina.
    b1, b13, b25, b30, b36 no salieron con nada.
    Cortes de DCS-BIOS (investigado el 03/11/2026): con DCS a pantalla completa, al cambiar a otra
    ventana DCS se minimiza (dcs.log: "resize main window to [0, 0]") y sigue exportando; al
    VOLVER ("resize ... [3840, 2160]" + "ModelTimeQuantizer: ANTIFREEZE ENABLED") se congela y deja
    de exportar decenas de segundos. Coincide con los "sin datos" de la tarde (vistos a la vez por
    dos procesos). TAMBIÉN el menú de ESC (pausa): ✅ comprobado, corte de 9-11 s mientras se está en el menú. En vuelo normal no debería pasar. Desde entonces
    `DcsBiosOutput`: `IsActive` = datos de < 2 s (avisa "sin datos" una vez y "vuelven los datos
    tras N s"), `HasState` = < 30 s (`StaleMs`): mantiene avión y valores D: en cortes cortos; a los
    30 s descarta todo. Además cada dirección de la imagen vale solo cuando ha llegado (`_seen`):
    antes, al arrancar, los D: leían 0 durante el primer ciclo completo (~16-40 s) y no NaN. Las
    órdenes D: se envían igual durante un corte (DCS-BIOS las aplica al volver; sin comprobar).
    ✅ Verificado con DCS (03/11/2026): con ESC 11 s, los valores D: se mantienen todo el corte.
    MFD WinWing en el Apache: se quedan asignados en DCS (decisión del usuario, 03/11/2026; no
    duplicarlos en RigControl). Curioso: R4 da b48 en `monitor` en los DOS MFD, pero las
    asignaciones de DCS dicen R4 = b27 (izq.) / b26 (der.) y no tienen b48, y aun así R4 funciona
    en DCS. Hipótesis sin comprobar: SimAppPro hace la acción por su Export (wwtExport.lua), o la
    numeración de DCS no es la de DirectInput en estos MFD. `ejemplo-ga.json` = demo de funciones, sin "aircraft".
  - `"repeatMs"` en un hold con K:/B:/L: lo reenvía mientras se mantiene (`ActionDispatcher`, sin
    asignaciones). Probado con un simulador falso; ya no lo usa ningún perfil (el trim pasó a la rueda).
  - En pruebas que envían teclas, comprobar SIEMPRE que la ventana destino tiene el foco antes de enviar.
- Motor de bindings (fase 2, ✅ completa) en `Bindings/`: perfiles JSON en `%APPDATA%\RigControl\profiles`
  (ejemplo en `profiles/ejemplo-ga.json`), gestos press/release/short/long/hold/double/sequence,
  condiciones all/any/not + capas, Conflict Guard. `BindingEngine` es puro (tiempo inyectado) y
  probado con escenarios simulados. Las salidas reales (teclado, SimConnect) son la fase 3.
  Condiciones evaluadas al pulsar; los bindings condicionados que se cumplen tienen prioridad
  sobre los que no llevan condición. `run` verificado con el Bravo real (28/09/2026): short, long,
  double, secuencia y tren correctos.
- Recarga en caliente (01/11/2026, ✅ verificada con el rig): `run` vigila el JSON
  (FileSystemWatcher, espera 300 ms tras el último aviso) y lo recarga con `ProfileLoader.LoadAndCheck`
  (lo mismo que `check`). Si el nuevo tiene errores o duplicados, se mantiene el anterior. Al
  cambiar: `BindingEngine.EndHolds` cierra los hold activos, el estado actual se toma como base sin
  disparar nada, y `BindingRunner.Attach` reutiliza/abre/cierra dispositivos según el perfil nuevo
  (base para el cambio automático de perfil de la fase 3). Fase 2 completa.
- Zonas de eje (01/11/2026): `alias:z>90`, `alias:z<10`, `alias:z=40..60` (en %, ejes x y z rx ry rz
  s0 s1). Son entradas virtuales "pulsadas" mientras el eje está dentro, así que valen todos los
  gestos y las condiciones sin tocar `BindingEngine`. Histéresis (`axisHysteresis`, 2 % por defecto)
  en `AxisZone`: entrar exige meterse el margen dentro, salir basta con cruzar el borde (zonas
  contiguas nunca se solapan; los extremos 0/100 no llevan margen). Probado con escenarios
  simulados y ✅ verificado con el Bravo real (01/11/2026): `y>95`, `y<20` condicionado al tren
  arriba y b24, correctos y sin repeticiones. `monitor` muestra el % de cada eje.
- Para que el usuario vea la salida de `run`/`monitor` en directo, lanzarlo en una ventana propia
  (`Start-Process .\src\RigProbe\bin\Debug\net10.0\RigProbe.exe -ArgumentList run,<perfil>`); en
  segundo plano solo la ve Claude. Para la app WPF se puede verificar el aspecto con una captura de
  la ventana (GetWindowRect + CopyFromScreen) y leer la imagen.
- App WPF, pantalla Monitor ✅ (01/11/2026): lista de dispositivos con punto de actividad, detalle
  con ejes (barra + % + valor bruto, solo los que existen según `GetObjects`), hats con flecha y
  botones que se iluminan; registro de cambios (pausable). Lee a ~60 Hz con un DispatcherTimer en
  el hilo de la interfaz usando `DeviceMonitor` (Core); se para mientras la pantalla está oculta.
- App WPF, pantalla Ejecución (01/11/2026): automático o perfil fijo, casilla Simulación (= --dry),
  Iniciar/Detener; tarjetas Simulador / Avión / Perfil activo; registro coloreado por nivel.
  Probado con UI Automation (simulación) y ✅ verificada por el usuario con MSFS y el rig
  (01/11/2026). Al cerrar la ventana se detiene la ejecución.
- App WPF, pantalla Perfiles (02/11/2026, solo lectura): lista de la carpeta con punto verde/rojo,
  pestañas Bindings (con buscador), Luces, Dispositivos (catálogo / conectado) y Conflict Guard;
  se actualiza al guardar. `ProfileInspector` (Core) = "check" sin log. Acciones descritas en
  español por `ActionDescriber` (o `"desc"` en el binding, que manda). Entradas con nombre
  desde `devices.json` → `"Inputs": { "b13": "ALT ON", "hat1": "...", "y": "Palanca 1" }` por
  dispositivo (`CatalogEntry.InputLabel`); el Yoke y el Bravo traen los de fábrica
  (`DeviceCatalog.DefaultInputs`, se rellenan al cargar si faltan). Nuevo perfil / Editar / Borrar.
- Editor gráfico de perfiles (02/11/2026, `ProfileEditorViewModel` + `BindingEditViewModel`,
  vista `ProfileEditorView`): nombre, sim, aviones ("Usar el avión actual" de MSFS o DCS), notas;
  bindings añadir/duplicar/borrar; entrada con "Capturar" (`InputCapture` en Core: primer
  botón/hat/eje que cambia tras 1 s; añade el dispositivo al perfil con alias corto si no estaba);
  gesto; acción por partes (destino + nombre + valor), con sugerencias de B: del avión cargado
  (SimConnect propio del editor) y K: frecuentes (`ActionDescriber.CommonSimEvents`), tecla
  capturada pulsándola; "desc"; repeatMs en hold. Valida en vivo (`ProfileInspector.Inspect` sobre
  el perfil en memoria). Guarda con `ProfileFile.Save` (.bak, temporal + renombrar, un binding por
  línea); los // comentarios se pierden: avisa antes de editar un perfil con comentarios.
  2.ª versión (02/11/2026): pestañas Bindings / Capas / Luces del Bravo.
  Condiciones (`ConditionEditViewModel`): ninguna, "todas" o "alguna" de piezas "se mantiene la
  entrada" (con captura) / "está activa la capa", cada una negable; una condición anidada más
  compleja se conserva tal cual (solo se puede quitar). Capas: crear, renombrar (se propaga a las
  condiciones), borrar (avisa si se usa). Luces (`LightEditViewModel`): luz + condiciones por
  partes (variable con sugerencias, unidades, operador, valor/rango, NO); texto libre si el del
  JSON no encaja; vista en vivo con MSFS (el editor compila solo las luces y llama a
  `SimConnectOutput.Prepare`: valor actual de cada condición y si la luz se encendería).
  Verificado: c172 y ejemplo-ga pasan por el editor y se guardan idénticos (bindings, capas,
  luces). Sin editar aún: secuencias (entradas), timing, axisHysteresis.
  `"notes"` en el perfil = notas libres (se ven en Perfiles). Lista agrupada por simulador (MSFS 2024 / DCS World / General).
- `"sim"` en el perfil (02/11/2026): "msfs", "dcs" o "general"; si falta se deduce de "aircraft"
  (`ProfileCompiler.ResolveSim`; mezclar aviones de los dos = error). En `run auto` solo se eligen
  perfiles del simulador del avión actual o generales (un "*" de MSFS no se activa en DCS).
  c172, uh1h y ejemplo-ga llevan "sim": "msfs".
- Bravo (verificado con `monitor`): HDG b1, NAV b2, APR b3, REV b4, ALT b5, VS b6, IAS b7, AP b8,
  tren arriba b31, tren abajo b32.
- Palancas del Bravo, de izquierda a derecha (verificado con `monitor` el 01/11/2026):
  1 = eje Y, 2 = X, 3 = RZ, 4 = RY, 5 = RX, 6 = Z. Abajo (hacia el piloto) = 0 %, arriba = 100 %.
  Al pasar del tope de abajo cada una da un botón mientras está ahí (¿reversa/corte?):
  1 b24, 2 b25, 3 b26, 4 b27, 5 b28, 6 b33 (ojo: la 6 no es b29). La 4 (RY) solo llegó a 0,9–98,3 %:
  no usar zonas más allá del 1 % / 98 % en ella. Rueda de trim ✅: un pulso de ~16 ms por clic,
  b22 = morro abajo, b23 = morro arriba. b21 y b34–b47 cambiaron de golpe una vez sin tocar nada a
  propósito (sin identificar).
- Yoke Alpha, ✅ mapeo COMPLETO (verificado con `monitor` el 01/11/2026):
  - Empuñadura izquierda, ✅ verificada el 01/11/2026: dos botones blancos sin etiqueta, b1 (el
    primero yendo de arriba abajo y de izquierda a derecha) y b2; hat1 de 4 direcciones (0/90/180/270°).
  - Dos basculantes verticales con retorno, también en la empuñadura izquierda: izquierdo
    b6 (arriba) / b5 (abajo); derecho b8 (arriba) / b7 (abajo). (El "b7 repetido" del 28/09 era
    el basculante derecho movido dos veces hacia abajo.)
  - Empuñadura derecha, ✅ verificada el 01/11/2026: dos botones sin etiqueta, b3 (el primero
    pulsado yendo de arriba abajo y de izquierda a derecha) y b4. Dos basculantes horizontales con
    retorno: izquierdo b9 (izquierda) / b10 (derecha); derecho b11 (izquierda) / b12 (derecha).
  - Interruptores de dos posiciones, ✅ verificados el 01/11/2026 (un botón por posición, siempre
    uno activo; arriba = ON = impar): ALT b13/b14, BAT b15/b16, BUS1 b17/b18, BUS2 b19/b20,
    BCN b21/b22, LAND b23/b24, TAXI b25/b26, NAV b27/b28, STROBE b29/b30 (ON/OFF).
  - Llave de magnetos, ✅ verificada el 01/11/2026: b31 OFF, b32 R, b33 L, b34 BOTH, b35 START
    (vuelve a BOTH por muelle). Al girar rápido hay ~30 ms entre posiciones sin ningún botón activo.
  - En `profiles/ejemplo-ga.json`, shift = yoke:b5 (basculante izquierdo abajo): mejor moverlo a
    un botón (b1–b4) si se va a mantener pulsado como modificador.
- DirectInput devuelve todo a cero en las primeras lecturas tras abrir; el estado real llega a los
  ~500 ms. `monitor` y `run` ignoran el primer segundo (`BindingRunner.WarmUpMs`).
- `monitor` y `run` funcionan sin consola interactiva (se pueden lanzar en segundo plano y leer su
  salida); en ese caso no se sale con ESC, hay que detener el proceso.
- `BravoLights.cs`: mapeo de bits de la comunidad, ✅ verificado contra las luces físicas con
  `bravo-test` (28/09/2026): las 28 luces coinciden con su nombre.

## Plan

1. ✅ Detección y visor de entradas; catálogo de dispositivos
2. ✅ Motor de bindings: pulsación corta, larga, mantenida, soltar, doble toque y secuencia;
   lógica condicional AND/OR (capas/modificadores); perfiles JSON que referencian claves de
   dispositivo; Conflict Guard (detectar bindings duplicados)
3. ✅ Salidas: SimConnect para MSFS 2024 (eventos K:, L:, A:, B:), detección del avión y cambio
   automático de perfil; teclado con SendInput como respaldo; hold con repetición (`repeatMs`).
   Falta: escribir A: (simvars).
4. (en curso) DCS por UDP (Export.lua / DCS-BIOS), bidireccional; feedback a LEDs WinWing (requiere
   capturar el protocolo con Wireshark + USBPcap) y al Bravo. Hecho: luces del Bravo desde MSFS
   y cliente DCS-BIOS, ✅ verificado con DCS y el AH-64D (ver "Fase 4" arriba); luces y
   condiciones desde el estado de DCS (`D:`, ✅ verificado con el AH-64D). Falta: LEDs WinWing.
5. (en curso) ✅ App Hangar (lanzar apps en secuencia); prioridad de CPU por paso;
   ✅ gestor de ventanas; ✅ mods con uniones de directorio; snapshots

Idea pendiente (propuesta el 02/11/2026, a decidir con el usuario): **generar perfiles con IA**
(Claude por la API de Anthropic). Botón "Generar con IA" en Perfiles: se envían los mandos con sus
nombres de entrada (devices.json `Inputs`), los controles del avión (B: enumerados por SimConnect o
`DcsBiosCatalog` con descripciones/posiciones) y las preferencias escritas por el usuario; se pide
un ProfileFile en JSON, se valida con ProfileCompiler + ConflictGuard (devolviendo los errores para
que los corrija) y se abre como borrador en el editor (nunca se guarda solo). Necesita clave de API
(guardarla cifrada, DPAPI). Antes de escribir código con la API, cargar la skill claude-api.
Alternativa sin integración: pedírselo a Claude Code en la sesión.

Otros pendientes (02/11/2026): (el Bravo NO se usará con DCS, decisión del usuario del 04/11/2026:
no hace falta probar luces `D:` en él; la función sigue disponible); renombrar archivo de perfil en
el editor; editar secuencias/timing/histéresis; escribir A:; perfil de jet (el usuario NO tiene el F-15E aunque su carpeta está en `Mods\aircraft`); limpiar la instalación
errónea de DCS-BIOS en `Saved Games\DCS` (inactiva); comprobar que el Yoke y el Bravo vuelven a
detectarse (no aparecían en Windows el 02/11/2026 ni el 03/11/2026).

## Convenciones

- Comentarios y mensajes de consola en español
- Hablar conmigo en español
