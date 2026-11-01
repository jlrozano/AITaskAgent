// Carga diferida y OPCIONAL de @streamdown/mermaid (peerDependency opcional). Va en un punto de entrada aparte
// (`@aitaskagent/chat/mermaid`) para que un anfitrión sin el paquete no lo resuelva nunca: index.ts no lo importa.
import type { DiagramPlugin } from "streamdown";

export interface MermaidLoadOptions {
  /** Configuración extra de Mermaid. `securityLevel` se fuerza siempre a "strict" (el diagrama viene de un modelo). */
  config?: Record<string, unknown>;
}

/** Importa @streamdown/mermaid y devuelve el plugin con `securityLevel: "strict"`; `null` si el paquete no está instalado. */
export async function loadMermaidPlugin(options: MermaidLoadOptions = {}): Promise<DiagramPlugin | null> {
  try {
    const m = await import("@streamdown/mermaid");
    return m.createMermaidPlugin({ config: { ...options.config, securityLevel: "strict" } }) as unknown as DiagramPlugin;
  } catch {
    return null;
  }
}
