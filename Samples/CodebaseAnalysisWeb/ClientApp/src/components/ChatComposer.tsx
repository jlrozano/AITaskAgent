import { useId, useState, type FormEvent, type KeyboardEvent, type ReactNode } from 'react';

export const MAX_MESSAGE_LENGTH = 8000;

interface Props {
  busy: boolean;
  /** Motivo por el que no se puede preguntar (análisis en curso, sin workspace), o null. */
  disabledReason: string | null;
  onSend: (text: string) => void;
  onStop: () => void;
  /** Selector de modelo, a la izquierda de la barra. */
  modelPicker?: ReactNode;
}

/** Enter envía, Shift+Enter hace salto de línea; «Parar» aborta la respuesta en curso (§7). */
export function ChatComposer({ busy, disabledReason, onSend, onStop, modelPicker }: Props) {
  const [text, setText] = useState('');
  const hintId = useId();
  const disabled = disabledReason !== null;
  const tooLong = text.length > MAX_MESSAGE_LENGTH;
  const canSend = !disabled && !busy && text.trim().length > 0 && !tooLong;

  const submit = (e?: FormEvent) => {
    e?.preventDefault();
    if (!canSend) return;
    onSend(text);
    setText('');
  };

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      submit();
    }
  };

  return (
    <form className="composer" onSubmit={submit} aria-label="Enviar pregunta">
      <label className="visually-hidden" htmlFor={`${hintId}-input`}>
        Pregunta
      </label>
      <textarea
        id={`${hintId}-input`}
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKeyDown}
        placeholder={disabled ? '' : '¿Dónde se valida la entrada de…?'}
        rows={3}
        disabled={disabled}
        aria-describedby={hintId}
        aria-invalid={tooLong || undefined}
      />
      <div className="composer__bar">
        {modelPicker}
        <span id={hintId} className={`composer__hint${tooLong ? ' text-error' : ''}`}>
          {tooLong
            ? `La pregunta supera los ${MAX_MESSAGE_LENGTH} caracteres (${text.length}).`
            : 'Enter envía · Mayús+Enter salto de línea'}
        </span>
        {busy ? (
          <button type="button" className="btn btn--danger" onClick={onStop}>
            Parar
          </button>
        ) : (
          <button type="submit" className="btn btn--primary" disabled={!canSend}>
            Enviar
          </button>
        )}
      </div>
    </form>
  );
}
