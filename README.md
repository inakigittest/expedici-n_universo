# Expedición Universo · exploración planetaria con IA híbrida (Godot 4.7 .NET)

Juego/simulador interactivo basado en el caso de evaluación ARES-X del curso de IA aplicada (rover autónomo con IA híbrida).

**Dinámica:** el operador **hace clic en el terreno** y el rover primero **evalúa el destino** con lo que cree del
mapa: ruta estimada, energía de ida y de regreso al refugio (margen ×1,25), riesgo de la RNA, fracción sin observar,
pendiente, temperatura, viento, lluvia o polvo y hora de la puesta de sol. Emite un veredicto (VIABLE · CON RIESGO ·
SOLO IDA · ENERGÍA INSUFICIENTE · INALCANZABLE) y luego navega hasta allí. Clic derecho cancela. Como alternativa
existe la misión autónoma, donde el AG planifica sitios y regreso (tecla M).

**Robot:** el mech-rover **EXPLORADOR**, creado con el pipeline de render SDF de un proyecto de mechas (los sprites ya vienen exportados en `game/assets/mechs/parts/exp_*`):
patas con bogies de 3 ruedas (estilo ATHLETE), chasis de electrónica con lámina dorada, mástil de cámaras, brazo
robótico con torreta, antena de alta ganancia, paneles solares y RTG. En los gigantes gaseosos (modo sonda) se usa el
Solaris Ranger con propulsores.

**Cada paso** hacia el destino pasa por el ciclo completo:

```
generador del mundo → sensores con ruido → validación → RNA → creencias → AG (si corresponde)
→ difuso → supervisor de seguridad → acción → física simplificada → bitácora y HUD
```

Antes de moverse, el rover evalúa: terreno (pendiente, rugosidad, caída LIDAR, suelo), proximidad, temperatura
exterior e interior, presión, gases, viento y ráfagas, lluvia (Tierra), visibilidad y polvo, vibración y sismos,
radiación, energía (batería, solar, consumo y reserva para volver al refugio), integridad y enlace.
Todo se ve en tiempo real en el HUD.

## Estructura del repositorio

| Ruta | Contenido |
|---|---|
| `game/` | Proyecto Godot 4.7.2 .NET (C#): `scripts/`, `scenes/`, `shaders/`, `assets/` (sprites del rover), `data/rna_model.json` (RNA pre-entrenada), `export_presets.cfg` (Android y Windows) |
| `installer/` | Script de Inno Setup del instalador de Windows (`crear_instalador.cmd`) |
| `jugar.cmd` | Compila el C# y abre el juego (o el editor con `-e`) |

Las builds (`exports/`: APK y ejecutable de Windows) no se versionan.

## Ejecutar

```
jugar.cmd            (desde iaaplicada_eva3/: compila el C# y abre el juego)
jugar.cmd -e         (abre el editor de Godot)
```

Requisitos: .NET SDK 8 y Godot 4.7.2 **mono/.NET**. `jugar.cmd` usa la variable de entorno `GODOT` (ruta al
ejecutable de Godot) si está definida. `game/nuget.config` apunta a los paquetes NuGet que trae el editor de Godot:
ajusta esa ruta a tu instalación. Exportar a Android requiere además .NET 9, JDK 17 y el Android SDK (ver más abajo).
Al arrancar, la RNA se entrena una vez en segundo plano (unos 10 s en CPU). Después se elige la configuración y se pulsa **Validar e iniciar**.

## Windows (instalador)

- `exports/ExpedicionUniverso_Setup_1.0.exe`: se genera con `installer/crear_instalador.cmd` (requiere Inno Setup).
  Se instala por usuario, sin permisos de administrador, en español, con accesos directos y desinstalador.
- `exports/ExpedicionUniverso_Windows_portable.zip`: descomprimir y abrir `ExpedicionUniverso.exe`.
- Incluye el runtime .NET: no hay que instalar nada más. Windows 10/11 de 64 bits.
- No está firmado: SmartScreen puede avisar ("Más información → Ejecutar de todas formas").
- Alt+Enter alterna pantalla completa. F12 exporta a `Documentos\Expedicion Universo`.

## Android (APK)

`exports/ExpedicionUniverso.apk`: firma de depuración, arm64-v8a, Android 7.0 o superior. Para instalarlo, cópialo al
teléfono y ábrelo (hay que permitir "instalar apps desconocidas"), o usa `adb install -r ExpedicionUniverso.apk`.

En el teléfono:
- **Toque** en el terreno = asignar destino (o la herramienta elegida: barranco, roca o despejar).
- **Mantener pulsado** = ver la celda.
- **Pellizco** = zoom.
- Barra derecha: ejecutar, cancelar destino, herramienta, girar cámara y zoom.
- Pestañas inferiores: abren telemetría, decisión, bitácora, mapa y control (de uno en uno), más los laboratorios.
- **Atrás** cierra el panel o el laboratorio; desde la misión vuelve a la configuración.

La red neuronal viene pre-entrenada en `data/rna_model.json`, con los mismos pesos que produce la semilla 2026, así
que el teléfono no entrena. Las exportaciones CSV/JSON quedan en la carpeta de datos de la app.

## Controles

| Tecla | Acción |
|---|---|
| Espacio / N | ejecutar-pausar / un solo paso |
| Clic izq. en el terreno o el mapa 2D | asignar destino (evaluación + navegación) · clic der. = cancelar |
| Pasar el mouse | previsualización de la celda: distancia, elevación y pendiente estimadas, riesgo, temperatura, suelo |
| Mayús / Ctrl / Alt + clic | agregar barranco (el rover no lo sabe hasta percibirlo) / roca / despejar |
| M | destino por clic ↔ misión autónoma |
| 1–4 | velocidad ×1 ×2 ×4 ×8 |
| Q/E, rueda | girar cámara 45°, zoom |
| V / T / C | mapa de riesgo RNA / verdad del simulador / rutas candidatas del AG |
| P | forzar replanificación del AG |
| F1…F11 | sismo, tormenta, meteorito, fallo de sensor (Mayús+F4 repara), noche, enlace, ráfaga, biofirma, alien (ficción), batería 12 %, remoción (Tierra) |
| Tab / H | laboratorios / ayuda |
| F12 | exportar traza CSV y resultado JSON a `game/exports/` |
| R | volver a la configuración |

## HUD (paneles A–E del caso)

- **Barra superior:** planeta y modo, sol y hora local (día/noche), estado de la máquina de estados, modo de control, política (ablación), semilla y paso.
- **B · Telemetría:** unos 40 canales con unidad, barra frente a sus límites y estado de validación (OK, AVISO, CRÍT, NaN, SAT, ATASC, CONTR, n/a). Debajo, estimaciones derivadas: autonomía, energía al refugio, calefactor, polvo en paneles, dosis, bodega, error de odometría, margen térmico, índice de remoción y latencia de órdenes.
- **C · Decisión explicable:** evaluación del destino (veredicto, distancia, energía de ida y regreso, batería al llegar y tras volver, ETA, riesgo, terreno y clima en el destino, avisos), RNA (probabilidades, confianza, vector de entrada normalizado, neuronas activas), difuso (grados de membresía, reglas activadas, curva agregada y centroide), AG (plan viajero+mochila, candidatas A/B/C con desglose C/D/E/R/T/P) y supervisor (acción, código S-xx, 13 restricciones con ✓/✗).
- **D · Traza temporal:** bitácora filtrable (decisiones, vetos y fallos, eventos, AG).
- **E · Control:** ejecutar, paso, reiniciar, exportar, modo, velocidad, eventos, laboratorios.
- **A · Mapa 2D:** creencia del rover o verdad del simulador, rutas planificada, candidatas y ejecutada, sitios, refugios y elipse de error de posición. A su lado, minigráficos de batería, temperatura interior, ráfaga, velocidad y riesgo.
- **Vista 3D:** baliza del destino con veredicto y pasos restantes, celda bajo el mouse, relieve, celdas prohibidas o vetadas (X), mapa de riesgo, rutas, celda evaluada (verde, ámbar o rojo), alcance de sensores según la visibilidad, lluvia y polvo con el viento, ciclo día/noche, faros e impacto de meteoritos.
- **Laboratorios:** RNA (red con activaciones, curvas, matriz de confusión, deslizadores por rasgo · CA-06), Difuso (curvas, reglas, agregación · CA-05), Genético (fitness por generación, selector de generación, cromosomas, cruce, mutación · CA-04), Políticas (20 semillas × 6 políticas · RF-14/15, CA-08) y Ayuda.

## Arquitectura (`scripts/`)

| Carpeta | Contenido |
|---|---|
| `game/scripts/core/` | `Rng` (SplitMix64 determinista), ruido fBm, `Cell`; `HeadlessRunner` (`--selftest`, `--benchmark`, `--diag`) |
| `game/scripts/sim/` | `PlanetProfile` (8 cuerpos + calibración `GeoClick`), `WorldMap` (40×40 celdas de 5 m), `SimEnvironment` (reloj, viento OU, lluvia, polvo, sismos, eventos), `Sensors` (`RoverState`, `SensorSuite`, validación), `Mission` (ciclo por paso, estados, física, ablaciones, eventos, CSV/JSON), `Benchmark` |
| `game/scripts/ai/` | `NeuralNet` (MLP 11→32→16→3 desde cero, Adam, métricas), `Perception` (modelo de rasgos, dataset por mapas, `BeliefMap`), `Fuzzy` (Mamdani genérico + 6 entradas / 2 salidas / 16 reglas), `Planning` (`RouteGA` de puntos de paso, `MissionGA` viajero+mochila, `CostModel`), `Supervisor` (S-01…S-13) |
| `game/scripts/view/` | terreno, objetos, capas (rutas, creencias, marcadores), robot (MechRig), cámara, cielo y partículas |
| `game/scripts/ui/` | tema, HUD, paneles dibujados a mano, configuración, laboratorios |
| `game/scripts/mechs/` | `MechRig` y `MechData`, copiados del proyecto de mechas (sprites del rover en `assets/mechs/parts/exp_*`) |

La simulación (`sim/`, `ai/`) no usa la API de Godot: corre sin ventana y es determinista por semilla.

## Correspondencia con la pauta (resumen)

- **RF-01, CA-07:** configuración y validación (Júpiter + ruedas bloqueado, con propuesta de sonda; NaN rechazado).
- **RF-02:** mapas con elevación, obstáculos, refugios y sitios; edición manual en el minimapa.
- **RF-03:** ciclo por paso con sensores ruidosos.
- **RF-04:** RNA real con partición por mapas: 24 de entrenamiento, 6 de validación y 8 de prueba. Exactitud en prueba ≈ 96–97 % frente a una línea base de 36,6 %; macro-F1 ≈ 0,97; recall de barranco ≈ 96 %; ECE ≈ 0,01.
- **RF-05, RF-06:** 6 variables con 3 conjuntos cada una, 16 reglas justificadas, centroide y respaldo documentado.
- **RF-07:** AG con torneo, cruce, mutación, elitismo, reparación, parada temprana y 3 candidatas inspeccionables.
- **RF-08, RF-12:** traza por paso; exportación CSV/JSON con semilla, versión y supuestos.
- **RF-09, RF-10, RF-11:** controles, vista ejecutada vs. planificada y laboratorios.
- **RF-13:** fallos de sensor, enlace y modo seguro sin excepciones.
- **RF-14, RF-15:** benchmark de políticas con media, mediana y desviación.
- **Familias de subproblemas del AG:** laberinto (factibilidad de ruta), viajero (orden de visitas) y mochila (muestras bajo capacidad y energía).

Ejemplo, Marte con 20 semillas y batería del 80 % (`--benchmark=marte`):

| Política | Éxito | Violaciones de seguridad |
|---|---|---|
| Híbrido | 95 % | 0,20 |
| Sin RNA | 85 % | 0,65 |
| Sin difuso | 95 % | 0,10 |
| Sin AG | 75 % | 0,45 |
| AG puro | 85 % | 0,50 |
| Control (menor distancia) | 60 % | 1,65 |

## Supuestos y límites

- **Simulación ficticia.** Los valores planetarios son aproximaciones de referencia (NASA). Daño, energía, radiación y fallos son hipótesis pedagógicas (ver la pestaña Ayuda y el JSON exportado).
- **Supuestos de diseño:** celda de 5 m, pendiente máxima 30°, precipicio = caída mayor a 5 m. Rover de 900 kg, 0,5 m/s y 320 Wh. Sonda de 350 kg, 2,5 m/s, 420 Wh y RTG de 110 W. El día está comprimido para que haya ciclo día/noche dentro de una misión.
- **Tierra calibrada con GeoClick 40e9efb1 (Santiago):** la escala vertical se ajusta hasta una pendiente mediana de 6,7°. Usa la lluvia media mensual (ERA5), los periodos de retorno de Gumbel para tormentas y remociones, y las temperaturas mensuales.
- **Navegación:** usa la celda real; el error de odometría se calcula y se muestra como diagnóstico, y se corrige en refugios y sitios.
- **Ciencia, no ficción:** el detector de vida solo produce una *señal anómala simulada*. El "alien" es narrativa ficticia.
- **Rover:** los sprites del rover vienen pre-renderizados en `game/assets/mechs/parts/exp_*`; el pipeline que los genera no forma parte de este repositorio.
- **Diferencia con el entregable del caso:** el caso pide Python en Google Colab. Este proyecto es un juego en Godot/C#. Los tres algoritmos están implementados desde cero y se pueden portar uno a uno a un notebook (MLP con scikit-learn/NumPy, Mamdani, AG).
