# FileToolsTestApp - Documentación

Este proyecto es una aplicación de consola en C# (.NET 10, `net10.0`) diseñada para interactuar con herramientas de manipulación de archivos y directorios, además de integrarse con servicios de inteligencia artificial (IA) para proporcionar asistencia automatizada.

## Propósito Principal

El proyecto actúa como un **"Agente de IA interactivo"** que permite a los usuarios realizar operaciones avanzadas con archivos y directorios dentro de los directorios autorizados (al arrancar, el directorio del proyecto; se pueden añadir más en tiempo de ejecución). Utiliza un modelo de lenguaje (LLM) para interpretar comandos del usuario y ejecutar acciones específicas mediante herramientas integradas.

## Componentes Clave

### 1. Configuración Inicial (`Main`)
- **Directorio del Proyecto**: Parte de `AppContext.BaseDirectory` y, si la ruta contiene `bin`, sube tres niveles hasta la carpeta del proyecto.
- **Registro de Logs**: Configura Serilog (nivel `Verbose`) para guardar un fichero por ejecución, `logs/trace_<yyyyMMdd_HHmmss>.log`; los de ejecuciones anteriores se conservan.
- **Configuración de la Aplicación**: Carga `appsettings.json`, `appsettings.{DOTNET_ENVIRONMENT}.json` (por defecto `Development`, opcional) y variables de entorno. Los `${VAR}` de las claves de API se expanden desde variables de entorno.

### 2. Servicios y Herramientas
- **Inyección de Dependencias (DI)**:
  - Registra los servicios del framework (`AddAITaskAgent`), las herramientas de archivos (`AddFileTools(projectDir)`) y cuatro servicios LLM: `OpenAILlmService`, `AzureOpenAILlmService`, `OpenRouterLlmService` y `GeminiLlmService`.
- **Herramientas Disponibles**:
  - `FileTools`: Para operaciones con archivos y directorios (todas salvo `write_to_file`).
  - Escritura de ficheros mediante el tag `<write_file>` en la respuesta del modelo (`WriteFileTagHandler`), no como tool.

### 3. Agente de IA
- **Prompt del Agente**:
  - El agente está configurado para operar exclusivamente dentro del directorio autorizado.
  - Responde en español y utiliza herramientas disponibles para ayudar al usuario.
- **Interacción con el Usuario**:
  - El usuario puede ingresar comandos en la consola, y el agente responde con el rol LLM elegido al arrancar: el que se pase como argumento, o el que se elija en un menú si hay varios roles (Enter = `DefaultRole`).
  - Ejemplo de comandos: buscar archivos, crear directorios, leer/escribir archivos, etc.
  - Comandos de la consola: `/setMainPath <ruta>`, `/addFolder <ruta>`, `/folders`, `salir` / `exit`.

### 4. Eventos y Logs
- **Registro de Eventos**: Los eventos del `IEventChannel` (tags, streaming de la respuesta, herramientas y progreso) se muestran en la consola; no se guardan en un fichero aparte.
- **Manejo de Errores**: Errores críticos se registran en los logs y se muestran al usuario.

## Flujo de Ejecución

1. **Inicialización**:
   - Configura logs, carga configuraciones y registra servicios.
   - Elige el rol LLM.
2. **Bucle de Interacción**:
   - El usuario escribe comandos en la consola.
   - El agente procesa el comando usando el modelo de IA y las herramientas disponibles.
   - Muestra resultados o errores en la consola.
3. **Finalización**:
   - Cierra logs (`Log.CloseAndFlush`).

## Archivos de Configuración

- **`appsettings.json`**:
  - Define tres proveedores (`OpenRouterBase`, `Gemini`, `AzureOpenAI`) y tres roles (`default` → OpenRouter `nvidia/nemotron-3-super-120b-a12b:free`, `gemini` → `gemini-2.5-flash`, `azure` → `gpt-5.4`), con `DefaultRole: azure` y parámetros como `Temperature` y `MaxTokens`.
- **`appsettings.Development.json`** (local, excluido de git):
  - Sobrescribe las claves de API (y el endpoint de Azure) para el entorno de desarrollo.

## Ejemplo de Uso

1. **Inicio**:
   ```bash
   dotnet run
   ```
2. **Interacción**:
   ```
   [Usuario]: Lista los archivos en el directorio actual.
   [Agente]: Los archivos en el directorio son: Program.cs, appsettings.json, ...
   ```
3. **Salida**:
   - Los logs se guardan en `logs/trace_<yyyyMMdd_HHmmss>.log`.

## Conclusión

El proyecto es una **herramienta de automatización y asistencia** que combina:
- Manipulación de archivos/directorios.
- Integración con modelos de IA para interpretación de comandos.
- Registro detallado de eventos y logs.

Para más detalles, consulta los archivos del proyecto o ejecuta la aplicación para interactuar con el agente.