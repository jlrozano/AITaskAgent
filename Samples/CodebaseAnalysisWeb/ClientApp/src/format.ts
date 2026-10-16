// Formateo en español para la UI.

export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) return '—';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const totalSeconds = Math.round(ms / 1000);
  if (totalSeconds < 60) return `${(ms / 1000).toFixed(ms < 10000 ? 1 : 0).replace('.', ',')} s`;
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return hours > 0 ? `${hours} h ${minutes} min` : `${minutes} min ${String(seconds).padStart(2, '0')} s`;
}

const numberFormat = new Intl.NumberFormat('es-ES');
export const formatNumber = (n: number | null | undefined): string =>
  n === null || n === undefined ? '—' : numberFormat.format(n);

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const kb = bytes / 1024;
  if (kb < 1024) return `${kb.toFixed(kb < 10 ? 1 : 0).replace('.', ',')} KB`;
  return `${(kb / 1024).toFixed(1).replace('.', ',')} MB`;
}

const timeFormat = new Intl.DateTimeFormat('es-ES', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const dateTimeFormat = new Intl.DateTimeFormat('es-ES', {
  day: '2-digit',
  month: 'short',
  hour: '2-digit',
  minute: '2-digit',
});

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '—' : timeFormat.format(d);
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '—' : dateTimeFormat.format(d);
}

export function formatCost(usd: number | null | undefined): string {
  if (usd === null || usd === undefined) return '—';
  // Con tres decimales, un coste de fracciones de milésima saldría «0,000 $», que parece gratis.
  if (usd > 0 && usd < 0.001) return '< 0,001 $';
  return `${usd.toFixed(usd < 0.1 ? 3 : 2).replace('.', ',')} $`;
}

/** «2 / 10 USD por millón (entrada / salida)»; null si el modelo no tiene precio. */
export function formatPricePerMillion(input: number | null | undefined, output: number | null | undefined): string | null {
  if (input == null || output == null) return null;
  const n = (v: number) => v.toLocaleString('es-ES', { maximumFractionDigits: 3 });
  return `${n(input)} / ${n(output)} USD por millón de tokens (entrada / salida)`;
}
