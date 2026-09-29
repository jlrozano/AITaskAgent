Por favor analiza el siguiente conjunto de archivos del repositorio para extraer la semántica de la aplicación y generar los nodos y aristas de enriquecimiento.

### Archivos a analizar:
```
{{ files_batch }}
```

### Contexto del Proyecto:
- Lenguaje/Ecosistema: `{{ project_language }}`
- Frameworks detectados: `{{ frameworks_detected }}`

### Formato de salida requerido (JSON):
```json
{
  "nodes": [
    {
      "label": "Route | DiRegistration | Flow | Interface | ConfigClass",
      "name": "Nombre visible",
      "qualified_name": "Nombre cualificado con prefijo reglamentario",
      "file": "Ruta relativa del archivo",
      "line": 10,
      "properties": {
        "presentational": true,
        "auth_required": true,
        "auth_roles": ["admin"],
        "http_verb": "POST",
        "route_path": "/api/v1/orders"
      }
    }
  ],
  "edges": [
    {
      "source_qualified_name": "Origen",
      "target_qualified_name": "Destino",
      "type": "HANDLES | IMPLEMENTS | DI_RESOLVES | FLOW_ENTRY | FLOW_NEXT",
      "properties": {
        "presentational": true
      }
    }
  ],
  "node_properties_updates": [
    {
      "qualified_name": "ClaseExistente.MetodoExistente",
      "properties": {
        "is_async": true,
        "accessibility": "public",
        "docstring": "Descripción funcional breve"
      }
    }
  ]
}
```
Produce únicamente el JSON.
