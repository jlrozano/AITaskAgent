// Utilidades de las pruebas: validadores del esquema chat-protocol/1 y lectura de un turno del mock.
import Ajv2020 from "ajv/dist/2020.js";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { ServerEvent } from "../src/protocol.ts";
import { SseParser } from "../src/sse.ts";

const schema = JSON.parse(readFileSync(resolve(import.meta.dirname, "../schema/chat-protocol.schema.json"), "utf8"));
const ajv = new Ajv2020({ allErrors: true, strict: true, strictRequired: false });
ajv.addKeyword("x-protocol");
ajv.addSchema(schema);

const at = (def: string) => {
  const v = ajv.getSchema(`${schema.$id}#/$defs/${def}`);
  if (!v) throw new Error(`Falta $defs/${def}`);
  return v;
};

export const validateServerEvent = at("ServerEvent");
export const validateClientRequest = at("ClientRequest");

export function errorsOf(v: { errors?: unknown }): string {
  return ajv.errorsText(v.errors as never);
}

export interface RawEvent { name: string; id?: string; data: unknown }

/** Lee un turno entero del mock con fetch (sin el cliente), para comprobar lo que sale del servidor. */
export async function readTurn(base: string, body: unknown): Promise<RawEvent[]> {
  const res = await fetch(`${base}/messages`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  if (res.headers.get("content-type")?.startsWith("text/event-stream") !== true) throw new Error(`HTTP ${res.status}`);
  const parser = new SseParser();
  const out: RawEvent[] = [];
  const decoder = new TextDecoder();
  for await (const chunk of res.body as unknown as AsyncIterable<Uint8Array>) {
    for (const e of parser.push(decoder.decode(chunk, { stream: true }))) out.push({ name: e.event, ...(e.id ? { id: e.id } : {}), data: e.data });
  }
  for (const e of parser.end()) out.push({ name: e.event, ...(e.id ? { id: e.id } : {}), data: e.data });
  return out;
}

export const isFuture = (e: RawEvent) => (e.data as { type?: string }).type === "futuro.evento";
export const asEvents = (raw: RawEvent[]) => raw.filter((e) => !isFuture(e)).map((e) => e.data as ServerEvent);
