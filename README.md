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

**Vehículos:** rover de ruedas (todos los cuerpos rocosos), sonda atmosférica (gigantes gaseosos) y
**helicóptero no tripulado** (solo la Tierra). El rover lleva además un **dron explorador** en la Tierra y en Marte.

**Escenarios GeoClick a escala real:** en la Tierra se puede jugar sobre 8 áreas reales convertidas desde informes
GeoClick (DEM Copernicus GLO-30 de 30 m, cobertura ESA WorldCover, imagen Sentinel-2, cicatrices, áreas quemadas y
clima ERA5): de 8 a 71 km², con celdas de 30 m y la imagen satelital como textura del terreno.

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
| `game/` | Proyecto Godot 4.7.2 .NET (C#): `scripts/`, `scenes/`, `shaders/`, `assets/` (sprites del rover), `data/rna_model.json` (RNA pre-entrenada), `data/escenarios/` (8 escenarios GeoClick: JSON con grillas + textura satelital), `export_presets.cfg` (Android y Windows) |
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

## Escenarios GeoClick a escala real

Cada informe PDF de GeoClick se convirtió (sin red, solo con el PDF) en un escenario: las figuras ráster del informe
se decodifican y se remuestrean a una grilla cuadrada de celdas de 30 m (la resolución del DEM).

| Capa del informe | Uso en el juego |
|---|---|
| Elevación (viridis sobre el rango del área) | relieve real en metros; el tamaño real sale de las marcas de coordenadas o de la barra de escala (el área del polígono reconstruido coincide con la del informe ±1 %) |
| Cobertura ESA WorldCover | suelo de cada celda: agua (prohibida), manzanas urbanas, bosque denso, matorral, pastizal, cultivos, suelo desnudo |
| Pendiente en 5 clases | escarpes (> 35–40°) |
| Cicatrices y áreas quemadas (Sentinel-2) | detritos y suelo quemado; sitios científicos preferentes |
| Imagen Sentinel-2 en color real | textura del terreno 3D y fondo del mapa 2D en modo verdad |
| Clima ERA5 (lluvia y temperatura mensual, periodos de retorno de Gumbel) | lluvia, temperatura, humedad del suelo, tormentas y remociones del escenario |

- Los mapas tienen de 126×126 a 317×317 celdas (3,8 a 9,5 km de lado). Fuera del polígono analizado no se puede circular.
- **El informe es el conocimiento orbital previo del rover:** relieve y cobertura conocidos desde el inicio. Lo que el
  informe no resuelve (rocas, árboles o edificios puntuales) lo descubre la RNA en el camino.
- A esta escala el vehículo es un rover terrestre de campo (2 m/s, 6 kWh) o el helicóptero.
- **Rendimiento:** la malla del terreno usa un vértice por celda y el detalle visual viene de la imagen satelital más una
  textura de grano procedural. Las creencias y el mapa 2D se pintan como texturas de un píxel por celda. La planificación
  usa A* y Dijkstra sobre arreglos en caché: unos 10–20 ms por paso en mapas de 100 000 celdas.
- Datos: Copernicus DEM GLO-30 © DLR/Airbus (ESA), ESA WorldCover 2021 (CC BY 4.0), Sentinel-2 (Copernicus) y ERA5
  (Copernicus/ECMWF), obtenidos a través de los informes GeoClick.

### Importar un informe PDF dentro del juego (Windows)

En la configuración, con la Tierra elegida, el botón **Importar PDF…** junto a "Terreno (Tierra)" abre un selector de
archivos. También se puede **arrastrar el PDF a la ventana**.

- El juego lee el informe y genera el escenario en menos de 2 s, con el mismo proceso descrito arriba: figuras,
  coordenadas o barra de escala, y tablas.
- El lector de PDF es PdfPig (Apache 2.0).
- El escenario queda en la carpeta de datos del juego (`escenarios/`) y aparece en la lista marcado como *importado*.
  Si tiene el mismo número de consulta que uno incluido, lo reemplaza.
- Al terminar muestra una **validación**: área reconstruida frente a la del informe, pendiente mediana calculada frente
  a la del informe, capas encontradas y si la escala salió de coordenadas o de la barra de escala. Si el área difiere
  más de 15 %, lo advierte.
- Reconoce los dos formatos de informe conocidos: con coordenadas y el antiguo con barra de escala. Con los 8
  informes, el área quedó a menos de 1,3 % de la del informe y las tablas coinciden con el conversor original.
- En Android todavía no está disponible (falta probar el selector de archivos en el teléfono).

## Helicóptero no tripulado (solo Tierra)

- Modelo de potencia por teoría de cantidad de movimiento: el vuelo estacionario necesita (m g)^1,5 / √(2 ρ A) / FM
  y la potencia disponible del motor cae con la densidad del aire. En cordillera y con calor el margen de potencia se
  estrecha (1,74 a nivel del mar y 15 °C; 1,10 a 2600 m y 25 °C).
- La curva de potencia en avance (inducida + perfil + parásita) da el costo de cada tramo; el AG elige rutas que
  ahorran ascensos sobre el relieve real.
- Para muestrear aterriza si la zona es apta (pendiente < 12°, sin agua, árboles, edificios ni rocas); si no, usa un
  cabrestante en vuelo estacionario, que gasta más energía.
- El supervisor agrega la envolvente de vuelo: H-01 margen de potencia, H-02 tasa de ascenso, H-03 lluvia > 8 mm/h,
  H-04 visibilidad < 1500 m. Además S-10 aplica a ráfagas > 15 m/s.
- Supuestos: 300 kg, rotor de 6,4 m, crucero de 25 m/s a 90 m sobre el terreno y 20 kWh embarcados.

## Dron explorador

- Viaja acoplado al rover en la Tierra y en Marte (como Ingenuity). En Mercurio y Venus no hay aire o el ambiente lo
  destruye, así que no está disponible.
- Una decisión difusa (incertidumbre de la ruta, viento y batería del dron) calcula su **conveniencia de despliegue**.
- Vuela al tramo más incierto, la RNA analiza el terreno desde el aire y el LIDAR confirma barrancos y obstáculos.
- Al final barre un radio de 25 m y puede **generar objetivos científicos nuevos** (afloramientos rocosos).
- Restricciones: batería mínima, ráfagas, lluvia, polvo y enlace; regresa antes de agotar la reserva.
- En Marte, con 20 semillas, el dron sube el éxito de 95 % a 100 %, baja las replanificaciones de 15,8 a 11,4 y sube
  la ciencia de 2,65 a 3,26.

## Interfaz

- **Acoples izquierdo y derecho:** las ventanas (telemetría, decisión, bitácora, mapa, control y laboratorios) se
  apilan a los lados sin taparse y se reparten el alto. Al minimizar o cerrar una, las demás crecen.
  - Para acoplar o soltar, arrastra el título hasta un borde. El botón ◧/◨/▣ cambia el acople.
  - La esquina inferior ajusta el ancho del acople y la altura relativa de la ventana.
  - Lejos de los bordes, una ventana queda flotante.
- **Centro libre para el vehículo**, con una **ficha flotante** que lo sigue: decisión (mover, esperar, analizar…),
  batería, temperatura, viento y visibilidad.
- **IA EN VIVO:** una columna vertical con las figuras de los tres laboratorios y del supervisor, del paso actual
  (clic en una sección abre su laboratorio completo):
  - RNA: fila de entrada con el nombre y valor de los 11 rasgos → normalización z → activaciones de la red (las
    neuronas se encienden en cada paso) → probabilidades → acción posterior del supervisor.
  - Difuso: curva agregada y centroide, historia de las salidas, y las 16 reglas con su fuerza y la salida crisp
    (velocidad y cautela) que daría cada una, frente al centroide final.
  - Genético: fitness por generación y mapa de riesgo con las 5 mejores rutas de cada generación (se reproduce la
    evolución de cada corrida nueva), más las candidatas A/B/C.
  - Supervisor: acción, código y una luz por restricción.
- **CONTROL** es un panel colapsable (solo se minimiza, no se cierra).
- **Laboratorios en vivo sin pausar la misión.** Si la ventana es angosta (por ejemplo, acoplada), muestran un
  **resumen de indicadores**: probabilidades, métricas de prueba y estado de las creencias (RNA); entradas, reglas
  activas y salidas (difuso); candidatas, desglose del fitness y curva por generación (genético). El botón
  Auto/Resumen/Completa fuerza la vista.
- **Disposiciones:**
  - Conducción (por defecto): IA en vivo, control y mapa a la derecha.
  - Análisis: los tres laboratorios en resumen.
  - Paneles: telemetría, bitácora, decisión y mapa.
  - Limpia: solo el mundo y la franja.
- La disposición se guarda entre sesiones. La opacidad de las ventanas se ajusta entre 55 y 100 %.

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
| D | desplegar el dron (a la celda bajo el cursor o al tramo más incierto) · en vuelo: ordenar regreso |
| Tab / H | laboratorio RNA / ayuda |
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
| `game/scripts/core/` | `Rng` (SplitMix64 determinista), ruido fBm, `Cell`; `Scenarios` (catálogo de escenarios GeoClick incluidos e importados); `GeoClickPdf` (importador de informes PDF); `HeadlessRunner` (`--selftest`, `--benchmark`, `--diag [--scenario=id] [--heli]`) |
| `game/scripts/sim/` | `PlanetProfile` (8 cuerpos), `GeoReport` (datos de un informe GeoClick), `WorldMap` (40×40 celdas de 5 m o escenario a escala de 30 m), `SimEnvironment` (reloj, viento OU, lluvia, polvo, sismos, eventos), `Sensors` (`RoverState`, `SensorSuite`, validación), `Mission` (ciclo por paso, estados, física, ablaciones, eventos, CSV/JSON), `Drone` (dron explorador), `Helicopter` (modelo de potencia y vuelo), `Benchmark` |
| `game/scripts/ai/` | `NeuralNet` (MLP 11→32→16→3 desde cero, Adam, métricas), `Perception` (modelo de rasgos, dataset por mapas, `BeliefMap`), `Fuzzy` (Mamdani genérico + 6 entradas / 2 salidas / 16 reglas), `Planning` (`RouteGA` de puntos de paso, `MissionGA` viajero+mochila, `CostModel`), `Supervisor` (S-01…S-13 y H-01…H-04 de vuelo) |
| `game/scripts/view/` | terreno (procedural o satelital), objetos, capas (rutas, creencias, marcadores), robot (MechRig) y helicóptero, dron, cámara, cielo y partículas |
| `game/scripts/ui/` | tema, HUD, ventanas flotantes (`FloatPanel`, `WindowManager`), paneles dibujados a mano, configuración, laboratorios en vivo |
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

| Política | Éxito | Ciencia | Replanificaciones | Violaciones de seguridad |
|---|---|---|---|---|
| Híbrido completo | 100 % | 3,26 | 11,4 | 0,10 |
| Híbrido sin dron | 95 % | 2,65 | 15,8 | 0,15 |
| Sin RNA | 80 % | 2,80 | 10,3 | 0,60 |
| Sin difuso | 90 % | 3,22 | 26,2 | 0,15 |
| Sin AG | 65 % | 4,86 | 48,0 | 0,45 |
| AG puro | 80 % | 2,49 | 8,9 | 0,75 |
| Control (menor distancia) | 40 % | 2,52 | 20,7 | 1,55 |

## Supuestos y límites

- **Simulación ficticia.** Los valores planetarios son aproximaciones de referencia (NASA). Daño, energía, radiación y fallos son hipótesis pedagógicas (ver la pestaña Ayuda y el JSON exportado).
- **Escenarios GeoClick:** el relieve sale de la figura de elevación del informe (cuantizada a 256 niveles y con
  compresión JPEG), no del GeoTIFF original. La pendiente mediana calculada queda entre 0,5° y 2° por debajo de la
  del informe. El rover terrestre de campo y el helicóptero son vehículos supuestos.
- **Supuestos de diseño:** celda de 5 m, pendiente máxima 30°, precipicio = caída mayor a 5 m. Rover de 900 kg, 0,5 m/s y 320 Wh. Sonda de 350 kg, 2,5 m/s, 420 Wh y RTG de 110 W. El día está comprimido para que haya ciclo día/noche dentro de una misión.
- **Tierra calibrada con GeoClick 40e9efb1 (Santiago):** la escala vertical se ajusta hasta una pendiente mediana de 6,7°. Usa la lluvia media mensual (ERA5), los periodos de retorno de Gumbel para tormentas y remociones, y las temperaturas mensuales.
- **Navegación:** usa la celda real; el error de odometría se calcula y se muestra como diagnóstico, y se corrige en refugios y sitios.
- **Ciencia, no ficción:** el detector de vida solo produce una *señal anómala simulada*. El "alien" es narrativa ficticia.
- **Rover:** los sprites del rover vienen pre-renderizados en `game/assets/mechs/parts/exp_*`; el pipeline que los genera no forma parte de este repositorio.
- **Diferencia con el entregable del caso:** el caso pide Python en Google Colab. Este proyecto es un juego en Godot/C#. Los tres algoritmos están implementados desde cero y se pueden portar uno a uno a un notebook (MLP con scikit-learn/NumPy, Mamdani, AG).
