# FileToolsTestApp - Documentación

Este proyecto es una aplicación de consola en C# .NET diseñada para interactuar con herramientas de manipulación de archivos y directorios, además de integrarse con servicios de inteligencia artificial (IA) para proporcionar asistencia automatizada.

## Propósito Principal

El proyecto actúa como un **"Agente de IA interactivo"** que permite a los usuarios realizar operaciones avanzadas con archivos y directorios dentro de un directorio autorizado (`projectDir`). Utiliza un modelo de lenguaje (LLM) para interpretar comandos del usuario y ejecutar acciones específicas mediante herramientas integradas.

## Componentes Clave

### 1. Configuración Inicial (`Main`)
- **Directorio del Proyecto**: Busca el directorio raíz del proyecto usando `FindProjectDir`.
- **Registro de Logs**: Configura Serilog para guardar logs en un archivo (`app.log`) dentro de la carpeta `logs`.
- **Configuración de la Aplicación**: Carga configuraciones desde `appsettings.json` y `appsettings.Development.json`, incluyendo claves de APIs para servicios externos (como OpenRouter para el modelo de IA).

### 2. Servicios y Herramientas
- **Inyección de Dependencias (DI)**:
  - Registra servicios de IA (`AITaskAgent`), herramientas de archivos (`FileTools`), y un servicio de modelo de lenguaje (`OpenAILlmService`).
  - Configura un proveedor de herramientas MCP (`SharpTools`) si está disponible.
- **Herramientas Disponibles**:
  - `FileTools`: Para operaciones con archivos y directorios.
  - `MCP Tools`: Herramientas externas integradas mediante MCP (Microservice Communication Protocol).

### 3. Agente de IA
- **Prompt del Agente**:
  - El agente está configurado para operar exclusivamente dentro del directorio autorizado.
  - Responde en español y utiliza herramientas disponibles para ayudar al usuario.
- **Interacción con el Usuario**:
  - El usuario puede ingresar comandos en la consola, y el agente responde utilizando el modelo de IA (`DeepSeek` en este caso).
  - Ejemplo de comandos: buscar archivos, crear directorios, leer/escribir archivos, etc.

### 4. Eventos y Logs
- **Registro de Eventos**: Los eventos generados durante la ejecución se guardan en un archivo JSON (`events.jsonl`).
- **Manejo de Errores**: Errores críticos se registran en los logs y se muestran al usuario.

## Flujo de Ejecución

1. **Inicialización**:
   - Configura logs, carga configuraciones y registra servicios.
   - Conecta con proveedores MCP (si existen).
2. **Bucle de Interacción**:
   - El usuario escribe comandos en la consola.
   - El agente procesa el comando usando el modelo de IA y las herramientas disponibles.
   - Muestra resultados o errores en la consola.
3. **Finalización**:
   - Desconecta proveedores MCP.
   - Cierra logs y libera recursos.

## Archivos de Configuración

- **`appsettings.json`**:
  - Define el proveedor de IA (`DeepSeek` en OpenRouter) y parámetros como `Temperature` y `MaxTokens`.
- **`appsettings.Development.json`**:
  - Sobrescribe la clave de API para el entorno de desarrollo.

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
   - Los logs se guardan en `logs/app.log`.
   - Los eventos se registran en `logs/events.jsonl`.

## Conclusión

El proyecto es una **herramienta de automatización y asistencia** que combina:
- Manipulación de archivos/directorios.
- Integración con modelos de IA para interpretación de comandos.
- Registro detallado de eventos y logs.

Para más detalles, consulta los archivos del proyecto o ejecuta la aplicación para interactuar con el agente.