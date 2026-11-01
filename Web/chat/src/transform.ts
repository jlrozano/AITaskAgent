// Hook previo `transformEvent`: se aplica a cada evento del servidor antes del reductor. Lo usan `ChatClient` (opción y
// parámetro de `send`) y `useChat`; es una función pura de aquí para fuera, sin React.
import { isServerEvent, type ServerEvent } from "./protocol.ts";

/**
 * Recibe una copia profunda (`structuredClone`) del mismo JSON que envía el servidor. Devolver un evento (modificado o no)
 * lo deja pasar; `null` lo descarta; `undefined` = sin cambios (se usa el evento original, aunque el hook haya mutado la copia).
 */
export type TransformEvent = (event: ServerEvent) => ServerEvent | null | undefined;

function clone<T>(x: T): T {
  return typeof structuredClone === "function" ? structuredClone(x) : (JSON.parse(JSON.stringify(x)) as T);
}

/**
 * Aplica el hook a un evento y devuelve lo que debe llegar al reductor (`null` = nada). Nunca lanza:
 * - si el hook lanza, el evento pasa intacto y el error se registra con `console.error`;
 * - un resultado que no es un evento del protocolo (sin `type` conocido) se ignora: pasa el original (`console.error`);
 * - `done` es intocable: no se descarta ni se convierte en otro tipo (pasa el original, `console.warn`), porque sin él el turno
 *   queda «en curso» para siempre; un `done` modificado pero aún `done` sí se respeta;
 * - descartar `error` o `turn.start` se permite pero se avisa con `console.warn` (el primero oculta un fallo real; el segundo
 *   deja al mensaje sin `turnId` ni modelo).
 */
export function applyTransform(hook: TransformEvent | undefined, event: ServerEvent): ServerEvent | null {
  if (!hook) return event;
  let result: ServerEvent | null | undefined;
  try {
    result = hook(clone(event));
  } catch (e) {
    console.error(`[chat] transformEvent lanzó con un evento «${event.type}»; pasa sin modificar.`, e);
    return event;
  }
  if (result === undefined) return event;
  if (result === null) {
    if (event.type === "done") {
      console.warn("[chat] transformEvent intentó descartar `done`: se ignora (el turno no se cerraría).");
      return event;
    }
    if (event.type === "error" || event.type === "turn.start") console.warn(`[chat] transformEvent descarta un evento «${event.type}»: no se recomienda.`);
    return null;
  }
  if (!isServerEvent(result)) {
    console.error(`[chat] transformEvent devolvió algo que no es un evento del protocolo para «${event.type}»; pasa sin modificar.`);
    return event;
  }
  if (event.type === "done" && result.type !== "done") {
    console.warn(`[chat] transformEvent convirtió \`done\` en «${result.type}»: se ignora.`);
    return event;
  }
  return result;
}
