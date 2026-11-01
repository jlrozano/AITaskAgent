// Textos de la interfaz. Por defecto en español; el anfitrión puede pasar `labels` parciales.
export interface Labels {
  placeholder: string;
  send: string;
  stop: string;
  thinking: string;
  toolRunning: string;
  apply: string;
  discard: string;
  applied: string;
  discarded: string;
  conflict: string;
  failed: string;
  approve: string;
  reject: string;
  answer: string;
  otherAnswer: string;
  cancelled: string;
  retryable: string;
  tokensIn: string;
  tokensOut: string;
  added: string;
  changed: string;
  removed: string;
  empty: string;
  retry: string;
  /** Encabezado del bloque de código que sustituye a un diagrama Mermaid inválido. */
  mermaidError: string;
  step: string;
  steps: string;
  stepsWithErrors: string;
  attachments: string;
  agent: string;
  agents: string;
  /** Resumen del panel al terminar: «Completado · 3 acciones · 4,2 s» (sin ceros). */
  done: string;
  countAction: string;
  countActions: string;
  /** Nombre accesible (aria-label y title) del icono de cada tipo de nodo; sustituye a una etiqueta de clase visible. */
  iconStep: string;
  iconCall: string;
  /** Resumen del panel mientras corre: «En curso: <nodo>». */
  nowRunning: string;
  /** @deprecated Sin uso: las herramientas van intercaladas en la caja del paso, sin bloque plegable «Llamadas». */
  toolsToggle: string;
  /** «intento 2» junto al nombre de un agente reintentado. */
  attempt: string;
  /** Botón de solo icono que abre el inspector de la herramienta (`toolInspector`): aria-label y title. */
  toolInspect: string;
  /** Título del inspector de herramienta. */
  toolInspectorTitle: string;
  /** Inspector: nombre técnico de la herramienta y textos de presentación de inicio y fin (`display`). */
  toolTechName: string;
  toolDisplayStart: string;
  toolDisplayEnd: string;
  toolInput: string;
  toolOutput: string;
  toolNoInput: string;
  toolNoOutput: string;
  /** Salida todavía no disponible (la herramienta sigue en curso). */
  toolOutputPending: string;
  toolOutputTruncated: string;
  /** La herramienta falló y no dejó mensaje. */
  toolFailed: string;
  copy: string;
  copied: string;
  close: string;
  jsonExpand: string;
  jsonCollapse: string;
  jsonKeys: string;
  jsonItems: string;
  /**
   * @deprecated La interfaz ya no pinta clases («Agente», «Herramienta», «Paso»): cada nodo se titula con su `display`.
   * Estas claves se ignoran; se conservan solo para que un anfitrión que las pase siga compilando.
   */
  classAgent?: string;
  /** @deprecated Ver `classAgent`. */
  classStep?: string;
  /** @deprecated Ver `classAgent`. */
  classTool?: string;
  /** @deprecated Ver `classAgent`: el recuento por clase se sustituyó por `countAction(s)`. */
  countAgent?: string;
  /** @deprecated Ver `countAgent`. */
  countAgents?: string;
  /** @deprecated Ver `countAgent`. */
  countStep?: string;
  /** @deprecated Ver `countAgent`. */
  countSteps?: string;
  /** @deprecated Ver `countAgent`. */
  countTool?: string;
  /** @deprecated Ver `countAgent`. */
  countTools?: string;
}

export const defaultLabels: Labels = {
  placeholder: "Escribe un mensaje…",
  send: "Enviar",
  stop: "Detener",
  thinking: "Razonamiento",
  toolRunning: "en curso…",
  apply: "Aplicar",
  discard: "Descartar",
  applied: "Aplicada",
  discarded: "Descartada",
  conflict: "Conflicto: el documento cambió; repite el turno.",
  failed: "No se pudo aplicar.",
  approve: "Aprobar",
  reject: "Rechazar",
  answer: "Responder",
  otherAnswer: "Otra respuesta…",
  cancelled: "Cancelado",
  retryable: "Se puede reintentar.",
  tokensIn: "entrada",
  tokensOut: "salida",
  added: "Añadido",
  changed: "Cambiado",
  removed: "Quitado",
  empty: "Sin mensajes todavía.",
  retry: "Reintentar",
  mermaidError: "Diagrama no válido",
  step: "paso",
  steps: "pasos",
  stepsWithErrors: "con error",
  attachments: "Adjuntos del mensaje",
  agent: "agente",
  agents: "agentes",
  attempt: "intento",
  done: "Completado",
  countAction: "acción",
  countActions: "acciones",
  iconStep: "Paso",
  iconCall: "Llamada",
  nowRunning: "En curso",
  toolsToggle: "Llamadas",
  toolInspect: "Ver entrada y salida",
  toolInspectorTitle: "Detalle",
  toolTechName: "Nombre técnico",
  toolDisplayStart: "Al empezar",
  toolDisplayEnd: "Al terminar",
  toolInput: "Entrada",
  toolOutput: "Salida",
  toolNoInput: "Sin entrada.",
  toolNoOutput: "Sin salida.",
  toolOutputPending: "en curso…",
  toolOutputTruncated: "Salida recortada: el servidor solo envía los primeros 16384 caracteres.",
  toolFailed: "La herramienta falló.",
  copy: "Copiar",
  copied: "Copiado",
  close: "Cerrar",
  jsonExpand: "Desplegar",
  jsonCollapse: "Plegar",
  jsonKeys: "claves",
  jsonItems: "elementos",
};
