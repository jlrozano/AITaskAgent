using AITaskAgent.FileTools.Tools;

namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// ApplyChunk is the actual matching engine behind replace_file_content and
/// multi_replace_file_content: an LLM-supplied line range plus an exact-substring match, not a
/// regex. Its edge cases (range clamping, ambiguous matches, line-ending normalisation) are the
/// ones that silently corrupt a file if wrong, so they are worth pinning down directly rather
/// than only through the tools that call it.
/// </summary>
public class FileToolsHelperTests
{
    [Fact]
    public void Replaces_the_single_match_within_the_line_range()
    {
        var content = "line1\nline2\nold text\nline4";

        var result = FileToolsHelper.ApplyChunk(content, "old text", "new text", 3, 3, allowMultiple: false);

        Assert.Equal("line1\nline2\nnew text\nline4", result);
    }

    [Fact]
    public void Returns_null_when_the_target_is_not_found_in_range()
    {
        var content = "line1\nold text\nline3";

        // "old text" exists, but outside the searched window [2, 2] which only covers "old text"
        // itself moved out of range by pointing at line 3 instead.
        var result = FileToolsHelper.ApplyChunk(content, "old text", "new text", 3, 3, allowMultiple: false);

        Assert.Null(result);
    }

    [Fact]
    public void Throws_when_target_is_ambiguous_and_AllowMultiple_is_false()
    {
        var content = "dup\ndup\ndup";

        var ex = Assert.Throws<InvalidOperationException>(
            () => FileToolsHelper.ApplyChunk(content, "dup", "x", 1, 3, allowMultiple: false));

        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Replaces_every_occurrence_in_range_when_AllowMultiple_is_true()
    {
        var content = "dup\ndup\ndup";

        var result = FileToolsHelper.ApplyChunk(content, "dup", "x", 1, 3, allowMultiple: true);

        Assert.Equal("x\nx\nx", result);
    }

    [Fact]
    public void Leaves_content_outside_the_window_untouched()
    {
        var content = "keep1\ntarget\nkeep2\ntarget\nkeep3";

        // Window [2,2] only covers the first "target"; the second, outside the window, is not a
        // candidate even though it is an exact duplicate of the string being searched for.
        var result = FileToolsHelper.ApplyChunk(content, "target", "hit", 2, 2, allowMultiple: false);

        Assert.Equal("keep1\nhit\nkeep2\ntarget\nkeep3", result);
    }

    [Fact]
    public void Normalizes_CRLF_input_to_LF_before_matching()
    {
        var content = "line1\r\nold\r\nline3";

        var result = FileToolsHelper.ApplyChunk(content, "old", "new", 2, 2, allowMultiple: false);

        Assert.Equal("line1\nnew\nline3", result);
    }

    [Fact]
    public void Clamps_a_range_past_end_of_file_instead_of_throwing()
    {
        var content = "line1\nline2";

        var result = FileToolsHelper.ApplyChunk(content, "line2", "replaced", 2, 999, allowMultiple: false);

        Assert.Equal("line1\nreplaced", result);
    }

    [Fact]
    public void Returns_null_for_a_degenerate_range_where_start_is_after_end()
    {
        var content = "line1\nline2\nline3";

        var result = FileToolsHelper.ApplyChunk(content, "line2", "x", 3, 1, allowMultiple: false);

        Assert.Null(result);
    }

    [Fact]
    public void Matching_is_literal_not_regex()
    {
        var content = "price: $5.00 (was $5x00)";

        // "$5.00" as a regex would also match "$5x00" via the unescaped '.'. ApplyChunk must not.
        var result = FileToolsHelper.ApplyChunk(content, "$5.00", "$3.00", 1, 1, allowMultiple: false);

        Assert.Equal("price: $3.00 (was $5x00)", result);
    }
}
