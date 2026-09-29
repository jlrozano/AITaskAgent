using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Tool to make multiple non-contiguous edits to a file.
/// </summary>
public sealed class MultiReplaceFileContentTool : BaseFileTool
{
    public override string Name => "multi_replace_file_content";
    public override string Description => "Use this tool to edit an existing file. Follow these rules:\n1. Use this tool ONLY when you are making MULTIPLE, NON-CONTIGUOUS edits to the same file (i.e., you are changing more than one separate block of text). If you are making a single contiguous block of edits, use the replace_file_content tool instead.\n2. Do NOT use this tool if you are only editing a single contiguous block of lines.\n3. Do NOT make multiple parallel calls to this tool or the replace_file_content tool for the same file.\n4. To edit multiple, non-adjacent lines of code in the same file, make a single call to this tool. Specify each edit as a separate ReplacementChunk.\n5. For each ReplacementChunk, specify StartLine, EndLine, TargetContent and ReplacementContent. StartLine and EndLine should specify a range of lines containing precisely the instances of TargetContent that you wish to edit. To edit a single instance of the TargetContent, the range should be such that it contains that specific instance of the TargetContent and no other instances. When applicable, provide a range that matches the range viewed in a previous view_file call. In TargetContent, specify the precise lines of code to edit. These lines MUST EXACTLY MATCH text in the existing file content. In ReplacementContent, specify the replacement content for the specified target content. This must be a complete drop-in replacement of the TargetContent, with necessary modifications made.\n6. If you are making multiple edits across a single file, specify multiple separate ReplacementChunks. DO NOT try to replace the entire existing content with the new content, this is very expensive.\n7. You may not edit file extensions: [.ipynb]\nIMPORTANT: You must generate the following arguments first, before any others: [TargetFile]";
    public override string? UsageGuidelines => "Use for multiple non-adjacent edits in one file. For single block edits, use replace_file_content.";

    public override ToolDefinition GetDefinition()
    {
        return new ToolDefinition
        {
            Name = Name,
            Description = Description,
            ParametersJsonSchema = """
            {
                "type": "object",
                "properties": {
                    "ArtifactMetadata": {
                        "type": "object",
                        "description": "Metadata updates if updating an artifact file, leave blank if not updating an artifact. Should be updated if the content is changing meaningfully.",
                        "properties": {
                            "ArtifactType": {
                                "type": "string",
                                "enum": [
                                    "implementation_plan",
                                    "walkthrough",
                                    "task",
                                    "other"
                                ],
                                "description": "Type of artifact: 'implementation_plan', 'walkthrough', 'task', or 'other'."
                            },
                        "Summary": {
                                "type": "string",
                                "description": "Detailed multi-line summary of the artifact file, after edits have been made. Summary does not need to mention the artifact name and should focus on the contents and purpose of the artifact."
                            }
                        },
                        "required": [
                            "Summary",
                            "ArtifactType"
                        ]
                    },
                    "CodeMarkdownLanguage": {
                        "type": "string",
                        "description": "Markdown language for the code block, e.g 'python' or 'javascript'"
                    },
                    "Complexity": {
                        "type": "integer",
                        "description": "A 1-10 rating of how important it is for the user to review this change. Rate based on: 1-3 (routine/obvious), 4-6 (worth noting), 7-10 (critical or subtle and warrants explanation)."
                    },
                    "Description": {
                        "type": "string",
                        "description": "Brief, user-facing explanation of what this change did. Focus on non-obvious rationale, design decisions, or important context. Don't just restate what the code does."
                    },
                    "Instruction": {
                        "type": "string",
                        "description": "A description of the changes that you are making to the file."
                    },
                    "ReplacementChunks": {
                        "type": "array",
                        "description": "A list of chunks to replace. It is best to provide multiple chunks for non-contiguous edits if possible. This must be a JSON array, not a string.",
                        "items": {
                            "type": "object",
                            "properties": {
                                "AllowMultiple": {
                                    "type": "boolean",
                                    "description": "If true, multiple occurrences of 'targetContent' will be replaced by 'replacementContent' if they are found. Otherwise if multiple occurences are found, an error will be returned."
                                },
                                "EndLine": {
                                    "type": "integer",
                                    "description": "The ending line number of the chunk (1-indexed). Should be at or after the last line containing the target content. Must satisfy StartLine <= EndLine <= number of lines in the file. The target content is searched for within the [StartLine, EndLine] range."
                                },
                                "ReplacementContent": {
                                    "type": "string",
                                    "description": "The content to replace the target content with."
                                },
                                "StartLine": {
                                    "type": "integer",
                                    "description": "The starting line number of the chunk (1-indexed). Should be at or before the first line containing the target content. Must satisfy 1 <= StartLine <= EndLine. The target content is searched for within the [StartLine, EndLine] range."
                                },
                                "TargetContent": {
                                    "type": "string",
                                    "description": "The exact string to be replaced. This must be the exact character-sequence to be replaced, including whitespace. Be very careful to include any leading whitespace otherwise this will not work at all. This must be a unique substring within the file, or else it will error."
                                }
                            },
                        "required": [
                                "AllowMultiple",
                                "TargetContent",
                                "ReplacementContent",
                                "StartLine",
                                "EndLine"
                            ]
                        }
                    },
                    "TargetFile": {
                        "type": "string",
                        "description": "The target file to modify. Can be absolute or relative to project root. The '/workspace/' prefix is handled automatically."
                    },
                    "TargetLintErrorIds": {
                        "type": "array",
                        "items": {
                            "type": "string"
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
                    "ReplacementChunks"
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
        var (success, args, parseError) = TryParseArguments<Arguments>(
            argumentsJson,
            expectedSchemaHint: """{"TargetFile": "string", "ReplacementChunks": [...]}""",
            logger: logger);

        if (!success) return parseError!;

        if (args is null || string.IsNullOrWhiteSpace(args.TargetFile))
        {
            return "Error: TargetFile is required.";
        }

        // Resolve path in case it's relative
        var resolvedTargetFile = ResolvePath(args.TargetFile);

        await NotifyProgressAsync($"📝 Performing multiple replacements in file '{resolvedTargetFile}'", context, cancellationToken);

        ValidatePath(resolvedTargetFile);

        if (!File.Exists(resolvedTargetFile))
        {
            return $"Error: File {resolvedTargetFile} does not exist.";
        }

        // Reflection-based argument parsing bypasses `required`/non-nullability, so a null list
        // or a null TargetContent/ReplacementContent inside a chunk would otherwise reach
        // FileToolsHelper.ApplyChunk's .Replace(...) call and throw a NullReferenceException.
        if (args.ReplacementChunks is null || args.ReplacementChunks.Count == 0)
        {
            return "Error: ReplacementChunks is required and must contain at least one chunk.";
        }

        foreach (var invalidChunk in args.ReplacementChunks)
        {
            if (invalidChunk.TargetContent is null || invalidChunk.ReplacementContent is null)
            {
                return $"Error: TargetContent and ReplacementContent are required for the chunk " +
                       $"at lines {invalidChunk.StartLine}-{invalidChunk.EndLine}.";
            }
        }

        var (originalContent, encoding) = await FileToolsHelper.ReadWithEncodingAsync(resolvedTargetFile, cancellationToken);
        var lineEnding = FileToolsHelper.DetectLineEnding(originalContent);

        // ApplyChunk normalises \r\n → \n internally and returns \n-only content each time.
        string currentContent = originalContent;

        // Sort chunks by StartLine descending so earlier replacements don't shift
        // the line numbers of subsequent chunks.
        var sortedChunks = args.ReplacementChunks.OrderByDescending(c => c.StartLine).ToList();

        foreach (var chunk in sortedChunks)
        {
            string? result;
            try
            {
                result = FileToolsHelper.ApplyChunk(
                    currentContent,
                    chunk.TargetContent,
                    chunk.ReplacementContent,
                    chunk.StartLine,
                    chunk.EndLine,
                    chunk.AllowMultiple);
            }
            catch (InvalidOperationException ex)
            {
                return $"Error in chunk at line {chunk.StartLine}: {ex.Message}";
            }

            if (result is null)
                return $"Error: TargetContent for chunk at lines {chunk.StartLine}–{chunk.EndLine} was not found in the file.";

            currentContent = result;
        }

        if (lineEnding != "\n")
        {
            currentContent = currentContent.Replace("\n", lineEnding);
        }

        if (FileToolsHelper.DescribeUnencodable(resolvedTargetFile, currentContent, encoding) is { } encodingError)
        {
            return encodingError;
        }

        await FileToolsHelper.WriteAtomicAsync(resolvedTargetFile, currentContent, encoding, cancellationToken);
        return $"Successfully replaced {args.ReplacementChunks.Count} chunks in {resolvedTargetFile}.";
    }

    private record Arguments(
        string TargetFile,
        List<ReplacementChunk> ReplacementChunks);

    private record ReplacementChunk(
        string TargetContent,
        string ReplacementContent,
        int StartLine,
        int EndLine,
        bool AllowMultiple);
}
