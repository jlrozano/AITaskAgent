using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to replace a contiguous block of text in a file.
/// </summary>
public sealed class ReplaceFileContentTool : BaseFileTool
{
    public override string Name => "replace_file_content";
    public override string Description => "Use this tool to edit an existing file. Follow these rules:\n1. Use this tool ONLY when you are making a SINGLE CONTIGUOUS block of edits to the same file (i.e. replacing a single contiguous block of text). If you are making edits to multiple non-adjacent lines, use the multi_replace_file_content tool instead.\n2. Do NOT make multiple parallel calls to this tool or the multi_replace_file_content tool for the same file.\n3. To edit multiple, non-adjacent lines of code in the same file, make a single call to the multi_replace_file_content \t\"toolName\": shared.MultiReplaceFileContentToolName,.\n4. For the ReplacementChunk, specify StartLine, EndLine, TargetContent and ReplacementContent. StartLine and EndLine should specify a range of lines containing precisely the instances of TargetContent that you wish to edit. To edit a single instance of the TargetContent, the range should be such that it contains that specific instance of the TargetContent and no other instances. When applicable, provide a range that matches the range viewed in a previous view_file call. In TargetContent, specify the precise lines of code to edit. These lines MUST EXACTLY MATCH text in the existing file content. In ReplacementContent, specify the replacement content for the specified target content. This must be a complete drop-in replacement of the TargetContent, with necessary modifications made.\n5. If you are making multiple edits across a single file, use the multi_replace_file_content tool instead.. DO NOT try to replace the entire existing content with the new content, this is very expensive.\n6. You may not edit file extensions: [.ipynb]\nIMPORTANT: You must generate the following arguments first, before any others: [TargetFile]";
    public override string? UsageGuidelines => "Use to edit a single contiguous block in a file. For multiple non-adjacent edits, use multi_replace_file_content.";

    public override ToolDefinition GetDefinition()
    {
        return new ToolDefinition
        {
            Name = Name,
            Description = Description,
            ParametersJsonSchema = """
            {
                "type": "OBJECT",
                "properties": {
                    "AllowMultiple": {
                        "type": "BOOLEAN",
                        "description": "If true, multiple occurrences of 'targetContent' will be replaced by 'replacementContent' if they are found. Otherwise if multiple occurences are found, an error will be returned."
                    },
                    "CodeMarkdownLanguage": {
                        "type": "STRING",
                        "description": "Markdown language for the code block, e.g 'python' or 'javascript'"
                    },
                    "Complexity": {
                        "type": "INTEGER",
                        "description": "A 1-10 rating of how important it is for the user to review this change. Rate based on: 1-3 (routine/obvious), 4-6 (worth noting), 7-10 (critical or subtle and warrants explanation)."
                    },
                    "Description": {
                        "type": "STRING",
                        "description": "Brief, user-facing explanation of what this change did. Focus on non-obvious rationale, design decisions, or important context. Don't just restate what the code does."
                    },
                    "EndLine": {
                        "type": "INTEGER",
                        "description": "The ending line number of the chunk (1-indexed). Should be at or after the last line containing the target content. Must satisfy StartLine <= EndLine <= number of lines in the file. The target content is searched for within the [StartLine, EndLine] range."
                    },
                    "Instruction": {
                        "type": "STRING",
                        "description": "A description of the changes that you are making to the file."
                    },
                    "ReplacementContent": {
                        "type": "STRING",
                        "description": "The content to replace the target content with."
                    },
                    "StartLine": {
                        "type": "INTEGER",
                        "description": "The starting line number of the chunk (1-indexed). Should be at or before the first line containing the target content. Must satisfy 1 <= StartLine <= EndLine. The target content is searched for within the [StartLine, EndLine] range."
                    },
                    "TargetContent": {
                        "type": "STRING",
                        "description": "The exact string to be replaced. This must be the exact character-sequence to be replaced, including whitespace. Be very careful to include any leading whitespace otherwise this will not work at all. This must be a unique substring within the file, or else it will error."
                    },
                    "TargetFile": {
                        "type": "STRING",
                        "description": "The target file to modify. Always specify the target file as the very first argument."
                    },
                    "TargetLintErrorIds": {
                        "type": "ARRAY",
                        "items": {
                            "type": "STRING"
                        },
                        "description": "If applicable, IDs of lint errors this edit aims to fix (they'll have been given in recent IDE feedback). If you believe the edit could fix lints, do specify lint IDs; if the edit is wholly unrelated, do not. A rule of thumb is, if your edit was influenced by lint feedback, include lint IDs. Exercise honest judgement here."
                    }
                },
                "required": [
                    "TargetFile",
                    "CodeMarkdownLanguage",
                    "Instruction",
                    "Description",
                    "Complexity",
                    "AllowMultiple",
                    "TargetContent",
                    "ReplacementContent",
                    "StartLine",
                    "EndLine"
                ]
            }
            """
        };
    }

    protected override async Task<string> ExecuteInternalAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var args = ParseArguments<Arguments>(argumentsJson);
        if (args is null || string.IsNullOrWhiteSpace(args.TargetFile))
        {
            return "Error: TargetFile is required.";
        }

        // Resolve path in case it's relative
        var resolvedTargetFile = ResolvePath(args.TargetFile);

        await NotifyProgressAsync($"📝 Replacing content in file '{resolvedTargetFile}'", context, cancellationToken);

        ValidatePath(resolvedTargetFile);

        if (!File.Exists(resolvedTargetFile))
        {
            return $"Error: File {resolvedTargetFile} does not exist.";
        }

        string content = await File.ReadAllTextAsync(resolvedTargetFile, cancellationToken);

        // Exact match replacement logic
        // Verify TargetContent exists
        if (!content.Contains(args.TargetContent))
        {
            // Fallback: try to normalize line endings
            var normalizedContent = content.Replace("\r\n", "\n");
            var normalizedTarget = args.TargetContent.Replace("\r\n", "\n");
            if (!normalizedContent.Contains(normalizedTarget))
            {
                return "Error: TargetContent not found in file (checked with normal and normalized line endings).";
            }
            content = normalizedContent.Replace(normalizedTarget, args.ReplacementContent.Replace("\r\n", "\n"));
        }
        else
        {
            content = content.Replace(args.TargetContent, args.ReplacementContent);
        }

        await File.WriteAllTextAsync(resolvedTargetFile, content, cancellationToken);

        return $"Successfully replaced content in {resolvedTargetFile}.";
    }

    private record Arguments(
        string TargetFile,
        string TargetContent,
        string ReplacementContent,
        int StartLine,
        int EndLine,
        bool AllowMultiple);
}
