import type { RendererRegistry } from "../registry.ts";
import { ProposalPart } from "./ProposalPart.tsx";
import { QuestionPart } from "./QuestionPart.tsx";
import { ErrorPart, NoticePart, UsagePart } from "./StatusParts.tsx";
import { TextPart } from "./TextPart.tsx";
import { ThinkingPart } from "./ThinkingPart.tsx";
import { ToolGroupPart } from "./ToolGroupPart.tsx";
import { ToolPart } from "./ToolPart.tsx";
import { AgentPart } from "./AgentPart.tsx";
import { AttachmentsPart } from "./AttachmentsPart.tsx";

export { AgentPart, AttachmentsPart, ToolGroupPart, ProposalPart, QuestionPart, ErrorPart, NoticePart, UsagePart, TextPart, ThinkingPart, ToolPart };

/** Un renderizador por tipo de parte. Se amplía o sustituye con `createRenderers(defaultRenderers, { … })`. */
export const defaultRenderers: RendererRegistry = {
  text: TextPart,
  thinking: ThinkingPart,
  tool: ToolPart,
  toolGroup: ToolGroupPart,
  agent: AgentPart,
  attachments: AttachmentsPart,
  proposal: ProposalPart,
  question: QuestionPart,
  usage: UsagePart,
  notice: NoticePart,
  error: ErrorPart,
};
