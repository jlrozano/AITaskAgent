import { Virtuoso } from 'react-virtuoso';
import {
    MessageInput,
    Message,
    Avatar,
} from '@chatscope/chat-ui-kit-react';
import '@chatscope/chat-ui-kit-styles/dist/default/styles.min.css';
import { ChatMessage } from '../../types';
import { Bot, User } from 'lucide-react';

interface ChatProps {
    messages: ChatMessage[];
    onSendMessage: (message: string) => void;
    isProcessing: boolean;
}

/**
 * Chat component with virtualized message list.
 * Uses chatscope for UI bubbles and react-virtuoso for performance/scrolling.
 * Layout uses standard flex divs to avoid conflicts with ChatScope containers.
 */
export function Chat({ messages, onSendMessage, isProcessing }: ChatProps) {
    return (
        <div className="flex flex-col h-full bg-white">
            {/* Message List Area */}
            <div className="flex-1 overflow-hidden relative">
                {messages.length === 0 ? (
                    <div className="h-full flex items-center justify-center text-gray-400 p-4 text-center">
                        <p>No messages yet.<br />Start a conversation by typing below!</p>
                    </div>
                ) : (
                    <Virtuoso
                        className="h-full"
                        data={messages}
                        followOutput="smooth"
                        initialTopMostItemIndex={messages.length - 1}
                        itemContent={(_, msg) => (
                            <div className="px-4 py-2">
                                <Message
                                    model={{
                                        message: msg.content,
                                        sentTime: msg.timestamp.toLocaleTimeString(),
                                        sender: msg.role === 'user' ? 'You' : 'AI',
                                        direction: msg.role === 'user' ? 'outgoing' : 'incoming',
                                        position: 'single',
                                    }}
                                    avatarPosition={msg.role === 'user' ? 'tr' : 'tl'}
                                >
                                    <Avatar>
                                        <div className={`w-8 h-8 rounded-full flex items-center justify-center ${msg.role === 'user'
                                            ? 'bg-blue-100 text-blue-600'
                                            : msg.role === 'system'
                                                ? 'bg-red-100 text-red-600'
                                                : 'bg-purple-100 text-purple-600'
                                            }`}>
                                            {msg.role === 'user' ? (
                                                <User className="w-4 h-4" />
                                            ) : (
                                                <Bot className="w-4 h-4" />
                                            )}
                                        </div>
                                    </Avatar>
                                </Message>
                            </div>
                        )}
                    />
                )}
            </div>

            {/* Input Area */}
            <div className="border-t border-gray-200 p-4 bg-gray-50">
                <MessageInput
                    placeholder={isProcessing ? 'Processing...' : 'Type your message...'}
                    onSend={onSendMessage}
                    disabled={isProcessing}
                    attachButton={false}
                    sendButton={true}
                    className="!bg-white !rounded-xl border border-gray-300 shadow-sm"
                />
            </div>
        </div>
    );
}
