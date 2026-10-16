import { useId } from 'react';
import type { ChatModelDto } from '../api/types';
import { formatPricePerMillion } from '../format';

interface Props {
  models: ChatModelDto[];
  value: string | null;
  /** Deshabilitado mientras hay un turno en curso: el modelo vale para los turnos siguientes. */
  disabled: boolean;
  onChange: (modelId: string) => void;
}

/** Desplegable del modelo que responde. Sin catálogo (servidor sin selector) no se pinta. */
export function ChatModelSelect({ models, value, disabled, onChange }: Props) {
  const id = useId();
  if (models.length === 0 || value === null) return null;
  const current = models.find((m) => m.id === value);
  const price = current ? formatPricePerMillion(current.inputPricePerMillion, current.outputPricePerMillion) : null;
  const title = [
    current ? `${current.provider} · ${current.model}` : null,
    price,
    current && !current.streaming ? 'Este modelo suelta la respuesta de golpe, sin ir goteando' : null,
    disabled ? 'Disponible cuando termine la respuesta en curso' : null,
  ]
    .filter(Boolean)
    .join(' · ');
  return (
    <div className="model-select" title={title || undefined}>
      <label htmlFor={id} className="model-select__label">
        Modelo
      </label>
      <select
        id={id}
        aria-label="Modelo del asistente"
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
      >
        {models.map((m) => (
          <option key={m.id} value={m.id}>
            {m.streaming ? m.label : `${m.label} · sin streaming`}
          </option>
        ))}
      </select>
    </div>
  );
}
