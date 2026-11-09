# HapticSeat

App propia de efectos hápticos para el cojín Next Level Racing HF8 Pro a partir de la telemetría
de DCS World, MSFS 2024, IL-2 Sturmovik (Great Battles) e IL-2 Korea. Nace porque HFS (app oficial)
y SimHub se quedan cortos en simulación de vuelo. Vive en el repo de RigControl (desde el 08/11/2026) como
programa aparte: comparten solución, no proceso ni código, y RigControl lo abre desde el Hangar.
La carpeta antigua `source\repos\HapticSeat` se borró el 08/11/2026.

## Stack

- .NET 10 (`net10.0-windows`, x64), C#, Windows 11. System.IO.Ports para el HF8
- IDE: Visual Studio Community 2026. Solución `RigControl.slnx`
- App: WPF con tema Fluent oscuro (`ThemeMode="Dark"`, aviso WPF0001 silenciado), CommunityToolkit.Mvvm,
  NotifyIcon de WinForms solo para la bandeja. Ejecutable `HapticSeatApp.exe`
- Consola: `dotnet run --project src/HapticSeat.Cli -- <comando>` (ejecutable `HapticSeat.exe`)
- Pruebas: `dotnet test tests/HapticSeat.Tests` (xUnit; no tocan el cojín real)
- El Hangar abre la copia publicada, `%LOCALAPPDATA%\RigControl\apps\HapticSeat\HapticSeatApp.exe`. Tras cambiar
  el código, con la app cerrada: `dotnet publish src/HapticSeat.App -c Release -o "$env:LOCALAPPDATA\RigControl\apps\HapticSeat"`

## HF8 Pro (verificado el 20/10/2026)

- USB serie CDC "ForceFeel Pad", `VID_04D8&PID_EE96`, hoy en COM4 (se detecta por registro, no fijar el COM).
  Lo fabrica ForceFeel/Realteus para NLR. No hay SDK oficial; protocolo de la nota pública de
  Buttplug.io (`nextlevelracing.rs`), comprobado contra el cojín real.
- 115200 baudios, 8 bits, sin paridad, 1,5 bits de parada, DTR activo, fin de línea `\r`.
- `P\r` → `E<0-255>,A<0|1>\r`: escala del dial rojo y modo Audio. El nuestro respondió `E255,A0`.
- `M<motor><intensidad>\r`, intensidad 0-255 en decimal sin ceros a la izquierda (`M3128\r`).
  No hace falta keepalive (en las pruebas un motor siguió 1 s sin refrescos): la seguridad la pone nuestro watchdog.
- Mapa de motores confirmado sentado: M0/M1 muslo der/izq, M2/M3 glúteo der/izq,
  M4/M5 espalda der/izq, M6/M7 hombro der/izq.
- Modo PC = LED del mando fijo. Mantener el dial rojo 10 s alterna PC/Audio.
- **HFS abre el puerto en exclusiva**: tiene que estar cerrado. SimHub no molesta si no usa el HF8.
  HFS sigue en la clave Run de Windows pero deshabilitado en las aplicaciones de arranque (desde el 29/08/2026).

## Reglas de diseño

- Los motores **nunca** pueden quedarse encendidos: watchdog de 250 ms en `Hf8Device` (si nadie llama
  a `SetMotors`, todo a 0), `Dispose` siempre apaga, Ctrl+C sale por el camino normal. La app apaga también
  ante excepciones no controladas y en ProcessExit. Lo único que no se cubre es matar el proceso a la fuerza.
- Una sola instancia de la app (mutex `Local\HapticSeat.App`): dos se pelearían por el puerto.
- Hilo de salida a ~100 Hz que solo envía los motores que cambian (más un refresco completo cada 500 ms),
  sin asignaciones en el bucle caliente.
- Convivir con HFS/SimHub/WinWing/MOZA en la telemetría: no robar puertos (IL-2 emite en broadcast,
  DCS se encadena en `Export.lua` sin romper los `dofile` existentes).

## Arquitectura

```
src/HapticSeat.Core   biblioteca: Hf8/, Telemetry/, Effects/, Profiles/, HapticHost
src/HapticSeat.Cli    consola: Program (comandos), EffectRunner (bucle con línea de estado)
src/HapticSeat.App    WPF: App (arranque, bandeja), MainWindow, ViewModels/, Dialogs, TrayIcon
tests/HapticSeat.Tests
```

- `Hf8/`: driver del cojín. `TryOpen` distingue no encontrado / ocupado (`Hf8BusyException`); `Faulted` tras
  un error de escritura (desenchufado) para que el host lo reabra.
- `Telemetry/`: `FlightState` (estado común, ejes del avión X adelante+, Y arriba+, Z derecha+),
  `FlightEvent` (disparos, impactos, daño, bombas, cohetes) e `ITelemetrySource`. Un lector por simulador.
- `Effects/`: efectos independientes del simulador (`IEffect`, puros, dt inyectado). `EffectEngine` los mezcla
  por motor (manda el mayor, el resto suma a la mitad) y `ToLevels` aplica el nivel mínimo de arranque.
  `Zones.Spatial` reparte por posición: proa → muslos, centro → glúteos, popa → espalda/hombros, cada lado al suyo.
  Cada efecto tiene `Id` estable (clave en los perfiles: engine, buffet, ground, gearflaps, airbrake, gforce,
  guns, hits, ordnance; **no cambiarlos**). `EffectEngine` admite desactivar efectos y `Solo` (botón Probar).
  `EffectDemo`: vuelo simulado de 2,5 s por efecto para el botón Probar.
- `HapticHost`: el bucle a 100 Hz de la app en su hilo. Reconecta el HF8 cada 2 s, recibe órdenes de la interfaz
  por una cola (perfil, mínimos, probar, rampa de calibración, pulso de mínimo) y publica un `HostSnapshot`.
  Al empezar a volar cancela pruebas y calibraciones. Si no puede abrir el UDP sigue funcionando (`SourceError`).
- `Profiles/`: `Profile` (ganancia general, efectos activos/ganancia en %, patrones de avión con * y ?),
  `AppSettings` (mínimos por motor, puerto IL-2, perfil automático, arrancar minimizado), `ProfileStore`
  (JSON en `%APPDATA%\HapticSeat`: `settings.json` + `profiles\*.json`, admiten comentarios; escritura atómica).
  El perfil `General` es el de reserva para aviones sin perfil propio y no se borra ni renombra.
- App: `MainViewModel` sondea el host a 30 Hz; elige el perfil al cambiar de avión si está activado
  «Cambiar según el avión»; guarda 600 ms después de la última edición. Pestañas Vuelo (estado, cojín en vivo,
  datos, efectos del perfil) y Calibración (mínimo por motor con «Medir» → «¡Lo noto!», que fija el motor y
  su pareja del otro lado descontando 0,3 s de reacción).
- `EffectRunner` (consola): bucle a 100 Hz con línea de estado y barras por motor; usa los mínimos de `settings.json`.

## Telemetría

| Sim | Fuente | Estado |
|---|---|---|
| IL-2 GB | UDP 5678 a 127.255.255.255 (`telemetrydevice` + `motiondevice` en `data\startup.cfg`) | Decodificador hecho; falta probar en vuelo |
| IL-2 Korea | `E:\IL2Series\game\data\startup.cfg` | Bloques añadidos el 20/10/2026 (copia en `startup.cfg.antes-hapticseat`); falta ver si emite |
| DCS | `dcs/HapticSeatExport.lua` → UDP 5679 (`Telemetry/Dcs/`). OJO: la carpeta activa es `Saved Games\DCS.openbeta\Scripts`, no `Saved Games\DCS` | ✅ Datos verificados volando el AH-64D sin cojín (09/11/2026); faltan armas, pausa y probarlo con el cojín |
| MSFS 2024 | SimConnect (`Telemetry/Msfs/`) | ✅ Verificado volando el C172 con el cojín puesto (08/11/2026): el usuario notó todos los efectos. Sin ajustar intensidades ni probar una toma suave |

La app escucha MSFS, DCS e IL-2 a la vez (`MultiSource`: gana el que está volando).

### DCS

- El script se carga al final de `Export.lua` con `pcall(dofile, ...)`, encadena las `LuaExport*` previas y no deja salir errores. Claude no
  puede modificar esa carpeta: el usuario copia el script y añade la línea con un comando (si cambia el script, volver a copiarlo).
- API oficial en `G:\Eagle Dynamics\DCS World OpenBeta\Scripts\Export.lua`. Se usa solo lo documentado ahí: `LoGetIndicatedAirSpeed`,
  `LoGetAltitudeAboveGroundLevel`, `LoGetAccelerationUnits`, `LoGetMechInfo`, `LoGetEngineInfo`, `LoGetPayloadInfo`, `LoGetMCPState`.
  Armamento y averías a 10 Hz para no gastar en cada fotograma.
- Pausa = deja de avanzar `LoGetModelTime` o de llegar paquetes. Sin permiso de exportación del servidor no se envía nada.
- `DcsDecoder` deduce disparos, bombas y cohetes de lo que baja el armamento (sin distinguir calibre; misiles = cohetes) e impactos de que
  aumente el número de averías de `LoGetMCPState`.
- **Vuelo con el AH-64D (09/11/2026, `dcs --sin-cojin`):** avión, IAS, AGL, G y RPM coherentes (RPM en %: 87 en tierra, 93-96 en
  vuelo). `gear.main.left/right.rod` = 0,5-0,7 en tierra y 0 en el aire; `nose` siempre 0 (la rueda de cola no sale en la API).
  Dos fallos corregidos: (1) el golpe de contacto exigía IAS > 15 m/s y el helicóptero toma tierra casi parado: ahora exige que
  esa rueda lleve ≥ 0,5 s sin carga; (2) posado con el rotor girando la IAS marca ~8 km/h por el aire del rotor y la rodadura
  vibraba parado: el script envía la velocidad horizontal sobre el suelo (`LoGetVectorVelocity`, campo 20, opcional) y la
  rodadura usa `FlightState.RollingSpeedMs` (sobre el suelo o, si el simulador no la da, la IAS).
  2.º vuelo con el cojín (09/11/2026): ✅ cañón (`Cannon.shells` baja, ~10 disparos/s), ✅ cohetes (uno por cohete), ✅ pausa (ESC:
  "En pausa" y a los ~2,5 s DCS deja de enviar; motores a cero) y ✅ golpe de toma de contacto (4,1 G, 2,2 G y 2,7 G). El usuario:
  cañón y cohetes se notan bien; el motor, exagerado en tierra y en vuelo (en helicópteros de turbina reales apenas se nota).
  Falta comprobar si `LoGetMCPState` da averías.
- **Rotor (09/11/2026):** el script envía el campo 21 = helicóptero (`LoGetSelfData().Type.level2 == 2`). DCS no
  da las RPM del rotor: `RotorSpeed` se deduce de las turbinas (40 % = parado, 80 % = a régimen; el Apache marca 87 % posado).
  `RotorEffect` vibra a la frecuencia de paso de palas (`Telemetry/Rotors.cs`, por modelo: AH-64 19,3 Hz, UH-1 10,8 Hz...; 15 Hz si
  no se conoce): base suave + sustentación traslacional (pico a 36 km/h IAS) + alta velocidad (desde 234 km/h) + G. En
  helicópteros `EngineEffect` baja al 12 %. ✅ Volado con el AH-64D y el cojín (09/11/2026): rotor detectado (100 %), el usuario
  da por buenos el rotor y el resto de efectos; el motor al 25 % aún se notaba demasiado y se bajó al 12 % (sin volar aún así).

### MSFS 2024

- `MsfsVars` (puro) convierte las variables al estado común; `MsfsSource` lleva la conexión en su hilo, reintenta cada 3 s y
  detecta una caída con un ping cada 5 s. `SimConnect.dll` se copia desde el SDK (`HapticSeat.Core.csproj`).
- Pausa = evento `Pause`, `Sim` a 0 o más de 500 ms sin datos (MSFS no envía datos parado).
- Sin datos en MSFS, y por tanto supuestos: posición de motores y ruedas (avión típico) y buffet, deducido de
  `INCIDENCE ALPHA` frente a `STALL ALPHA` y de `STALL WARNING`. Turbinas: `TURB ENG N1` como proporción.
- Visto con el C172 (08/11/2026): MSFS acepta todas las variables (si rechaza alguna sale un aviso «excepción» en la consola; en
  la app no se ven los avisos). Nada más conectar, el estado "Sim" no da un vuelo aunque lo haya: se insiste cada segundo.
  En vuelo normal (101-228 km/h) el buffet es 0, sin falsos positivos. La pantalla de choque cuenta como pausa.
- En el C172 `CONTACT POINT COMPRESSION` vale siempre 0, también rodando: siempre entra el respaldo de `SIM ON GROUND`, así que
  las tres ruedas tocan a la vez y el golpe no distingue morro, izquierda o derecha. Sin probar en otros aviones.
- Buffet: entra con el aviso de pérdida y se mantuvo durante una pérdida con caída (80-110 km/h). También saltó a 141 km/h en un
  tirón brusco a 1,4 G, unos 50 km/h por encima de la pérdida en vuelo normal: vigilar si molesta.
- Durante el vuelo con el cojín hubo una pausa larga con los motores a 0; supuesto ESC, sin confirmar. Sin probar: menú principal
  de MSFS 2024 y una toma suave (las dos pasadas acabaron en choque). Sin armas: no hay eventos.
- OJO: SimConnect es código nativo en el mismo proceso que el HF8. Si lo tumbara, el watchdog no actuaría; sin
  comprobar qué hace el cojín si deja de recibir órdenes más allá de 1 s.

### Formato IL-2 (sin documentación oficial; IDs de la ingeniería inversa de IL2Shaker, GPL: no copiar código)

- Movimiento `0x494C0100`: id, tick, yaw/pitch/roll, giros X/Y/Z, aceleraciones X/Y/Z (float LE).
- Estado `0x54000101`: id u32, tamaño u16, tick u32, n u8 × [id u16, nFloats u8, floats], n u8 × [tipo u16, nBytes u8, bytes].
  Sin indicadores = pausa/menús.
- Indicadores: 0 RPM, 1 presión admisión, 4 posición tren, 5 carga ruedas, 6 IAS m/s, 8 acel (long, vert, lat),
  9 buffet (frec, amp), 10 AGL, 11 flaps, 12 aerofreno. 2, 3 y 7 sin identificar.
- Eventos: 0 nombre del avión, 1 datos motor (pos, RPM máx), 2 datos arma (pos, masa, velocidad), 3 datos rueda (pos),
  4 bomba, 5 cohete, 7 impacto (pos, fuerza), 8 daño, 9 disparo (índice de arma), 6/13/14 sin uso.
- **Sin verificar en vuelo** (ajustar con el monitor): escala de `BuffetAmplitude`, escala de la fuerza de impacto,
  si la aceleración vertical incluye la gravedad (se asume 1 G en vuelo nivelado), si el índice de rueda de los
  indicadores 4/5 coincide con el del evento 3.

## Estado actual

App WPF funcionando (20/10/2026): probada con telemetría simulada (perfil automático por avión, datos en vivo,
cojín en vivo). Falta probarla volando y los botones Probar/Medir con el usuario sentado.
Comandos de consola: `estado`, `test [pct]`, `motor <0-7|todos> <pct> [ms]`, `umbral [motor]`,
`il2|msfs|dcs [--sin-cojin] [--ganancia %] [--minimo 0-255] [--puerto N]` (la consola no usa perfiles, solo los mínimos).
Efectos: motor, rotor (helicópteros), buffet, suelo (rodadura + toma de contacto), tren/flaps, aerofreno, carga G, armas, impactos,
bombas/cohetes. Probado con paquetes sintéticos de extremo a extremo (20/10/2026); falta volar.
Nivel mínimo por motor (`Hf8Motors.DefaultMinLevels`), medido con `umbral` sentado y el dial al máximo
(20/10/2026): glúteos 20, espalda 25, hombros 30; muslos sin medir (20, como glúteos). `--minimo N` fuerza uno común.

## Plan

1. ✅ Driver del HF8 con watchdog y pruebas de motores
2. ✅ Lector IL-2 (UDP), modelo de telemetría común y motor de efectos. Pendiente: calibrar en vuelo
3. ✅ Perfiles JSON por avión y app WPF (visor en vivo, efectos, calibración guiada, bandeja)
4. ✅ MSFS 2024 (SimConnect). ✅ DCS (Export.lua por UDP), verificado con el AH-64D. ✅ Efecto de rotor (volado con el AH-64D)
5. Pendientes menores: ajustes avanzados por efecto (escalas de buffet/impactos tras calibrar en vuelo),
   arranque con Windows, cambiar el puerto UDP desde la interfaz (hoy solo en settings.json, requiere reiniciar)

## Convenciones

- Comentarios, mensajes de consola e interfaz en español
- Hablar conmigo en español
