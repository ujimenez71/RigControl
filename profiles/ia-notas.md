# Notas para la IA (las lee "Generar con IA"; copia en %APPDATA%\RigControl\ia-notas.md, editable)

Lo que se sabe de cada mando y que no está en los nombres del catálogo. Si añades un mapa de botones
nuevo (con `RigProbe monitor`), apúntalo aquí para que la IA lo use.

## Reglas generales
- Abro todo en modo compartido; SimAppPro, Pit House, VPC Configurator y SimHub siguen funcionando.
- Los ejes (stick, palancas, colectivo, pedales) los asigno en el simulador, no en RigControl.
- MSFS 2024 trae su propio perfil para el Yoke y el Bravo: si un botón está asignado en MSFS y en
  RigControl, los interruptores se anulan. Quité en MSFS el AP y el trim del Bravo.
- El Bravo NO se usa con DCS.

## Honeycomb Bravo (294B:1901)
- Autopiloto: HDG b1, NAV b2, APR b3, REV b4, ALT b5, VS b6, IAS b7, AP b8.
- Tren: arriba b31, abajo b32 (botón mantenido mientras la palanca está en esa posición).
- Rueda de trim: b22 = morro abajo, b23 = morro arriba (un pulso de ~16 ms por clic: usar "press").
- Palancas de izquierda a derecha: 1 = eje Y, 2 = X, 3 = RZ, 4 = RY, 5 = RX, 6 = Z (abajo 0 %, arriba
  100 %). Al pasar del tope de abajo dan un botón: 1 b24, 2 b25, 3 b26, 4 b27, 5 b28, 6 b33. La 4 solo
  llega a 0,9-98,3 %.
- b21 y b34-b47: sin identificar (no usar).

## Honeycomb Yoke Alpha (294B:1900)
- Los nombres de todas las entradas están en el catálogo. Interruptores: arriba = ON = botón impar.
- Para usar un botón como modificador mantenido, mejor b1-b4 que los basculantes.

## Moza AB9 + MH16 (346E:1000) — stick de jet y cíclico de helicóptero
- Gatillo: b1 primer tope, b6 a fondo (b1 sigue pulsado). b2 botón de arriba del todo; b5 lado derecho de la cabeza.
- Hat de arriba: b20 arriba, b21 derecha, b22 abajo, b23 izquierda, centro b24.
- Hat del pulgar: b28 arriba, b29 derecha, b30 abajo, b31 izquierda (sin centro).
- Hat de debajo del pulgar: b11 arriba, b12 derecha, b13 abajo, b14 izquierda.
- Hat central (debajo de b2): b7 arriba, b8 derecha, b9 abajo, b10 izquierda.
- Hat de la izquierda: b15 arriba, b16 derecha, b17 abajo, b18 izquierda, centro b19.
- Basculante: b26 arriba, b25 abajo.
- En DCS con el AH-64D, b28 (Force Trim) y los botones de George (b7-b11, b13, b14) están asignados en
  el propio DCS: no duplicarlos en ese avión.

## Colectivo VPC Rotor TCS PLUS (3344:832E) — AH-64D
- El perfil ah64d.json tiene todos sus botones identificados (ver sus "desc").
- b2, b7 y b21 = pulsar el centro de los hats FCR Scan Size / FCR Mode / Sight Select (el real no los
  tiene: sin asignar). Eje Y = fricción física (sin asignar).

## WinWing (throttle F-15EX, pedales, MFD, PTO 2, ICP)
- Mapa de botones del throttle F-15EX, los MFD, el PTO 2 y el ICP: AÚN SIN IDENTIFICAR. En DCS los MFD
  están asignados en el propio DCS. No asignar sus botones sin que yo diga cuál es cuál.
- Sus luces sí están identificadas (lista de luces WinWing).
