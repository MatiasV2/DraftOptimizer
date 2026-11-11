# Referencia técnica

- Proyecto: https://github.com/vigovlugt/draftgap
- Releases indicadas por el usuario: https://github.com/vigovlugt/draftgap/releases
- Commit revisado de main: `e5ffb383388844a7cc2bca928627b1d77df9c195`.
- Se consultaron el descubrimiento LCU en `apps/frontend/src-tauri/src/main.rs`, los modelos `packages/core/src/models/dataset`, la carga de datasets en `DatasetContext.tsx` y la extracción Qwik de `apps/dataset/src/lolalytics/qwik.ts`.
- Se adaptó la extracción de referencias JSON Qwik a C#. La interfaz, persistencia de perfiles, validación, importaciones LCU e índice estadístico se implementaron para esta aplicación.
- Se portó el análisis dinámico de `packages/core/src/draft/analysis.ts`, la conversión de `packages/core/src/rating/ratings.ts` y la selección de roles más probable de `packages/core/src/role/role-predictor.ts`. Se utiliza riesgo medio (1000 partidas previas). Las posiciones desconocidas sin ningún dato de frecuencias tienen una asignación neutral determinista como protección adicional.
- Se ejecutaron las funciones TypeScript originales sobre los datasets públicos de parche actual y 30 días para producir los resultados de referencia en `DraftOptimizer.Tests/Fixtures/DraftGapParity.json`. Se eliminaron únicamente campeones y pares que no participaban en esos escenarios. El código C# coincide con las cuatro estimaciones con tolerancia `1e-10`.
- Se incluye el aviso MIT del origen en `LICENSE.DraftGap`.
- La incertidumbre propagada del draft y su límite conservador de ranking son propios; no forman parte de la estimación puntual del código original. Se verificó que las cuatro estimaciones puntuales de referencia permanecen iguales.
- Builds y runas automáticas usan la página pública de Lolalytics con filtro `tier=d2_plus`, Global, Solo/Duo y parche/posición solicitados. Se contrastó con una página real de Ahri/middle D2+ del parche 16.22 y se verifica el objeto de filtros Qwik de la respuesta.
