Este es el paquete del lote. Ya lo leíste una vez; tu lectura tiene huecos o errores que hay que corregir.

{{ input.packet }}

---

### Lo que ya es válido (no lo repitas)

{{ input.valid }}

### Lo que falta o está mal

{{ input.errors }}

### Qué devuelves

Un objeto JSON con la misma estructura de tu mensaje de sistema que contenga **solo** lo que falta o se corrige:

- los ficheros, tipos y miembros que faltan (un tipo ya válido al que le faltan miembros se repite con su `name` y su `path` y solo los miembros que faltan);
- los veredictos de las semillas que faltan;
- los hallazgos con error, corregidos y con el mismo `localId` (si no puedes anclarlos con una cita literal que exista, omítelos);
- nada de lo que ya es válido.

Responde solo con el objeto JSON.
