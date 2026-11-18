# DraftOptimizer

Aplicación de escritorio Windows en C# y WPF, basada funcionalmente en [DraftGap](https://github.com/vigovlugt/draftgap), con perfiles de runas y ruta de objetos. Esta es una implementación inicial utilizable, con integración LCU implementada y pruebas simuladas; la selección e importaciones deben verificarse también con un cliente real abierto.

## Abrir y ejecutar

Abre `DraftOptimizer.slnx` en Visual Studio con la carga de trabajo **Desarrollo de escritorio de .NET**. Selecciona **DraftOptimizer.Wpf** como proyecto de inicio. También puedes ejecutar estos comandos desde la carpeta de la solución.

```powershell
.\run.ps1
dotnet build .\DraftOptimizer.slnx -c Release
dotnet run --project .\DraftOptimizer.Tests
dotnet publish .\DraftOptimizer.Wpf -c Release -r win-x64 --self-contained false
```

La aplicación requiere Windows, el SDK de .NET 10 para compilar y **.NET Desktop Runtime 10** para ejecutar una publicación dependiente del runtime. Las pruebas ejecutan el núcleo en .NET 8. El publicador genera una aplicación de escritorio con `DraftOptimizer.exe` y su carpeta `Data`. LCU no requiere una clave de API de Riot.

## Uso

1. Abre League of Legends y DraftOptimizer. La detección consulta el proceso de League y su lockfile. Si no lo encuentra, indica la ruta completa del archivo desde **Ruta de League**. Deja el campo vacío para volver a detección automática. No introduzcas contraseñas de tu cuenta.
2. **Campeones** contiene el buscador, las estrellas de favoritos y la lista. **Runas y objetos** contiene los perfiles y las opciones de automatización. **Estado del draft** muestra composición, posiciones y estado detallado. Se sincroniza cada 200 ms durante tu turno, cada 500 ms durante el resto del draft y cada dos segundos fuera de selección. Desmarca **Seguir mi campeón y posición del cliente** en Estado del draft para trabajar manualmente.
3. Selecciona una sugerencia o usa el selector de campeón. **Preseleccionar** cambia la selección sin cerrarla; **Confirmar campeón** pide confirmación y completa la acción local. Ambas acciones se habilitan durante tu turno y verifican de nuevo la disponibilidad al pulsarlas.
4. En **Runas y objetos**, pulsa **Cargar Lolalytics D2+** para obtener la build más popular del campeón, posición y parche mostrados: D2+, Global, Solo/Duo. El proveedor verifica esos filtros en la respuesta Qwik, además de campeón y parche; el campo header.tier es la clasificación del campeón (S/A/etc.), no el filtro de elo. Revisa y guarda. Si Lolalytics bloquea la petición o cambia su formato se muestra el fallo. El filtro D2+ corresponde a builds/runas; el análisis del draft sigue utilizando los datasets públicos de DraftGap.
5. Edita las ramas, las cuatro runas principales, dos secundarias de filas distintas y los tres fragmentos. Los objetos se escriben por bloques: `Inicio: 1056, 2003, 2003`. Acepta saltos CR, LF y CRLF, líneas vacías y objetos en varias líneas: escribe `Inicio:` y coloca los IDs en las líneas siguientes, separados por comas, espacios o saltos. Una lista de IDs sin cabecera se convierte en un bloque `Objetos`. El buscador muestra nombres e IDs y añade el objeto al último bloque respetando los saltos existentes. Un ID repetido representa cantidad. Los nombres de bloques importados con saltos, dos puntos o comillas se muestran como una cadena JSON entre comillas, con escapes, para que la edición conserve el nombre original y sus condiciones de hechizos. Los bloques de alternativas de Lolalytics contienen opciones, no un orden obligatorio de compra. **Nuevo** parte de un ejemplo editable de mago, no de una recomendación para el campeón seleccionado.
6. **Guardar perfil** persiste la configuración por campeón y posición. **Importar build .json** abre un archivo exportado de conjuntos de objetos de League, como `PantheonSR.json`: acepta una lista de builds, un conjunto individual o un contenedor `itemSets`. Conserva título, campeón, mapas, bloques, orden, cantidades y condiciones de hechizos de los bloques. Si contiene varias builds se guardan todas en **Mis perfiles**. Para varios campeones asociados se crea un perfil por campeón; si no indica campeón usa el seleccionado en pantalla. Como el archivo no incluye posición, utiliza la posición elegida al importar. **JSON ↔** permite pegar esos mismos formatos o copiar/pegar perfiles nativos como nuevos, incluso si el editor todavía está incompleto. Los perfiles guardados pueden seleccionarse y eliminarse desde la interfaz.

   Los conjuntos de objetos importados no contienen runas: se guardan y pueden enviarse a League directamente. Para **Aplicar runas a League**, configura primero las nueve selecciones del editor. Importar el archivo no cambia automáticamente el cliente. Los objetos se validan contra el catálogo del parche; ante un objeto retirado se muestra un error y no se guarda parcialmente la importación. Límite de archivo: 4 MB; hasta 100 builds por archivo, 99 unidades por entrada de objeto y 500 unidades por bloque.
7. **Aplicar hechizos, runas y objetos al confirmar campeón** está activada inicialmente y se conserva en favorites.json. La app prepara y aplica la configuración tras confirmar tu campeón, sin esperar a los demás picks. Cada escritura revalida partida, campeón y posición. Puedes desactivar la opción para usar los botones manuales. **Aplicar runas a League** crea o actualiza exclusivamente una página editable llamada `DraftOptimizer`; nunca borra otras páginas. **Enviar objetos a League** conserva los demás conjuntos y recomienda compras dentro del juego; no compra objetos automáticamente.

En **Runas y objetos** puedes elegir un hechizo distinto para cada tecla, **D** y **F**. Se guardan con el perfil. **Aplicar hechizos a League** los envía manualmente durante la selección de campeones. Las recomendaciones de Lolalytics incluyen los hechizos cuando la fuente entrega una pareja válida. Si ambos campos quedan en **Sin configurar**, se conserva la selección del cliente.

   Se prioriza el perfil cargado y válido del campeón/posición; si no lo hay, se busca el perfil guardado coincidente más reciente. Sin perfil se descarga automáticamente Lolalytics D2+. Si el perfil contiene solo objetos, se conservan sus compras y se completan las runas con D2+. La descarga se precarga sin bloquear el sondeo del cliente. El ejemplo de perfil nuevo no se considera una configuración guardada ni impide el fallback.

   Antes de cada escritura se revalidan partida, campeón, posición y fase final. La salida del draft, cambio de campeón o desconexión cancela trabajos pendientes. Se aplica una sola vez por configuración y selección; los conjuntos automáticos tienen una identidad estable para evitar duplicados entre partidas. **Ver configuración preparada** carga el resultado en el editor. Los fallos aparecen en el resumen y **Reintentar configuración** inicia un nuevo intento, sin repetir automáticamente escrituras fallidas. Si faltan páginas de runas o termina la ventana de importación se informa del error; puede haber aplicación parcial si la primera escritura tuvo éxito y la segunda falló.

## Datos y privacidad

- Estadísticas base: `https://bucket.draftgap.com/datasets/v5/current-patch.json`.
- Enfrentamientos y sinergias: `https://bucket.draftgap.com/datasets/v5/30-days.json` (aproximadamente 54 MB en la verificación). Se descargan al iniciar/actualizar y se guardan en caché. La fecha de ambos datasets se muestra en pantalla.
- Builds: página pública de Lolalytics, decodificando su JSON Qwik. Se validan campeón, rol, parche, runas y objetos antes de cargarlos. No se utiliza el endpoint antiguo `ax.lolalytics.com`, que falló en la verificación.
- La distribución incluye un catálogo base real del 8 de noviembre de 2026 para permitir edición sin conexión. En modo caché revisa el parche/fecha. Los enfrentamientos solo están disponibles sin red si ya se descargaron anteriormente.
- Perfiles, respaldo `.bak`, caché y ruta configurada: `%LOCALAPPDATA%\DraftOptimizer`. Un archivo de perfiles corrupto se conserva y bloquea nuevas escrituras hasta que se recupere.
- Las credenciales efímeras del cliente permanecen en memoria. Las peticiones LCU se limitan a `https://127.0.0.1`; la excepción de certificado autofirmado se aplica solo a ese cliente HTTP y puerto local. Las peticiones de Internet usan validación TLS normal y un cliente separado. No se envían credenciales de League a Lolalytics o DraftGap.

## Confianza del draft y actualización de la lista

Cuando todavía no hay picks, el puntaje inicial usa el **winrate del campeón en el rol seleccionado** y sus **partidas del parche en ese mismo rol**. No suma partidas de otras posiciones ni añade las de 30 días. La confianza inicial es 100 veces el límite inferior de Wilson bilateral al 99%, con un mínimo de 200 partidas en el rol seleccionado. Cambiar de rol recalcula el orden y la muestra. Funciona también sin cliente conectado o sin contexto descargado.

En cuanto aparecen picks, el puntaje pasa al draft completo al añadir el candidato. En ese modo el WR base de la posición queda como referencia. La preselección local del primer turno y las intenciones de preselección no crean por sí solas un draft ficticio: se conserva la confianza global inicial hasta que haya otro pick visible o un pick confirmado. El cambio de modo invalida la caché y actualiza las filas existentes.

El analizador conserva las ecuaciones de rating/winrate de DraftGap y calcula incertidumbre de las tasas de campeones, enfrentamientos y sinergias que intervienen en esa composición. Para una proporción con victorias w y partidas n, estima su varianza con parámetros Beta(w+0,5, n-w+0,5), la transforma a la escala de rating y propaga las varianzas de los componentes del draft. Las muestras del parche y de 30 días se consideran solapadas; no se suman como partidas independientes. Las dos direcciones de un matchup también se promedian, no se cuentan dos veces. Las 1000 partidas de regularización estabilizan el WR puntual pero no se contabilizan como evidencia jugada.

Cuando falta un par, su efecto puntual permanece neutral pero se incorpora incertidumbre amplia. El rango orientativo se obtiene transformando el rating total más/menos 1,95996398454 errores estándar. El puntaje es 100 veces el extremo inferior del rango del draft. Un mismo winrate base puede obtener puntuaciones diferentes contra distintas composiciones. Manteniendo el mismo WR contextual, una muestra más sólida estrecha el rango y mejora el límite conservador.

Esta propagación es una heurística para ordenar: los pares y campeones pueden compartir partidas y los roles inferidos son una hipótesis. El rango no tiene una cobertura empírica calibrada del 99% ni representa certeza sobre el resultado de una partida. Se aplica a los picks conocidos y al candidato, no a futuros picks desconocidos. El historial personal no entra en el cálculo.

Los favoritos siguen primero; dentro de cada grupo se usa confianza global mientras no haya picks y confianza del draft cuando exista composición. En modo draft se mantiene el mínimo de 200 partidas por campeón/posición. Si ya hay picks pero falta el dataset de contexto, el puntaje contextual aparece como — y la automatización espera esos datos. No se usa el puntaje global para ocultar la falta de contexto de una composición existente.

La lista se enlaza una sola vez a una ObservableCollection. Cada campeón conserva su instancia e implementa INotifyPropertyChanged. Los cambios notifican únicamente las propiedades afectadas y la reordenación usa Move, Insert o Remove: no se sustituye ItemsSource, no se vacía la colección ni se emite Reset. Se conservan las instancias al filtrar y restaurar resultados. Los cambios del reloj reutilizan el análisis.

La ventana separa Campeones, Runas y objetos y Estado del draft en pestañas. Campeones mantiene una lista con desplazamiento propio y resúmenes compactos; las opciones de perfiles y el detalle de la composición no ocupan permanentemente su altura.


## Winrate dinámico del draft

**WR base** representa la tasa del campeón y posición en el parche; permanece fija durante la selección. **WR draft** estima el resultado de la composición al colocar ese candidato en tu posición, reemplazando tu preselección anterior. Se actualiza con los picks e intenciones visibles que entrega LCU. La cabecera muestra por separado la estimación de la composición actual, cantidad de picks conocidos y pares con datos. Los cambios del reloj no disparan un nuevo análisis; cualquier cambio de campeones, posiciones, acciones o disponibilidad invalida la caché.

Se portaron las ecuaciones de `packages/core/src/draft/analysis.ts` de DraftGap: ratings de los campeones aliados, sinergias aliadas y enfrentamientos, menos ratings y sinergias rivales. Se utiliza la transformación rating/winrate original y el riesgo medio del proyecto de referencia: 1000 partidas previas, usando el dataset de 30 días como referencia para la tasa base y los pares. Las estadísticas direccionales de cada pareja se promedian igual que en el original. El puntaje de confianza aplica incertidumbre a esa estimación completa.

LCU suele omitir posiciones de los enemigos. Se usa la combinación de roles más probable según sus frecuencias en el dataset del parche, respetando las posiciones conocidas y asignando como máximo un campeón por posición. En la cabecera, los roles inferidos llevan `*`. Para un candidato se reserva su posición antes de inferir el resto de aliados. Si un campeón no tiene frecuencias del parche se consulta el dataset de contexto; ante ausencia total de frecuencias se usa una asignación neutral determinista.

Si falta el dataset de contexto se muestra **WR draft —** y un mensaje explícito en la cabecera; el WR base no se presenta como si fuera contextual. Las estimaciones dependen de las posiciones inferidas y los datos disponibles y no son probabilidades calibradas de una partida concreta. El orden y la preselección automática conservan el puntaje de confianza solicitado.

La equivalencia numérica se verificó ejecutando las funciones TypeScript originales del commit de referencia sobre datos reales: draft vacío, Ahri versus Zed, tres picks por equipo y draft completo. Los resultados y un subconjunto de los datos relevantes se incluyen en `DraftOptimizer.Tests/Fixtures/DraftGapParity.json`; las pruebas C# exigen una diferencia inferior a `1e-10`. La predicción conjunta de cinco roles también se contrasta contra el resultado original.

## Favoritos y selección automática

Pulsa la estrella junto al nombre de un campeón en la lista principal: **☆** lo añade a favoritos y **★** lo quita. Se guarda inmediatamente para la posición seleccionada. Los campeones marcados aparecen primero y cada grupo mantiene el orden por índice estadístico del draft. El buscador de la lista permite filtrar por nombre sin perder las marcas. Se muestra todo el catálogo para poder marcar también campeones sin estadísticas suficientes o no disponibles en el draft; esas condiciones se indican en cada fila y la selección automática mantiene sus restricciones de disponibilidad y muestra mínima. Se conservan listas independientes para Top, Jungle, Middle, Bottom y Support en `%LOCALAPPDATA%\DraftOptimizer\favorites.json`, con respaldo `.bak`. El panel **Favoritos y selección automática** muestra el resumen y las opciones de automatización.

**Preseleccionar la mejor opción (favoritos si los hay)** está habilitado inicialmente. Durante una acción local activa elige el favorito disponible con mayor confianza del modo actual; si no hay favoritos para ese rol, elige entre todos los candidatos válidos. Usa confianza del rol sin picks y del draft cuando existe composición. Recalcula al cambiar la composición sin reenviar la misma preselección en cada sondeo. Usa la posición asignada por el cliente o la elegida si el cliente no la informa. Si hay favoritos, no elige fuera de ese conjunto. Respeta disponibilidad y mínimo de 200 partidas. Con picks existentes, espera los datos de contexto antes de actuar.

**Confirmar automáticamente al quedar 1 segundo** está desactivado inicialmente. Al activarlo junto con la preselección, intenta confirmar el mejor favorito cuando el tiempo restante es mayor que cero y menor o igual a 1000 ms. Si la app entra al turno durante el último segundo, puede enviar directamente la confirmación. La pantalla muestra el contador que entrega el cliente. Durante tu turno la app sondea cada 200 ms; el resto de la selección, cada 500 ms; fuera de selección, cada dos segundos.

La pestaña **Baneos prioritarios**, a la izquierda de **Simulación**, permite mantener una lista ordenada para Top, Jungle, Middle, Bottom y Support. Elige la línea, añade campeones y usa **Subir**, **Bajar** y **Quitar** para ajustar el orden. La opción **Banear automáticamente en los últimos 8 segundos** está desactivada inicialmente. Al activarla, durante tu acción local de ban, se recorre en los últimos ocho segundos la lista de tu posición (o la posición elegida si League no la informa); se saltan campeones no baneables, ya seleccionados/baneados o preseleccionados por un compañero. La acción y la intención aliada se vuelven a validar en LCU justo antes de enviar el ban. Si no queda una opción segura de la lista, la app no banea. Las listas se guardan en `favorites.json` junto con las demás preferencias.

La confirmación usa `timer.adjustedTimeLeftInPhase` y `timer.internalNowInEpochMs` para descontar el tiempo transcurrido, en fase `BAN_PICK`. Se contrastaron esos campos con los tipos del [proyecto de referencia](https://github.com/vigovlugt/draftgap/blob/e5ffb383388844a7cc2bca928627b1d77df9c195/apps/frontend/src/types/Lcu.ts). Antes de escribir, se comprueban de nuevo partida, acción local, posición, disponibilidad, campeón visible, temporizador y opciones activadas. No confirma con un temporizador ausente, infinito, incompatible o expirado. La latencia, suspensión de la aplicación o fallos del cliente pueden hacer que se pierda el último segundo; no se garantiza la confirmación ni una ejecución exactamente a 1000 ms.

Si cambias manualmente el campeón en League después de que la app observe su preselección, o usas los botones manuales de preselección/confirmación, se pausa la automatización hasta el siguiente turno. Si la preselección no se refleja en el cliente dentro de un segundo también se pausa. Ante un error de envío o una validación final rechazada se pausa y no repite acciones automáticamente. Desactivar las opciones cancela escrituras que aún estén pendientes de la validación final. La desconexión pausa el turno observado. Las opciones se conservan entre aperturas; desactívalas cuando quieras seleccionar manualmente. La confirmación automática requiere preselección automática habilitada.

La preselección automática está cubierta por simulaciones de LCU y pruebas del controlador. El baneo prioritario revalida la acción y las preselecciones aliadas antes del envío; requiere verificación en una selección real de League.

## Verificación

```powershell
dotnet run --project ./DraftOptimizer.Tests
```

El ejecutable de pruebas sin dependencias adicionales comprueba perfiles, ramas/fragmentos, cantidades, persistencia y respaldo, parsing Qwik, identidad de builds, exclusión de campeones, índices con matchup, selección local y preservación de páginas/conjuntos en una simulación HTTP de LCU. Opcionalmente acepta la ruta a una página HTML real de Ahri/middle del parche del catálogo para verificar extracción completa:

```powershell
dotnet run --project ./DraftOptimizer.Tests -- C:/ruta/ahri.html
```

Para verificar además el filtro D2+ sobre una página descargada, añade `D2` como segundo argumento. La suite incluye pruebas de identidad de filas y ausencia de Reset, confianza contextual con muestras débiles/fuertes, precarga y aplicación única al finalizar, cancelación al salir de selección, perfil personalizado completo, perfil solo de objetos y guardas LCU justo antes de cada escritura.

Se verificó extracción y validación de una página real de Lolalytics para Ahri/middle, parche 16.22. La app compiló para Windows x64 sin advertencias ni errores y se abrió su interfaz para inspección visual. **No había un cliente de League conectado**, por lo que seleccionar campeón, importar runas e importar objetos aún requieren una prueba de extremo a extremo con League.

## Referencias y licencia

Referencia de código revisada: DraftGap `main`, commit `e5ffb383388844a7cc2bca928627b1d77df9c195`, consultado desde el repositorio enlazado en la página de releases. El aviso MIT del proyecto de origen se conserva en `LICENSE.DraftGap`. El código nuevo se distribuye bajo MIT en `LICENSE`. Los datos y marcas de terceros pertenecen a sus titulares.

[Riot documenta LCU como un servicio local sin soporte oficial para terceros](https://developer.riotgames.com/docs/lol#league-client-api), por lo que sus endpoints pueden cambiar. Para distribuir una aplicación a otros jugadores, registra el producto en Riot Developer Portal conforme a sus políticas.

DraftOptimizer is not endorsed by Riot Games and does not reflect the views or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot Games and all associated properties are trademarks or registered trademarks of Riot Games, Inc.

## Perfiles, simulación y retratos

Cada perfil muestra el nombre de su campeón en «Mis perfiles». El editor tiene selectores propios de campeón y posición: guardar, importar y consultar Lolalytics usan esos selectores, aunque cambie la selección del lobby.

La pestaña «Simulación» funciona sin League abierto. El orden es azul, rojo, rojo, azul, azul, rojo, rojo, azul, azul, rojo. Elige la posición y un candidato y pulsa «Confirmar pick simulado». Cada equipo ocupa una vez cada posición y no se repiten campeones. «Deshacer» y «Reiniciar» permiten probar variantes. La lista usa el mismo motor del draft real, desde la perspectiva del equipo que elige a continuación; el resumen siempre muestra el WR azul y rojo. Sin contexto de 30 días, se puede simular la selección pero no se inventan métricas contextuales. La sesión simulada no envía selecciones ni configuraciones a Riot.

«Retratos» alterna entre lista y galería tanto en campeones como en simulación. Conserva nombres, orden y selección disponible; las métricas aparecen en el tooltip al pasar el cursor. La galería permite ver más campeones a la vez; la lista sigue siendo preferible para comparar cifras directamente. Los retratos usan el parche del catálogo y las imágenes de Data Dragon documentadas por Riot: https://developer.riotgames.com/docs/lol#data-dragon_champions. Requieren acceso a ese servicio; si no cargan, el nombre y la alternativa de lista siguen disponibles. No se incluyen iconos de runas u objetos en este cambio.

### Corrección de estabilidad y rendimiento de retratos

Campeones y Simulación usan una lista por pestaña, con virtualización en modo fila y distribución en mosaico en modo retrato. Los modos comparten una caché de imágenes limitada a 256 URLs con decodificación de 72px. El simulador solo calcula mientras está activo y agrupa la búsqueda durante 180ms. Sus controles y plantillas se declaran en XAML, sin obtener plantillas de controles de otra pestaña. Las excepciones de interfaz se registran en `%LOCALAPPDATA%/DraftOptimizer/logs/ui-errors.log`.

### Automatización y calidad de la build

- Con «Seguir mi campeón y posición del cliente» activo (por defecto), la lista cambia a la posición asignada por League. La automatización siempre valida la posición local antes de actuar.
- La preselección elige el mejor favorito del rol por confianza del draft; si no hay favoritos en ese rol, usa el mejor candidato disponible. No confirma automáticamente salvo que esté activada la opción de confirmar a 1 segundo.
- El baneo prioritario sigue la lista configurada para la posición local y omite las intenciones de selección aliadas.
- Después de confirmar el campeón, aplica el perfil personal de ese campeón y rol. Si falta, obtiene Lolalytics D2+, Global, Solo/Duo y el parche del catálogo. Si solo existen objetos personalizados, los conserva y completa las runas desde D2+. Cada escritura comprueba de nuevo partida, campeón confirmado y posición; cambiar de campeón o abandonar el draft cancela la operación. Ya no espera al cierre de todos los picks.
- Las builds generadas desde los datos completos de Lolalytics incluyen habilidades para niveles 1, 2 y 3 en el título de inicio y prioridad de maxeo en los primeros tres bloques de objetos. Las configuraciones personalizadas conservan sus títulos.
- Cada posición de compra y las botas se evalúan por separado: límite inferior de Wilson bilateral al 99%, n≥200, alternativas a ≤2 puntos porcentuales del mejor límite inferior. Se importan al menos cuatro alternativas si la fuente ofrece suficientes candidatos. Las muestras menores a 200 completan la lista cuando faltan opciones y aparecen sin valor de confianza. Si Mejai queda entre las alternativas de un objeto, se añade una más cuando está disponible. Los conjuntos iniciales se evalúan como conjuntos completos para conservar cantidades. El WR publicado por Lolalytics está redondeado: los límites son aproximados. Esta comparación describe resultados observados; no corrige el sesgo de objetos situacionales ni demuestra que comprarlos cause más victorias.
- La primera pestaña muestra a la derecha el perfil personal o la configuración automática preparada: campeón, rol, origen, runas, habilidades, objetos y evidencia por posición. «Ver en el editor» abre su configuración.

### Trabajo en segundo plano

El catálogo/contexto se descargan y leen por stream UTF-8; su parseo, el análisis del draft y el parseo de Lolalytics se realizan fuera del hilo UI. Los cálculos usan una copia de la composición, cancelación y comprobación de versión antes de publicar. Los datasets permanecen retenidos hasta que termina el último lector. El estado del controlador automático y las escrituras a Riot siguen seriales y revalidadas.

Los retratos usan archivos PNG en `%LOCALAPPDATA%/DraftOptimizer/cache/portraits`, con descargas compartidas por URL y un máximo de cuatro en paralelo. WPF actualiza las imágenes en el dispatcher con prioridad de fondo. Las filas conservan sus enlaces de imagen al actualizar las estadísticas. La búsqueda principal agrupa cambios durante 160 ms.
