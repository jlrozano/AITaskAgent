import { useState } from 'react';
import { ApiError, api, errorMessage } from '../api/client';
import type { KnowledgeState, KnowledgeStatusDto, SourceRefDto } from '../api/types';
import { formatNumber } from '../format';
import { useWorkspace } from '../state/workspace';
import { StatusBanner } from './StatusBanner';

interface Props {
  source: SourceRefDto;
  knowledge: KnowledgeState;
  /** Respuesta de la operación: el estado nuevo de la fuente. */
  onChange: (status: KnowledgeStatusDto) => void;
  analysisRunning: boolean;
  /** Turno de chat en curso en esta pestaña (el de otra ventana llega como 409). */
  chatBusy: boolean;
}

type Tone = 'on' | 'warn' | 'off';
type Op = 'graph:add' | 'graph:remove' | 'emb:add' | 'emb:remove';

const OP_TEXT: Record<Op, string> = {
  'graph:add': 'Añadiendo al grafo…',
  'graph:remove': 'Quitando del grafo…',
  'emb:add': 'Indexando embeddings…',
  'emb:remove': 'Quitando embeddings…',
};

export function graphStateText(k: KnowledgeState['graph']): { text: string; tone: Tone } {
  switch (k.state) {
    case 'present':
      return { text: `En el grafo · ${formatNumber(k.nodeCount)} nodos · ${formatNumber(k.edgeCount)} aristas`, tone: 'on' };
    case 'stale':
      return { text: 'Desactualizado en el grafo: el contenido ha cambiado', tone: 'warn' };
    case 'pending':
      return { text: 'Pendiente de reincorporar al grafo', tone: 'warn' };
    default:
      return { text: 'No está en el grafo', tone: 'off' };
  }
}

export function embeddingsStateText(k: KnowledgeState['embeddings']): { text: string; tone: Tone } {
  switch (k.state) {
    case 'indexed':
      return { text: `Indexado · ${formatNumber(k.chunkCount)} trozos`, tone: 'on' };
    case 'outdated':
      return { text: 'Desactualizado: el contenido ha cambiado desde que se indexó', tone: 'warn' };
    case 'model_mismatch':
      return { text: `Indexado con otro modelo${k.model ? ` (${k.model})` : ''}`, tone: 'warn' };
    default:
      return { text: 'No indexado', tone: 'off' };
  }
}

/**
 * «Añadir al grafo»/«Quitar del grafo» y «Añadir a embeddings»/«Actualizar»/«Quitar» de una fuente (V2-D, plan v2
 * §8.3). Se deshabilitan con el motivo según la matriz de §5.5 y `features.embeddings`; un 409 que llegue igual
 * (otra ventana, otra operación) se muestra como aviso, no como error.
 */
export function KnowledgeActions({ source, knowledge, onChange, analysisRunning, chatBusy }: Props) {
  const { workspace, meta } = useWorkspace();
  const [op, setOp] = useState<Op | null>(null);
  const [confirm, setConfirm] = useState<'graph' | 'emb' | null>(null);
  const [notice, setNotice] = useState<{ tone: 'warning' | 'error'; text: string } | null>(null);

  const embeddings = meta?.features.embeddings;
  const graphReason = !workspace?.hasGraph
    ? 'Este repositorio aún no tiene grafo: lanza un análisis antes.'
    : analysisRunning
      ? 'Hay un análisis en curso.'
      : chatBusy
        ? 'Hay una respuesta del chat en curso.'
        : null;
  const embReason = !embeddings?.enabled
    ? (embeddings?.disabledReason ?? 'Los embeddings no están disponibles.')
    : source.kind === 'report' && analysisRunning
      ? 'Hay un análisis en curso: reescribe los informes del catálogo.'
      : null;
  const busyReason = op ? 'Hay una operación en curso.' : null;

  const run = async (next: Op, call: () => Promise<KnowledgeStatusDto>) => {
    setConfirm(null);
    setNotice(null);
    setOp(next);
    try {
      onChange(await call());
    } catch (e) {
      const conflict = e instanceof ApiError && e.status === 409;
      setNotice({ tone: conflict ? 'warning' : 'error', text: errorMessage(e) });
    } finally {
      setOp(null);
    }
  };

  const g = knowledge.graph;
  const em = knowledge.embeddings;
  const gState = graphStateText(g);
  const eState = embeddingsStateText(em);
  const inGraph = g.state !== 'absent';
  const indexed = em.state !== 'absent';

  const button = (
    label: string,
    next: Op,
    call: () => Promise<KnowledgeStatusDto>,
    reason: string | null,
    variant = '',
  ) => (
    <button
      type="button"
      className={`btn btn--small${variant}`}
      disabled={!!(busyReason ?? reason)}
      title={busyReason ?? reason ?? undefined}
      onClick={() => void run(next, call)}
    >
      {op === next ? OP_TEXT[next] : label}
    </button>
  );

  const removeButton = (group: 'graph' | 'emb', label: string, reason: string | null) =>
    confirm === group ? (
      <span className="knowledge__confirm">
        {group === 'graph'
          ? button('Sí, quitar', 'graph:remove', () => api.removeFromGraph(source), reason, ' btn--danger')
          : button('Sí, quitar', 'emb:remove', () => api.removeEmbeddings(source), reason, ' btn--danger')}
        <button type="button" className="btn btn--small btn--ghost" onClick={() => setConfirm(null)}>
          No
        </button>
      </span>
    ) : (
      <button
        type="button"
        className="btn btn--small btn--ghost"
        disabled={!!(busyReason ?? reason)}
        title={busyReason ?? reason ?? undefined}
        onClick={() => setConfirm(group)}
      >
        {op === (group === 'graph' ? 'graph:remove' : 'emb:remove') ? OP_TEXT[group === 'graph' ? 'graph:remove' : 'emb:remove'] : label}
      </button>
    );

  // Quitar embeddings no depende de que la función esté activa (se limpia lo indexado con otra configuración) y §5.5
  // no lo restringe con un análisis en curso: solo lo impide otra operación de conocimiento, que llega como 409.
  const embRemoveReason = null;

  return (
    <section className="knowledge" aria-label="Grafo y embeddings" aria-busy={op !== null}>
      <div className="knowledge__group">
        <span className="knowledge__name">Grafo</span>
        <span className={`knowledge__state knowledge__state--${gState.tone}`}>
          <span className="knowledge__dot" aria-hidden="true" />
          {gState.text}
        </span>
        <span className="knowledge__actions">
          {!inGraph && button('Añadir al grafo', 'graph:add', () => api.addToGraph(source), graphReason, ' btn--primary')}
          {inGraph && g.state !== 'present' && button('Actualizar', 'graph:add', () => api.addToGraph(source), graphReason)}
          {inGraph && removeButton('graph', 'Quitar del grafo', graphReason)}
        </span>
        {graphReason && <span className="knowledge__reason">{graphReason}</span>}
      </div>
      <div className="knowledge__group">
        <span className="knowledge__name">Embeddings</span>
        <span className={`knowledge__state knowledge__state--${eState.tone}`}>
          <span className="knowledge__dot" aria-hidden="true" />
          {eState.text}
        </span>
        <span className="knowledge__actions">
          {!indexed && button('Añadir a embeddings', 'emb:add', () => api.indexEmbeddings(source), embReason, ' btn--primary')}
          {indexed && em.state !== 'indexed' && button('Actualizar', 'emb:add', () => api.indexEmbeddings(source), embReason)}
          {indexed && removeButton('emb', 'Quitar de embeddings', embRemoveReason)}
        </span>
        {embReason && !indexed && <span className="knowledge__reason">{embReason}</span>}
      </div>
      {notice && (
        <StatusBanner tone={notice.tone} onDismiss={() => setNotice(null)}>
          {notice.text}
        </StatusBanner>
      )}
    </section>
  );
}

/** Miniestado de la lista: solo lo que no está ausente, para no llenar cada entrada de ruido. */
export function KnowledgeMini({ knowledge }: { knowledge: KnowledgeState | null | undefined }) {
  if (!knowledge) return null;
  const g = knowledge.graph;
  const em = knowledge.embeddings;
  const gTone = g.state === 'present' ? 'on' : 'warn';
  const eTone = em.state === 'indexed' ? 'on' : 'warn';
  if (g.state === 'absent' && em.state === 'absent') return null;
  return (
    <span className="kmini">
      {g.state !== 'absent' && (
        <span className={`kmini__tag kmini__tag--${gTone}`} title={graphStateText(g).text}>
          grafo
          <span className="visually-hidden">: {graphStateText(g).text}</span>
        </span>
      )}
      {em.state !== 'absent' && (
        <span className={`kmini__tag kmini__tag--${eTone}`} title={embeddingsStateText(em).text}>
          emb
          <span className="visually-hidden">: {embeddingsStateText(em).text}</span>
        </span>
      )}
    </span>
  );
}
