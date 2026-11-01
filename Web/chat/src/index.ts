// @aitaskagent/chat: componente de chat reutilizable que consume chat-protocol/1.
export * from "./protocol.ts";
export { ChatClient, type ChatClientOptions } from "./client.ts";
export { applyTransform, type TransformEvent } from "./transform.ts";
export { chatReducer, initialState, type ChatAction, type ChatMessage, type ChatState, type Part, type PartType, type ProposalStatus } from "./state.ts";
export type { TextPart, ThinkingPart, ToolPart, AgentPart, AgentNodePart, FlowItem, GroupEntry, ToolGroupPart, AttachmentsPart, ChatAttachment, ProposalPart, QuestionPart, NoticePart, ErrorPart, UsagePart, MessageStatus } from "./state.ts";
export type { HostAdapter, ProposalOutcome } from "./host.ts";
export { useChat, type UseChat, type UseChatOptions } from "./useChat.ts";
export { createRenderers, type Renderer, type RendererContext, type RendererProps, type RendererRegistry, type NoticeRenderers } from "./registry.ts";
export { defaultRenderers } from "./renderers/index.ts";
export { defaultLabels, type Labels } from "./labels.ts";
export { ChatView, type ChatViewProps } from "./components/ChatView.tsx";
export { MessageList, type MessageListProps } from "./components/MessageList.tsx";
export { renderMarkdown } from "./markdown.tsx";
export type { MarkdownOptions, MarkdownPlugins, MarkdownComponents, MarkdownRehypePlugins } from "./markdown.tsx";
