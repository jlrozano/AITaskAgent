import { useState } from "react";
import type { QuestionItem } from "../protocol.ts";
import type { RendererProps } from "../registry.ts";
import type { QuestionPart as Part } from "../state.ts";

function itemsOf(q: Part["question"]): QuestionItem[] {
  if (q.questions && q.questions.length > 0) return q.questions;
  return [{ question: q.question, ...(q.header ? { header: q.header } : {}), multiSelect: q.multiSelect === true, options: (q.options ?? []).map((label) => ({ label })) }];
}

/** Pregunta del asistente: aprobación (Aprobar/Rechazar), opciones (una o varias preguntas) o texto libre. La respuesta cierra el turno anterior y entra como mensaje nuevo. */
export function QuestionPart({ part, ctx }: RendererProps<Part>) {
  const q = part.question;
  const l = ctx.labels;
  const items = itemsOf(q);
  const [picked, setPicked] = useState<string[][]>(() => items.map(() => []));
  const [text, setText] = useState("");

  if (part.answered !== undefined) {
    return (
      <section className="atc-question atc-answered">
        <p>{q.question}</p>
        <p className="atc-status">{part.answered}</p>
      </section>
    );
  }

  const disabled = ctx.busy;
  if (q.kind === "approval") {
    return (
      <section className="atc-question" data-kind="approval">
        <p>{q.question}</p>
        <div className="atc-actions">
          <button type="button" className="atc-primary" disabled={disabled} onClick={() => ctx.answerQuestion(part.id, { approved: true }, l.approve)}>{l.approve}</button>
          <button type="button" disabled={disabled} onClick={() => ctx.answerQuestion(part.id, { approved: false }, l.reject)}>{l.reject}</button>
        </div>
      </section>
    );
  }

  const hasOptions = items.some((i) => (i.options?.length ?? 0) > 0);
  if (!hasOptions) {
    return (
      <section className="atc-question" data-kind="clarification">
        <p>{q.question}</p>
        <textarea aria-label={q.question} value={text} onChange={(e) => setText(e.target.value)} />
        <div className="atc-actions">
          <button type="button" className="atc-primary" disabled={disabled || text.trim() === ""} onClick={() => ctx.answerQuestion(part.id, { text: text.trim() }, text.trim())}>{l.answer}</button>
        </div>
      </section>
    );
  }

  const quick = items.length === 1 && items[0]!.multiSelect !== true; // una pregunta de selección única: pulsar responde
  const toggle = (qi: number, label: string) =>
    setPicked((cur) => cur.map((sel, k) => (k !== qi ? sel : items[qi]!.multiSelect ? (sel.includes(label) ? sel.filter((x) => x !== label) : [...sel, label]) : [label])));
  const submit = (answers: string[][]) => ctx.answerQuestion(part.id, { answers }, answers.map((a) => a.join(", ")).join(" · "));
  return (
    <section className="atc-question" data-kind="choice">
      {items.map((item, qi) => (
        <fieldset key={qi}>
          <legend>{item.question}</legend>
          {item.options!.map((o) => (
            <label key={o.label} className="atc-option">
              <input
                type={item.multiSelect ? "checkbox" : "radio"}
                name={`${part.id}-${qi}`}
                disabled={disabled}
                checked={picked[qi]!.includes(o.label)}
                onChange={() => (quick ? submit([[o.label]]) : toggle(qi, o.label))}
              />{" "}
              {o.label}
              {o.recommended ? <em> ★</em> : null}
              {o.description ? <small> {o.description}</small> : null}
            </label>
          ))}
        </fieldset>
      ))}
      {quick ? null : (
        <div className="atc-actions">
          <button type="button" className="atc-primary" disabled={disabled || picked.some((s) => s.length === 0)} onClick={() => submit(picked)}>{l.answer}</button>
        </div>
      )}
    </section>
  );
}
