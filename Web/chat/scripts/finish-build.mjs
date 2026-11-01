// Tras `vite build` y `tsc -p tsconfig.build.json`: deja dist/ listo para empaquetar.
//  - los .d.ts conservan los imports relativos con extensión .ts (allowImportingTsExtensions): se reescriben a .js,
//    que es lo que resuelve cualquier consumidor;
//  - chat.css se copia tal cual (el build de biblioteca no lo emite porque ningún módulo lo importa).
import { copyFileSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const root = new URL("..", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
const types = join(root, "dist", "types");

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) yield* walk(full);
    else if (full.endsWith(".d.ts")) yield full;
  }
}

let changed = 0;
for (const file of walk(types)) {
  const before = readFileSync(file, "utf8");
  const after = before.replace(/(from\s+["']\.{1,2}\/[^"']*?)\.tsx?(["'])/g, "$1.js$2").replace(/(import\(["']\.{1,2}\/[^"']*?)\.tsx?(["']\))/g, "$1.js$2");
  if (after !== before) { writeFileSync(file, after); changed++; }
}
copyFileSync(join(root, "src", "chat.css"), join(root, "dist", "chat.css"));
console.log(`finish-build: ${changed} .d.ts reescritos, chat.css copiado`);
