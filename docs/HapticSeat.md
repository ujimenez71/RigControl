# HapticSeat

App propia de efectos hápticos para el cojín Next Level Racing HF8 Pro a partir de la telemetría
de DCS World, MSFS 2024, IL-2 Sturmovik (Great Battles) e IL-2 Korea. Nace porque HFS (app oficial)
y SimHub se quedan cortos en simulación de vuelo. Vive en el repo de RigControl (desde el 08/11/2026) como
programa aparte: comparten solución, no proceso ni código, y RigControl lo abre desde el Hangar.
La copia antigua en `C:\Users\ujime\source\repos\HapticSeat` ya no es la buena.

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
| DCS | OJO: la carpeta activa es `Saved Games\DCS.openbeta\Scripts\Export.lua` (ver CLAUDE.md de la raíz), no `Saved Games\DCS` | Pendiente. El activo carga SimAppPro, MOZA y DCS-BIOS |
| MSFS 2024 | SimConnect | Pendiente. Cliente instalado |

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
`il2 [--sin-cojin] [--ganancia %] [--minimo 0-255] [--puerto N]` (la consola no usa perfiles, solo los mínimos).
Efectos: motor, buffet, suelo (rodadura + toma de contacto), tren/flaps, aerofreno, carga G, armas, impactos,
bombas/cohetes. Probado con paquetes sintéticos de extremo a extremo (20/10/2026); falta volar.
Nivel mínimo por motor (`Hf8Motors.DefaultMinLevels`), medido con `umbral` sentado y el dial al máximo
(20/10/2026): glúteos 20, espalda 25, hombros 30; muslos sin medir (20, como glúteos). `--minimo N` fuerza uno común.

## Plan

1. ✅ Driver del HF8 con watchdog y pruebas de motores
2. ✅ Lector IL-2 (UDP), modelo de telemetría común y motor de efectos. Pendiente: calibrar en vuelo
3. ✅ Perfiles JSON por avión y app WPF (visor en vivo, efectos, calibración guiada, bandeja)
4. DCS (Export.lua por UDP; añade AoA, rotor de helicópteros, cañón) y MSFS 2024 (SimConnect)
5. Pendientes menores: ajustes avanzados por efecto (escalas de buffet/impactos tras calibrar en vuelo),
   arranque con Windows, cambiar el puerto UDP desde la interfaz (hoy solo en settings.json, requiere reiniciar)

## Convenciones

- Comentarios, mensajes de consola e interfaz en español
- Hablar conmigo en español
