# RigControl

Software propio de control del rig de simulación, inspirado en Hangar Control de OverKill Simulations
(https://overkillsimulations.com/hangar-control/), adaptado a mis periféricos concretos.

## Estructura (solución `RigControl.slnx`, repo git en rama `main`)

- `src/RigControl.Core/`: biblioteca con todo lo que no es interfaz: dispositivos (`DeviceScanner`,
  `DeviceCatalog`, `DeviceMonitor`), `BravoLights`, y `Bindings/` (motor, perfiles, salidas teclado
  y SimConnect). Copia `SimConnect.dll` del SDK a la salida de quien la referencia.
- `src/RigProbe/`: consola de sondeo y pruebas (`Program.cs`, `ConsoleInput.cs`).
  `dotnet run --project src/RigProbe -- <comando>`; exe en `src/RigProbe/bin/Debug/net10.0/RigProbe.exe`.
- `src/RigControl/`: app WPF (`dotnet run --project src/RigControl`). MVVM sin librerías
  (`ViewModels/ObservableObject`), vistas en `Views/`.
- `tests/RigControl.Tests/`: pruebas de escenarios en consola, sin paquetes externos
  (`dotnet run --project tests/RigControl.Tests`; `--hardware` añade la prueba que escribe en el
  Bravo real). Ejes, acciones, repetición, luces y DCS-BIOS (con un DCS falso por UDP local).
  Pasarlas antes de cada commit que toque Core.
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
`sim [eventos [filtro]]`, `sim vars <perfil> [s]`, `dcs [s]`, `dcs enviar CONTROL [VALOR]`.
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
    `uh1h.json` (B: de batería/luces). `ah64d.json` (DCS, hecho con el editor: colectivo b18 alterna ANTICOL OFF <-> WHT con `state` (✅ 03/11/2026), b20 -> OFF). `ejemplo-ga.json` = demo de funciones, sin "aircraft".
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
5. App Hangar (lanzar apps en secuencia), gestor de ventanas, mods por symlinks, snapshots,
   prioridad de CPU

Idea pendiente (propuesta el 02/11/2026, a decidir con el usuario): **generar perfiles con IA**
(Claude por la API de Anthropic). Botón "Generar con IA" en Perfiles: se envían los mandos con sus
nombres de entrada (devices.json `Inputs`), los controles del avión (B: enumerados por SimConnect o
`DcsBiosCatalog` con descripciones/posiciones) y las preferencias escritas por el usuario; se pide
un ProfileFile en JSON, se valida con ProfileCompiler + ConflictGuard (devolviendo los errores para
que los corrija) y se abre como borrador en el editor (nunca se guarda solo). Necesita clave de API
(guardarla cifrada, DPAPI). Antes de escribir código con la API, cargar la skill claude-api.
Alternativa sin integración: pedírselo a Claude Code en la sesión.

Otros pendientes (02/11/2026): probar en DCS las luces `D:` del Bravo (cuando vuelva a detectarse); renombrar archivo de perfil en
el editor; editar secuencias/timing/histéresis; escribir A:; perfil F-15E; limpiar la instalación
errónea de DCS-BIOS en `Saved Games\DCS` (inactiva); comprobar que el Yoke y el Bravo vuelven a
detectarse (no aparecían en Windows el 02/11/2026 ni el 03/11/2026).

## Convenciones

- Comentarios y mensajes de consola en español
- Hablar conmigo en español
